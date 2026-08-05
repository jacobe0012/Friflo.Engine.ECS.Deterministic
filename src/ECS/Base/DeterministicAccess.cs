// Copyright (c) Ullrich Praetz - https://github.com/friflo. All rights reserved.
// See LICENSE file in the project root for full license information.

// Additive file supporting the deterministic rollback bridge (Klotho.Friflo.Bridge).
// Exposes store/archetype state that is otherwise private so a snapshot can capture and
// restore it. Nothing here changes existing behaviour: every member is a new accessor or a
// rebuild routine expressed in terms of already existing operations.
//
// Two reasons this must live inside Friflo.Engine.ECS rather than in the bridge:
//   - `EntityStore.intern` and `EntityStoreBase.archsCount` are private, so
//     [InternalsVisibleTo] alone cannot reach them. Both types are partial, so accessors can
//     be added here without editing any existing source file.
//   - Copying `StructHeap<T>.components` and re-registering indexes/relations needs the
//     component type `T`, which is only recoverable through the generic StructHeap<T>.

using System;
using System.Collections.Generic;
using Friflo.Engine.ECS.Collections;
using Friflo.Engine.ECS.Index;
using Friflo.Engine.ECS.Relations;

// ReSharper disable once CheckNamespace
namespace Friflo.Engine.ECS
{
    public partial class EntityStoreBase
    {
        /// <summary>Number of archetypes in <see cref="archs"/>. Archetypes are never removed.</summary>
        internal int ArchsCount => archsCount;

        /// <summary>Return the archetype at <paramref name="index"/> in <see cref="archs"/>.</summary>
        internal Archetype GetArchetypeAt(int index) => archs[index];
    }

    public sealed partial class EntityStore
    {
    #region store state access
        /// <summary>Incrementing id used for the next new entity. See <see cref="NewId"/>.</summary>
        internal int SequenceId {
            get => intern.sequenceId;
            set => intern.sequenceId = value;
        }

        /// <summary>
        /// Copy the recycle-id stack (ids of deleted entities, popped LIFO by <see cref="NewId"/>)
        /// into <paramref name="buffer"/>, growing it if needed. Returns the number of ids written.
        /// </summary>
        internal int GetRecycleIds(ref int[] buffer)
        {
            int count = intern.recycleIds.Count;
            if (buffer.Length < count) {
                buffer = new int[Math.Max(count, 2 * buffer.Length)];
            }
            for (int n = 0; n < count; n++) {
                buffer[n] = intern.recycleIds.GetAt(n);
            }
            return count;
        }

        /// <summary>Replace the recycle-id stack with the first <paramref name="count"/> ids of <paramref name="buffer"/>.</summary>
        internal void SetRecycleIds(int[] buffer, int count)
        {
            intern.recycleIds.Clear();
            for (int n = 0; n < count; n++) {
                intern.recycleIds.Push(buffer[n]);
            }
        }

        /// <summary>Number of all entities in the store.</summary>
        internal int EntityCountInternal {
            get => entityCount;
            set => entityCount = value;
        }

        /// <summary>Grow <see cref="nodes"/> to at least <paramref name="length"/> entries.</summary>
        internal void EnsureNodesLengthInternal(int length) => EnsureNodesLength(length);

        /// <summary>Read the deterministic fields of the node with the passed <paramref name="id"/>.</summary>
        internal void GetNodeState(int id, out int compIndex, out short revision, out int isOwner, out int isLinked)
        {
            ref var node = ref nodes[id];
            compIndex   = node.compIndex;
            revision    = node.revision;
            isOwner     = node.isOwner;
            isLinked    = node.isLinked;
        }

        /// <summary>Overwrite the deterministic fields of the node with the passed <paramref name="id"/>.</summary>
        /// <remarks>
        /// Event handler fields (<see cref="EntityNode.hasEvent"/>, <see cref="EntityNode.signalTypeCount"/>)
        /// are deliberately left untouched: they are view-layer state, not simulation state, and
        /// clearing them on rollback would silently detach a caller's handlers.
        /// </remarks>
        internal void SetNodeState(int id, Archetype archetype, int compIndex, short revision, int isOwner, int isLinked)
        {
            ref var node    = ref nodes[id];
            node.archetype  = archetype;
            node.compIndex  = compIndex;
            node.revision   = revision;
            node.isOwner    = isOwner;
            node.isLinked   = isLinked;
        }
        #endregion

    #region relation archetypes
        /// <summary>
        /// Add the archetypes holding relation components to <paramref name="result"/>, ordered by
        /// struct index so the order is stable across calls.
        /// </summary>
        /// <remarks>
        /// Relation archetypes are created by <see cref="AbstractEntityRelations"/> without going
        /// through <c>EntityStoreBase.AddArchetype</c>, so they are NOT present in
        /// <c>EntityStoreBase.archs</c> and must be enumerated separately when capturing state.
        /// </remarks>
        internal void GetRelationArchetypes(List<Archetype> result)
        {
            var relationsMap = extension.relationsMap;
            if (relationsMap == null) {
                return;
            }
            for (int structIndex = 0; structIndex < relationsMap.Length; structIndex++) {
                var relations = relationsMap[structIndex];
                if (relations != null) {
                    result.Add(relations.archetype);
                }
            }
        }
        #endregion

    #region hierarchy (parent / child)
        /// <summary>Parent id of each entity, indexed by entity id. 0 means "no parent".</summary>
        /// <remarks>
        /// The child lists live in <see cref="TreeNode"/> components (restored with the archetypes)
        /// but they hold <see cref="Collections.IdArray"/> handles into <see cref="StoreExtension.childHeap"/>,
        /// so the heap has to be rebuilt from those components. See <see cref="RebuildChildHeap"/>.
        /// </remarks>
        internal int[] GetParentMap() => extension.parentMap;

        /// <summary>Overwrite the parent map with the first <paramref name="count"/> entries of <paramref name="source"/>.</summary>
        internal void SetParentMap(int[] source, int count)
        {
            var parentMap = extension.parentMap;
            if (parentMap.Length < count) {
                extension.parentMap = parentMap = new int[count];
            }
            Array.Copy(source, parentMap, count);
            for (int id = count; id < parentMap.Length; id++) {
                parentMap[id] = Static.NoParentId;
            }
        }

        /// <summary>
        /// Rebuild <see cref="StoreExtension.childHeap"/> so every restored <see cref="TreeNode"/>
        /// owns a freshly allocated child list holding the same ids.
        /// </summary>
        /// <remarks>
        /// A <see cref="TreeNode"/> stores an <see cref="Collections.IdArray"/>, i.e. a
        /// <c>(start, count)</c> handle into a pooled allocator — <c>start</c> is an allocator
        /// offset, not data. Restoring the raw component bytes would leave those handles pointing
        /// at whatever the pool holds now. The child ids themselves were captured separately and
        /// are re-inserted here, which also re-establishes the pool's free lists.
        /// </remarks>
        internal void RebuildChildHeap(int[] childIds, int[] childStarts, int[] childCounts, int nodeCount)
        {
            var heap    = extension.childHeap;
            var treeHeap = (StructHeap<TreeNode>)null;

            // Every child list is about to be rebuilt from scratch, so all current allocations
            // become unreachable at once. Releasing them one by one is not possible: the handles
            // live in the TreeNode components being overwritten, and an entity deleted since the
            // capture no longer has a component to read its handle from. Left un-reset, the pool
            // would hand out a fresh offset per rollback and grow for the whole match.
            heap.Clear();

            for (int id = 0; id < nodeCount; id++) {
                int count = childCounts[id];
                ref var node = ref nodes[id];
                var archetype = node.archetype;
                if (archetype == null) {
                    continue;
                }
                var heapEntry = archetype.heapMap[StructInfo<TreeNode>.Index];
                if (heapEntry == null) {
                    continue;
                }
                treeHeap = (StructHeap<TreeNode>)heapEntry;
                ref var treeNode = ref treeHeap.components[node.compIndex];
                // Clear the handle before allocating: the pool was just reset, so letting
                // SetArray free the old one would decrement a count that is already zero.
                treeNode.childIds = default;
                if (count == 0) {
                    continue;
                }
                treeNode.childIds.SetArray(new ReadOnlySpan<int>(childIds, childStarts[id], count), heap);
            }
        }

        /// <summary>Number of child ids of the entity <paramref name="id"/>, or 0 if it has none.</summary>
        internal int GetChildCount(int id)
        {
            ref var node = ref nodes[id];
            var archetype = node.archetype;
            var heapEntry = archetype?.heapMap[StructInfo<TreeNode>.Index];
            if (heapEntry == null) {
                return 0;
            }
            return ((StructHeap<TreeNode>)heapEntry).components[node.compIndex].childIds.count;
        }

        /// <summary>The pool backing every <see cref="TreeNode"/> child list.</summary>
        internal Collections.IdArrayHeap GetChildHeap() => extension.childHeap;

        /// <summary>Copy the child ids of entity <paramref name="id"/> into <paramref name="target"/>.</summary>
        internal void CopyChildIds(int id, int[] target, int targetOffset)
        {
            ref var node  = ref nodes[id];
            var heapEntry = node.archetype?.heapMap[StructInfo<TreeNode>.Index];
            if (heapEntry == null) {
                return;
            }
            ref var treeNode = ref ((StructHeap<TreeNode>)heapEntry).components[node.compIndex];
            var span = treeNode.childIds.GetSpan(extension.childHeap, this);
            span.CopyTo(new Span<int>(target, targetOffset, span.Length));
        }
        #endregion

    #region scripts
        /// <summary>Number of entities holding one or more <see cref="Script"/>s.</summary>
        internal int ScriptEntityCount => extension.entityScriptCount - 1;

        /// <summary>The scripts of the <paramref name="index"/>-th script-bearing entity.</summary>
        internal void GetScriptsAt(int index, out int entityId, out Script[] scripts)
        {
            ref var entry = ref extension.entityScripts[index + 1];   // invariant: slot 0 is unused
            entityId = entry.id;
            scripts  = entry.scripts;
        }

        /// <summary>Remove every script from the store, so they can be re-added from a snapshot.</summary>
        internal void ClearScripts()
        {
            var scripts = extension.entityScripts;
            for (int n = 1; n < extension.entityScriptCount; n++) {
                scripts[n] = default;
            }
            extension.entityScriptCount = 1;
            extension.scriptMap.Clear();
        }

        /// <summary>Attach <paramref name="script"/> to the entity <paramref name="entityId"/>.</summary>
        /// <remarks>
        /// Used when restoring a snapshot. Unlike <c>Entity.AddScript</c> this raises no event:
        /// a rollback is not an application-level script addition, and re-raising would make
        /// handlers observe the same script being added many times.
        /// </remarks>
        internal void RestoreScript(int entityId, Script script)
        {
            extension.AppendScript(new Entity(this, entityId, nodes[entityId].revision), script);
        }

        /// <summary>
        /// The schema index of <paramref name="script"/>'s type — its position in
        /// <see cref="EntitySchema.Scripts"/>.
        /// </summary>
        /// <remarks>
        /// Used to name a script type on the wire. The index is assigned when the schema is built
        /// from the loaded assemblies, so two peers running the same build agree on it, and it
        /// costs one int rather than a type name.
        /// </remarks>
        internal static int GetScriptIndex(Script script)
            => Static.EntitySchema.ScriptTypeByType[script.GetType()].ScriptIndex;

        /// <summary>
        /// Create an instance of the script type registered at <paramref name="scriptIndex"/>.
        /// </summary>
        /// <remarks>
        /// The full-state path builds a whole world from bytes, so unlike a rollback — which keeps
        /// each script instance and only rewrites its fields — the receiving store may hold no
        /// instance to write into and needs a new one.
        /// </remarks>
        internal static Script CreateScript(int scriptIndex)
        {
            var scripts = Static.EntitySchema.Scripts;
            if (scriptIndex < 1 || scriptIndex >= scripts.Length) {
                throw new ArgumentOutOfRangeException(nameof(scriptIndex), scriptIndex,
                    $"no script type registered at this index. the schema holds {scripts.Length - 1}");
            }
            return scripts[scriptIndex].CreateScript();
        }
        #endregion

    #region archetype lookup
        /// <summary>
        /// Return the archetype holding exactly <paramref name="structIndices"/> and the tags in
        /// <paramref name="l0"/>..<paramref name="l3"/>, creating it if this store has not used it
        /// yet.
        /// </summary>
        /// <remarks>
        /// Used when restoring a full state produced by another peer, which may have archetypes
        /// this store has never created. All four tag words are taken: a tag index of 64 or above
        /// lives outside the first, and dropping it would rebuild the entity under different tags
        /// than the sender had, so it would match different queries.
        /// </remarks>
        internal Archetype GetArchetypeByContent(int[] structIndices, long l0, long l1, long l2, long l3)
        {
            var componentTypes = new ComponentTypes();
            foreach (var structIndex in structIndices) {
                componentTypes.bitSet.SetBit(structIndex);
            }
            var tags = new Tags();
            tags.bitSet.l0 = l0;
            tags.bitSet.l1 = l1;
            tags.bitSet.l2 = l2;
            tags.bitSet.l3 = l3;
            return GetArchetype(componentTypes, tags);
        }

        /// <summary>
        /// Return the archetype storing the relations of the component type
        /// <paramref name="structIndex"/>, creating the relation set if needed.
        /// </summary>
        internal Archetype GetRelationArchetype(int structIndex)
            => AbstractEntityRelations.GetEntityRelations(this, structIndex).archetype;
        #endregion

    #region index / relation rebuild
        /// <summary>
        /// Rebuild all component index and relation lookup tables from the current component data.
        /// </summary>
        /// <remarks>
        /// Called after a snapshot restore. The tables (<c>Dictionary&lt;TValue,IdArray&gt;</c> and the
        /// relation position maps) are pure derived data, so clearing and re-registering from the
        /// restored component values is both correct and cheaper than deep-copying reference-typed
        /// dictionaries every frame.
        /// </remarks>
        internal void RebuildIndexesAndRelations()
        {
            RebuildIndexes();
            RebuildRelations();
        }

        private void RebuildIndexes()
        {
            var indexMap = extension.indexMap;
            if (indexMap == null) {
                return;
            }
            foreach (var index in indexMap) {
                index?.ClearIndex();
            }
            // ClearIndex empties the value -> ids dictionaries, which drops the only references to
            // the id arrays those dictionaries held. The arrays live in a pooled allocator that
            // reclaims space only through an explicit delete, so without this the pool would grow
            // by one allocation per indexed value per rollback and never shrink.
            foreach (var index in indexMap) {
                index?.ClearIdHeap();
            }
            // Re-register every indexed component from its archetype's component data.
            var schema = Static.EntitySchema;
            for (int archIndex = 0; archIndex < ArchsCount; archIndex++) {
                var archetype = GetArchetypeAt(archIndex);
                int count     = archetype.entityCount;
                if (count == 0) {
                    continue;
                }
                // All four words: struct indices run to 256, so intersecting only the first would
                // silently skip any indexed component numbered 64 or above. Its values would
                // restore correctly while its index kept entries from the abandoned timeline —
                // visible only as queries returning the wrong entities, never as a hash mismatch.
                var indexTypes = new ComponentTypes();
                var archBits   = archetype.componentTypes.bitSet;
                var indexBits  = schema.indexTypes.bitSet;
                indexTypes.bitSet.l0 = archBits.l0 & indexBits.l0;
                indexTypes.bitSet.l1 = archBits.l1 & indexBits.l1;
                indexTypes.bitSet.l2 = archBits.l2 & indexBits.l2;
                indexTypes.bitSet.l3 = archBits.l3 & indexBits.l3;
                foreach (var componentType in indexTypes) {
                    var heap = archetype.heapMap[componentType.StructIndex];
                    heap.AddIndexRange(this, archetype.entityIds, count);
                }
            }
        }

        private void RebuildRelations()
        {
            var relationsMap = extension.relationsMap;
            if (relationsMap == null) {
                return;
            }
            foreach (var relations in relationsMap) {
                relations?.RebuildLookup();
            }
        }
        #endregion
    }

    internal partial struct StackArray<T>
    {
        /// <summary>Read the item at <paramref name="index"/> counted from the bottom of the stack.</summary>
        internal readonly T GetAt(int index) => items[index];
    }

    public sealed partial class Archetype
    {
        /// <summary>Index of this archetype in <see cref="EntityStoreBase.archs"/>.</summary>
        /// <remarks>
        /// Meaningless for relation archetypes, which are not registered in <c>archs</c>.
        /// See <see cref="EntityStore.GetRelationArchetypes"/>.
        /// </remarks>
        internal int GetArchIndex() => archIndex;

        /// <summary>Number of struct heaps (component types) managed by this archetype.</summary>
        internal int GetHeapCount() => structHeaps.Length;

        /// <summary>Backing entity id array. Only the first <see cref="Count"/> entries are live.</summary>
        internal int[] GetEntityIds() => entityIds;

        /// <summary>Set the number of entities stored in this archetype.</summary>
        internal void SetEntityCount(int count) => entityCount = count;

        /// <summary>Overwrite the first <paramref name="count"/> entity ids from <paramref name="source"/>.</summary>
        internal void SetEntityIds(int[] source, int count) => Array.Copy(source, entityIds, count);

        /// <summary>Copy the first <paramref name="count"/> components of heap <paramref name="heapIndex"/> into <paramref name="buffer"/>.</summary>
        internal void CopyHeapComponentsTo(int heapIndex, ref Array buffer, int count)
            => structHeaps[heapIndex].CopyComponentsTo(ref buffer, count);

        /// <summary>Restore the first <paramref name="count"/> components of heap <paramref name="heapIndex"/> from <paramref name="buffer"/>.</summary>
        internal void RestoreHeapComponentsFrom(int heapIndex, Array buffer, int count)
            => structHeaps[heapIndex].RestoreComponentsFrom(buffer, count);

        /// <summary>Stable component type id of heap <paramref name="heapIndex"/>.</summary>
        internal int GetHeapStructIndex(int heapIndex) => structHeaps[heapIndex].structIndex;

        /// <summary>Deterministic hash of the component at <paramref name="slot"/> in heap <paramref name="heapIndex"/>.</summary>
        /// <remarks>
        /// <see cref="TreeNode"/> is hashed by its child ids rather than its bytes. It stores an
        /// <see cref="Collections.IdArray"/> whose <c>start</c> is an offset into a pooled
        /// allocator, so two peers holding the very same hierarchy can carry different offsets
        /// purely because of allocation history — hashing the bytes would report a desync that
        /// does not exist.
        /// </remarks>
        internal ulong HashHeapComponent(int heapIndex, int slot)
        {
            var heap = structHeaps[heapIndex];
            if (heap.structIndex == StructInfo<TreeNode>.Index) {
                return HashTreeNode(((StructHeap<TreeNode>)heap).components[slot]);
            }
            return heap.HashComponent(slot);
        }

        /// <summary>Hash a <see cref="TreeNode"/> by the child ids it references, in order.</summary>
        private ulong HashTreeNode(in TreeNode treeNode)
        {
            const ulong prime = 1099511628211;
            ulong hash = 14695981039346656037;
            var   span = treeNode.childIds.GetSpan(entityStore.GetChildHeap(), entityStore);
            // Child order is meaningful (InsertChild positions matter), so it is hashed in order.
            hash ^= (ulong)(uint)span.Length;
            hash *= prime;
            foreach (var childId in span) {
                hash ^= (ulong)(uint)childId;
                hash *= prime;
            }
            return hash;
        }

        /// <summary>All four words of this archetype's tag bits.</summary>
        /// <remarks>
        /// Tag indices are allocated per schema across every assembly it sees, so the 64th tag
        /// declared anywhere lands outside the first word. Returning only <c>l0</c> would make
        /// every higher tag invisible to the state hash and absent from a serialized world — two
        /// peers disagreeing about one would agree on the hash and never report the desync.
        /// </remarks>
        internal void GetTagBits(out long l0, out long l1, out long l2, out long l3)
        {
            var bitSet = tags.bitSet;
            l0 = bitSet.l0;
            l1 = bitSet.l1;
            l2 = bitSet.l2;
            l3 = bitSet.l3;
        }

        /// <summary>Raw bytes of the first <paramref name="count"/> components of heap <paramref name="heapIndex"/>.</summary>
        internal byte[] GetHeapComponentBytes(int heapIndex, int count)
            => structHeaps[heapIndex].GetComponentBytes(count);

        /// <summary>Restore the first <paramref name="count"/> components of heap <paramref name="heapIndex"/> from raw bytes.</summary>
        internal void SetHeapComponentBytes(int heapIndex, byte[] bytes, int count)
            => structHeaps[heapIndex].SetComponentBytes(bytes, count, entityStore);
    }

    internal abstract partial class StructHeap
    {
        /// <summary>Copy the first <paramref name="count"/> components into <paramref name="buffer"/>, growing it if needed.</summary>
        internal abstract void CopyComponentsTo(ref Array buffer, int count);

        /// <summary>Restore the first <paramref name="count"/> components from <paramref name="buffer"/>.</summary>
        internal abstract void RestoreComponentsFrom(Array buffer, int count);

        /// <summary>Register the first <paramref name="count"/> components of this heap in their component index.</summary>
        internal abstract void AddIndexRange(EntityStore store, int[] ids, int count);

        /// <summary>Deterministic hash of the component stored at <paramref name="compIndex"/>.</summary>
        internal abstract ulong HashComponent(int compIndex);

        /// <summary>Raw bytes of the first <paramref name="count"/> components.</summary>
        internal abstract byte[] GetComponentBytes(int count);

        /// <summary>
        /// Restore the first <paramref name="count"/> components from raw bytes, binding any
        /// entity references to <paramref name="store"/>.
        /// </summary>
        internal abstract void SetComponentBytes(byte[] bytes, int count, EntityStore store);
    }

    internal sealed partial class StructHeap<T>
    {
        internal override void CopyComponentsTo(ref Array buffer, int count)
        {
            if (buffer is not T[] typed || typed.Length < count) {
                typed  = new T[Math.Max(count, 8)];
                buffer = typed;
            }
            Array.Copy(components, typed, count);
        }

        internal override void RestoreComponentsFrom(Array buffer, int count)
        {
            Array.Copy((T[])buffer, components, count);
        }

        internal override void AddIndexRange(EntityStore store, int[] ids, int count)
        {
            for (int n = 0; n < count; n++) {
                StoreIndex.AddIndex(store, ids[n], components[n]);
            }
        }

        /// <summary>
        /// Hash the raw bytes of the component value. Byte-level hashing is what makes the result
        /// reproducible across platforms: it depends only on the value's memory representation,
        /// not on a <c>GetHashCode</c> implementation (which is randomized for strings and
        /// unspecified for most structs).
        /// </summary>
        /// <exception cref="NotSupportedException">
        /// The component type contains references. Such a component cannot participate in a
        /// deterministic simulation: its bytes are addresses that differ per run and per peer.
        /// </exception>
        internal override ulong HashComponent(int compIndex)
        {
            if (DeterministicLayout<T>.HasReferences) {
                // Component holds an Entity: hash its (Id, Revision) identity, never the
                // process-local store reference. See DeterministicComponentLayout.cs.
                return DeterministicLayout<T>.Hash(components[compIndex]);
            }
            var value = components[compIndex];
            var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref value, 1));

            ulong hash = 14695981039346656037;
            foreach (var b in bytes) {
                hash ^= b;
                hash *= 1099511628211;
            }
            return hash;
        }

        internal override byte[] GetComponentBytes(int count)
        {
            if (DeterministicLayout<T>.HasReferences) {
                // Serialize the deterministic leaves instead of raw memory: the bytes of an
                // Entity's store reference are meaningless to the receiving peer.
                int   leafCount = DeterministicLayout<T>.LeafCount;
                var   leaves    = new ulong[leafCount * count];
                for (int n = 0; n < count; n++) {
                    DeterministicLayout<T>.Write(components[n], leaves, n * leafCount);
                }
                return System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                    new ReadOnlySpan<ulong>(leaves)).ToArray();
            }
            var source = System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                new ReadOnlySpan<T>(components, 0, count));
            return source.ToArray();
        }

        internal override void SetComponentBytes(byte[] bytes, int count, EntityStore store)
        {
            if (DeterministicLayout<T>.HasReferences) {
                int leafCount = DeterministicLayout<T>.LeafCount;
                var leaves    = new ulong[leafCount * count];
                new ReadOnlySpan<byte>(bytes).CopyTo(
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(new Span<ulong>(leaves)));
                for (int n = 0; n < count; n++) {
                    DeterministicLayout<T>.Read(ref components[n], leaves, n * leafCount, store);
                }
                return;
            }
            var target = System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                new Span<T>(components, 0, count));
            new ReadOnlySpan<byte>(bytes).CopyTo(target);
        }
    }

}

namespace Friflo.Engine.ECS.Index
{
    public abstract partial class AbstractComponentIndex
    {
        /// <summary>Remove all entries so the index can be rebuilt from component data.</summary>
        internal abstract void ClearIndex();

        /// <summary>
        /// Drop every id-array allocation backing this index.
        /// </summary>
        /// <remarks>
        /// Must be called only together with <see cref="ClearIndex"/>, which discards the handles
        /// into this pool. Defined here rather than per index type because
        /// <see cref="AbstractComponentIndex.idHeap"/> is common to all of them.
        /// </remarks>
        internal void ClearIdHeap() => idHeap.Clear();
    }
}

namespace Friflo.Engine.ECS.Relations
{
    internal abstract partial class AbstractEntityRelations
    {
        /// <summary>
        /// Rebuild the relation lookup tables (<see cref="positionMap"/>, and for link relations
        /// <see cref="linkEntityMap"/>) from the relation archetype's restored entity ids.
        /// </summary>
        /// <remarks>
        /// The relation data itself lives in <see cref="archetype"/> and is restored by the snapshot;
        /// only these id-to-position maps are derived state.
        /// </remarks>
        internal void RebuildLookup()
        {
            positionMap.Clear();
            // The cleared dictionaries held the only handles into these pools, so their
            // allocations are now unreachable and can only be reclaimed wholesale. Skipping this
            // leaks one allocation per relation-holding entity, per rollback, for the whole match.
            idHeap.Clear();
            if (linkEntityMap != null) {
                // Only link relations maintain this map, and only they construct linkIdsHeap -
                // on any other relation it is a default IdArrayHeap with no pools to clear.
                linkEntityMap.Clear();
                linkIdsHeap.Clear();
            }

            int count   = archetype.entityCount;
            var ids     = archetype.entityIds;
            var nodes   = store.nodes;
            for (int position = 0; position < count; position++) {
                int id = ids[position];
                positionMap.TryGetValue(id, out var positions);
                positions.Add(position, idHeap);
                positionMap[id] = positions;
                nodes[id].isOwner |= relationBit;
            }
            RebuildLinkLookup(count);
        }

        /// <summary>Rebuild <see cref="linkEntityMap"/>. Only link relations maintain it.</summary>
        internal virtual void RebuildLinkLookup(int count) { }
    }
}
