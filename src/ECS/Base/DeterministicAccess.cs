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
        /// through <see cref="AddArchetype"/>, so they are NOT present in <see cref="archs"/> and
        /// must be enumerated separately when capturing state.
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
        /// <summary>
        /// Return the archetype holding exactly <paramref name="structIndices"/> and
        /// <paramref name="tagBits"/>, creating it if this store has not used it yet.
        /// </summary>
        /// <remarks>
        /// Used when restoring a full state produced by another peer, which may have archetypes
        /// this store has never created.
        /// </remarks>
        internal Archetype GetArchetypeByContent(int[] structIndices, long tagBits)
        {
            var componentTypes = new ComponentTypes();
            foreach (var structIndex in structIndices) {
                componentTypes.bitSet.SetBit(structIndex);
            }
            var tags = new Tags();
            tags.bitSet.l0 = tagBits;
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
            // Re-register every indexed component from its archetype's component data.
            var schema = Static.EntitySchema;
            for (int archIndex = 0; archIndex < ArchsCount; archIndex++) {
                var archetype = GetArchetypeAt(archIndex);
                int count     = archetype.entityCount;
                if (count == 0) {
                    continue;
                }
                var indexTypes = new ComponentTypes();
                indexTypes.bitSet.l0 = archetype.componentTypes.bitSet.l0 & schema.indexTypes.bitSet.l0;
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
        internal ulong HashHeapComponent(int heapIndex, int slot)
            => structHeaps[heapIndex].HashComponent(slot);

        /// <summary>Tag bits of this archetype.</summary>
        internal long GetTagBits() => tags.bitSet.l0;

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
            linkEntityMap?.Clear();

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
