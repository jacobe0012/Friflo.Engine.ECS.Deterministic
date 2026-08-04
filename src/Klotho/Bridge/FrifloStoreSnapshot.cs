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
/// <para><b>Not covered</b> (see <see cref="FrifloSimulation"/> contract): parent/child hierarchy
/// and <c>Script</c>s. A simulation store must not use <c>AddChild</c> or scripts.</para>
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

    /// <summary>Scratch list of relation archetypes; reused so capture stays allocation-free.</summary>
    private readonly List<Archetype> relationArchetypes = new();

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

    // ------------------------------------------------------------------ capture

    /// <summary>Capture the current store state, overwriting any previously captured state.</summary>
    internal void Capture()
    {
        CaptureArchetypes();
        CaptureRelationArchetypes();
        CaptureNodes();
        HasState = true;
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
        RebuildLookupTables();
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
        store.SetRecycleIds(recycleIds, recycleCount);
        store.EntityCountInternal = entityCount;
    }

    /// <summary>
    /// Layer 4 — rebuild index and relation lookup tables from the restored component data.
    /// </summary>
    private void RebuildLookupTables()
    {
        store.RebuildIndexesAndRelations();
    }
}
