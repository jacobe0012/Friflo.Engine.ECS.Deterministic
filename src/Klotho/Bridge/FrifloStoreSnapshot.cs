using System;
using System.Collections.Generic;
using Friflo.Engine.ECS;

// ReSharper disable once CheckNamespace
namespace Klotho.Friflo;

/// <summary>
/// Captures and restores the full deterministic state of an <see cref="EntityStore"/> so a
/// simulation can be rolled back and re-simulated bit-identically.
/// </summary>
/// <remarks>
/// State is stored in three layers, with a fourth rebuilt on restore:
/// <list type="number">
///   <item>Component data — each archetype's <c>StructHeap&lt;T&gt;.components</c>, bulk-copied.</item>
///   <item>Entity structure — each archetype's <c>entityIds</c> and <c>entityCount</c>.</item>
///   <item>Store state — <c>nodes[]</c> (archetype stored as an index, never a reference),
///         <c>compIndex</c>, <c>revision</c>, <c>isOwner</c>/<c>isLinked</c>, plus
///         <c>sequenceId</c>, the recycle-id stack and the entity count.</item>
///   <item>Lookup tables — index and relation dictionaries, rebuilt from layer 1 on restore
///         rather than deep-copied. They are pure derived data, so rebuilding makes
///         "table disagrees with data" structurally impossible and keeps the per-frame path
///         allocation-free. Cost is one O(entities) pass, paid only on rollback.</item>
/// </list>
/// Buffers are reused across captures, so a steady-state capture allocates nothing.
///
/// <para><b>Relation archetypes.</b> Relation data lives in an <see cref="Archetype"/> that is
/// <i>not</i> registered in <c>store.archs[]</c> — <c>AbstractEntityRelations</c> constructs it
/// directly, bypassing <c>AddArchetype</c>. They are therefore captured through a separate list
/// obtained from <c>store.extension.relationsMap</c>. Entity nodes never reference a relation
/// archetype, so layer 3 only ever resolves indices into <c>store.archs[]</c>.</para>
///
/// <para><b>Hierarchy and scripts</b> are captured too, but neither fits the layers above:
/// <c>parentMap</c> is a plain side array, while a <c>TreeNode</c>'s child list is an
/// <c>IdArray</c> handle into a pooled allocator — the ids are captured separately and the pool
/// is rebuilt on restore. Scripts are class instances, so their deterministic field values are
/// captured and written back into the same instances.</para>
/// </remarks>
internal sealed class FrifloStoreSnapshot
{
    private readonly EntityStore store;

    /// <summary>Per-archetype captured state, indexed by <c>archIndex</c> for regular archetypes.</summary>
    private ArchetypeState[] archStates = new ArchetypeState[8];
    private int              archStateCount;

    /// <summary>Relation archetypes, captured in a stable order (see <see cref="CollectRelationArchetypes"/>).</summary>
    private ArchetypeState[] relationStates = new ArchetypeState[4];
    private int              relationStateCount;

    // --- layer 3: store state
    private NodeState[] nodeStates = new NodeState[128];
    private int         nodeCount;
    private int         sequenceId;
    private int[]       recycleIds = new int[8];
    private int         recycleCount;
    private int         entityCount;
    private bool        recycleIdsEnabled;
    private int         storeRootId;
    private short       storeRootRevision;

    /// <summary>Scratch list of relation archetypes; reused so capture stays allocation-free.</summary>
    private readonly List<Archetype> relationArchetypes = new();

    // --- hierarchy
    /// <summary>Parent id per entity id.</summary>
    private int[] parentMap   = new int[128];
    private int   parentCount;
    /// <summary>Child ids of every entity, packed back to back.</summary>
    private int[] childIds    = new int[64];
    /// <summary>Where entity id's children start in <see cref="childIds"/>.</summary>
    private int[] childStarts = new int[128];
    /// <summary>How many children entity id has.</summary>
    private int[] childCounts = new int[128];

    // --- scripts
    /// <summary>One entry per script instance found in the store.</summary>
    private ScriptState[] scriptStates = new ScriptState[8];
    private int           scriptStateCount;

    /// <summary>True once <see cref="Capture"/> has run at least once.</summary>
    internal bool HasState { get; private set; }

    internal FrifloStoreSnapshot(EntityStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>Per-archetype component data and entity structure (layers 1 and 2).</summary>
    private struct ArchetypeState
    {
        /// <summary>Archetype this state belongs to. Archetypes are never destroyed, so this stays valid.</summary>
        internal Archetype   archetype;
        /// <summary>One buffer per struct heap, parallel to <c>archetype.structHeaps</c>.</summary>
        internal Array[]     componentBuffers;
        internal int[]       entityIds;
        internal int         entityCount;
    }

    /// <summary>The parts of an <see cref="EntityNode"/> that make up deterministic state (layer 3).</summary>
    private struct NodeState
    {
        /// <summary>Index into <c>store.archs[]</c>, or -1 when the node holds no entity.</summary>
        internal int   archIndex;
        internal int   compIndex;
        internal short revision;
        internal int   isOwner;
        internal int   isLinked;
    }

    /// <summary>A script instance and the values of its deterministic fields.</summary>
    private struct ScriptState
    {
        /// <summary>Entity the script is attached to.</summary>
        internal int      entityId;
        /// <summary>
        /// The live instance. Scripts are reference types, so the snapshot keeps the identity and
        /// restores the field values into it — replacing the object would invalidate references
        /// the game holds.
        /// </summary>
        internal Script   script;
        /// <summary>Deterministic field values of <see cref="script"/>.</summary>
        internal ulong[]  leaves;
    }

    // ------------------------------------------------------------------ capture

    /// <summary>Capture the current store state, overwriting any previously captured state.</summary>
    internal void Capture()
    {
        CaptureArchetypes();
        CaptureRelationArchetypes();
        CaptureNodes();
        CaptureHierarchy();
        CaptureScripts();
        HasState = true;
    }

    /// <summary>
    /// Capture the parent map and every entity's child ids.
    /// </summary>
    /// <remarks>
    /// The child ids are captured as plain ints rather than by copying the <c>TreeNode</c>
    /// components: those hold <c>IdArray</c> handles into a pooled allocator, whose offsets are
    /// allocation history rather than state.
    /// </remarks>
    private void CaptureHierarchy()
    {
        int nodes = store.Capacity;
        var source = store.GetParentMap();
        if (parentMap.Length < nodes) {
            Array.Resize(ref parentMap, nodes);
        }
        parentCount = Math.Min(source.Length, nodes);
        Array.Copy(source, parentMap, parentCount);
        for (int id = parentCount; id < nodes; id++) {
            parentMap[id] = 0;      // Static.NoParentId
        }
        parentCount = nodes;

        if (childStarts.Length < nodes) {
            Array.Resize(ref childStarts, nodes);
            Array.Resize(ref childCounts, nodes);
        }
        int total = 0;
        for (int id = 0; id < nodes; id++) {
            int count      = store.GetChildCount(id);
            childStarts[id] = total;
            childCounts[id] = count;
            total          += count;
        }
        if (childIds.Length < total) {
            Array.Resize(ref childIds, Math.Max(total, 2 * childIds.Length));
        }
        for (int id = 0; id < nodes; id++) {
            if (childCounts[id] > 0) {
                store.CopyChildIds(id, childIds, childStarts[id]);
            }
        }
    }

    /// <summary>Capture every script instance and its deterministic field values.</summary>
    private void CaptureScripts()
    {
        int count = store.ScriptEntityCount;
        int index = 0;
        for (int n = 0; n < count; n++) {
            store.GetScriptsAt(n, out int entityId, out var scripts);
            foreach (var script in scripts) {
                if (scriptStates.Length == index) {
                    Array.Resize(ref scriptStates, Math.Max(4, 2 * index));
                }
                ref var state  = ref scriptStates[index++];
                state.entityId = entityId;
                state.script   = script;
                ScriptLayout.Capture(script, ref state.leaves);
            }
        }
        scriptStateCount = index;
    }

    private void CaptureArchetypes()
    {
        int count = store.ArchsCount;
        if (archStates.Length < count) {
            Array.Resize(ref archStates, Math.Max(count, 2 * archStates.Length));
        }
        for (int i = 0; i < count; i++) {
            CaptureArchetype(ref archStates[i], store.GetArchetypeAt(i));
        }
        archStateCount = count;
    }

    private void CaptureRelationArchetypes()
    {
        CollectRelationArchetypes();
        int count = relationArchetypes.Count;
        if (relationStates.Length < count) {
            Array.Resize(ref relationStates, Math.Max(count, 2 * relationStates.Length));
        }
        for (int i = 0; i < count; i++) {
            CaptureArchetype(ref relationStates[i], relationArchetypes[i]);
        }
        relationStateCount = count;
    }

    /// <summary>
    /// Fill <paramref name="relationArchetypes"/> with the relation archetypes of the store,
    /// ordered by struct index so capture and restore always agree on the order.
    /// </summary>
    private void CollectRelationArchetypes()
    {
        relationArchetypes.Clear();
        store.GetRelationArchetypes(relationArchetypes);
    }

    private static void CaptureArchetype(ref ArchetypeState state, Archetype archetype)
    {
        state.archetype = archetype;
        int count       = archetype.Count;
        state.entityCount = count;

        // --- layer 2: entity ids
        var ids = archetype.GetEntityIds();
        if (state.entityIds == null || state.entityIds.Length < count) {
            state.entityIds = new int[Math.Max(count, 8)];
        }
        Array.Copy(ids, state.entityIds, count);

        // --- layer 1: component data
        int heapCount = archetype.GetHeapCount();
        if (state.componentBuffers == null || state.componentBuffers.Length < heapCount) {
            Array.Resize(ref state.componentBuffers, heapCount);
        }
        for (int h = 0; h < heapCount; h++) {
            archetype.CopyHeapComponentsTo(h, ref state.componentBuffers[h], count);
        }
    }

    private void CaptureNodes()
    {
        int count = store.Capacity;
        if (nodeStates.Length < count) {
            Array.Resize(ref nodeStates, Math.Max(count, 2 * nodeStates.Length));
        }
        for (int id = 0; id < count; id++) {
            ref var node  = ref nodeStates[id];
            var archetype = store.GetEntityNode(id).Archetype;
            node.archIndex = archetype?.GetArchIndex() ?? -1;
            store.GetNodeState(id, out node.compIndex, out node.revision, out node.isOwner, out node.isLinked);
        }
        nodeCount    = count;
        sequenceId   = store.SequenceId;
        recycleCount = store.GetRecycleIds(ref recycleIds);
        entityCount  = store.Count;
        recycleIdsEnabled = store.RecycleIds;
        store.GetStoreRootState(out storeRootId, out storeRootRevision);
    }

    // ------------------------------------------------------------------ restore

    /// <summary>Restore the store to the last captured state.</summary>
    internal void Restore()
    {
        if (!HasState) {
            throw new InvalidOperationException("no snapshot captured");
        }
        RestoreArchetypes();
        RestoreNodes();
        RestoreHierarchy();
        RestoreScripts();
        RebuildLookupTables();
        // Events describe operations, not the restored state. Anything recorded since this
        // snapshot belongs to the abandoned branch and must not remain observable after Rollback.
        store.ClearRecordedEvents();
    }

    /// <summary>Restore the parent map and rebuild every child list from the captured ids.</summary>
    private void RestoreHierarchy()
    {
        store.SetParentMap(parentMap, parentCount);
        // Must run after RestoreNodes: it walks nodes to find each entity's TreeNode component.
        store.RebuildChildHeap(childIds, childStarts, childCounts, parentCount);
    }

    /// <summary>
    /// Restore script attachment and the field values of each script instance.
    /// </summary>
    /// <remarks>
    /// Scripts are re-attached from scratch because a rollback can change which entity owns which
    /// script. Field values are written back into the original instances, so references the game
    /// holds to a script stay valid across a rollback.
    /// </remarks>
    private void RestoreScripts()
    {
        store.ClearScripts();
        for (int i = 0; i < scriptStateCount; i++) {
            ref var state = ref scriptStates[i];
            ScriptLayout.Restore(state.script, state.leaves, store);
            store.RestoreScript(state.entityId, state.script);
        }
    }

    private void RestoreArchetypes()
    {
        for (int i = 0; i < archStateCount; i++) {
            RestoreArchetype(ref archStates[i]);
        }
        // Archetypes created after the capture hold entities that must not survive the rollback.
        for (int i = archStateCount; i < store.ArchsCount; i++) {
            store.GetArchetypeAt(i).SetEntityCount(0);
        }
        for (int i = 0; i < relationStateCount; i++) {
            RestoreArchetype(ref relationStates[i]);
        }
        // Relation types first used after the capture: clear anything they accumulated.
        CollectRelationArchetypes();
        for (int i = 0; i < relationArchetypes.Count; i++) {
            var archetype = relationArchetypes[i];
            if (!IsCapturedRelation(archetype)) {
                archetype.SetEntityCount(0);
            }
        }
    }

    private bool IsCapturedRelation(Archetype archetype)
    {
        for (int i = 0; i < relationStateCount; i++) {
            if (ReferenceEquals(relationStates[i].archetype, archetype)) return true;
        }
        return false;
    }

    private static void RestoreArchetype(ref ArchetypeState state)
    {
        var archetype = state.archetype;
        int count     = state.entityCount;

        archetype.EnsureCapacity(count);
        archetype.SetEntityIds(state.entityIds, count);

        int heapCount = archetype.GetHeapCount();
        for (int h = 0; h < heapCount; h++) {
            archetype.RestoreHeapComponentsFrom(h, state.componentBuffers[h], count);
        }
        archetype.SetEntityCount(count);
    }

    private void RestoreNodes()
    {
        store.EnsureNodesLengthInternal(nodeCount);
        for (int id = 0; id < nodeCount; id++) {
            ref var node     = ref nodeStates[id];
            var     archetype = node.archIndex < 0 ? null : store.GetArchetypeAt(node.archIndex);
            store.SetNodeState(id, archetype, node.compIndex, node.revision, node.isOwner, node.isLinked);
        }
        // Nodes created after the capture: clear them so their ids read as free again.
        for (int id = nodeCount; id < store.Capacity; id++) {
            store.SetNodeState(id, null, 0, 0, 0, 0);
        }
        store.SequenceId          = sequenceId;
        store.RecycleIds          = recycleIdsEnabled;
        store.SetRecycleIds(recycleIds, recycleCount);
        store.EntityCountInternal = entityCount;
        store.SetStoreRootState(storeRootId, storeRootRevision);
    }

    /// <summary>
    /// Layer 4 — rebuild index and relation lookup tables from the restored component data.
    /// </summary>
    private void RebuildLookupTables()
    {
        store.RebuildIndexesAndRelations();
    }
}
