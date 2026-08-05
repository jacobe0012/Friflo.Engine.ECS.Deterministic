using System;
using System.Collections.Generic;
using System.IO;
using Friflo.Engine.ECS;

// ReSharper disable once CheckNamespace
namespace Klotho.Friflo;

/// <summary>
/// Serializes the full deterministic state of an <see cref="EntityStore"/> to a byte array and
/// back, for Klotho late-join and resync.
/// </summary>
/// <remarks>
/// Unlike <see cref="FrifloStoreSnapshot"/> — which restores into the <i>same</i> store and can
/// therefore reference archetypes by index — this format must be readable by a store that has
/// not yet created the same archetypes. Archetypes are identified by the set of component
/// struct indices and tag bits they hold, and are recreated on the receiving side.
///
/// Component payloads are raw value bytes, matching the hashing rule in
/// <c>StructHeap&lt;T&gt;.HashComponent</c>: components must be unmanaged value types.
///
/// <para>The parent/child hierarchy and <see cref="Script"/>s need their own sections, for the
/// same reasons <see cref="FrifloStoreSnapshot"/> handles them separately: a <c>TreeNode</c>'s
/// child list is an <c>IdArray</c> handle into a pooled allocator (writing those raw bytes would
/// hand the receiver offsets into a pool that never made the matching allocation), and scripts are
/// class instances living outside the archetypes. Both are therefore written by content and
/// rebuilt on read.</para>
///
/// This runs at join/resync time, not per tick, so it allocates freely.
/// </remarks>
internal sealed class FrifloStateSerializer
{
    /// <summary>
    /// Version 2 added the hierarchy and script sections: a peer reading version 1 would silently
    /// build a world with no scripts and a broken child pool. Version 3 widened the archetype tag
    /// field from one 64-bit word to all four, so a peer mixing the two would misread every
    /// archetype header from that field onwards.
    /// </summary>
    private const int FormatVersion = 3;

    private readonly EntityStore store;

    internal FrifloStateSerializer(EntityStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    // ------------------------------------------------------------------ write

    /// <summary>Serialize the whole store, tagging it with <paramref name="currentTick"/>.</summary>
    internal byte[] Serialize(int currentTick)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);

        writer.Write(FormatVersion);
        writer.Write(currentTick);
        writer.Write(store.SequenceId);
        writer.Write(store.Count);
        writer.Write(store.Capacity);

        WriteRecycleIds(writer);
        WriteArchetypes(writer);
        WriteRelations(writer);
        WriteNodes(writer);
        WriteHierarchy(writer);
        WriteScripts(writer);

        writer.Flush();
        return buffer.ToArray();
    }

    private void WriteRecycleIds(BinaryWriter writer)
    {
        var ids   = Array.Empty<int>();
        int count = store.GetRecycleIds(ref ids);
        writer.Write(count);
        for (int n = 0; n < count; n++) {
            writer.Write(ids[n]);
        }
    }

    private void WriteArchetypes(BinaryWriter writer)
    {
        // Archetype 0 is the default archetype, always present in every store.
        int count = store.ArchsCount;
        writer.Write(count);
        for (int archIndex = 0; archIndex < count; archIndex++) {
            WriteArchetype(writer, store.GetArchetypeAt(archIndex));
        }
    }

    private void WriteRelations(BinaryWriter writer)
    {
        var relationArchetypes = new List<Archetype>();
        store.GetRelationArchetypes(relationArchetypes);
        writer.Write(relationArchetypes.Count);
        foreach (var archetype in relationArchetypes) {
            WriteArchetype(writer, archetype);
        }
    }

    private static void WriteArchetype(BinaryWriter writer, Archetype archetype)
    {
        int entityCount = archetype.Count;
        int heapCount   = archetype.GetHeapCount();

        // Identify the archetype by content, not by index: the receiving store may not have it.
        // All four tag words travel: an index of 64 or above lives outside the first one, and a
        // dropped tag changes which queries match the entity on the receiving side.
        archetype.GetTagBits(out long tag0, out long tag1, out long tag2, out long tag3);
        writer.Write(tag0);
        writer.Write(tag1);
        writer.Write(tag2);
        writer.Write(tag3);
        writer.Write(heapCount);
        for (int h = 0; h < heapCount; h++) {
            writer.Write(archetype.GetHeapStructIndex(h));
        }
        writer.Write(entityCount);
        var entityIds = archetype.GetEntityIds();
        for (int n = 0; n < entityCount; n++) {
            writer.Write(entityIds[n]);
        }
        for (int h = 0; h < heapCount; h++) {
            var bytes = archetype.GetHeapComponentBytes(h, entityCount);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }
    }

    private void WriteNodes(BinaryWriter writer)
    {
        int capacity = store.Capacity;
        for (int id = 0; id < capacity; id++) {
            var archetype = store.GetEntityNode(id).Archetype;
            store.GetNodeState(id, out int compIndex, out short revision, out int isOwner, out int isLinked);
            writer.Write(archetype?.GetArchIndex() ?? -1);
            writer.Write(compIndex);
            writer.Write(revision);
            writer.Write(isOwner);
            writer.Write(isLinked);
        }
    }

    /// <summary>
    /// Write the parent of every entity and the ids of its children.
    /// </summary>
    /// <remarks>
    /// Child lists are written as plain ids rather than as the <c>TreeNode</c> components that
    /// hold them. Those components store an <c>IdArray</c>, i.e. a <c>(start, count)</c> handle
    /// into a pooled allocator: <c>start</c> is this peer's allocation history, and the receiving
    /// store's pool has no matching allocation to point at. Reading such a handle back throws.
    /// </remarks>
    private void WriteHierarchy(BinaryWriter writer)
    {
        int capacity  = store.Capacity;
        var parentMap = store.GetParentMap();
        writer.Write(capacity);
        for (int id = 0; id < capacity; id++) {
            writer.Write(id < parentMap.Length ? parentMap[id] : 0);   // 0 == Static.NoParentId
        }
        var children = new int[8];
        for (int id = 0; id < capacity; id++) {
            int count = store.GetChildCount(id);
            writer.Write(count);
            if (count == 0) {
                continue;
            }
            if (children.Length < count) {
                children = new int[count];
            }
            store.CopyChildIds(id, children, 0);
            // Child order is meaningful (InsertChild positions matter), so it is preserved.
            for (int n = 0; n < count; n++) {
                writer.Write(children[n]);
            }
        }
    }

    /// <summary>
    /// Write every script: which entity holds it, which type it is, and its field values.
    /// </summary>
    /// <remarks>
    /// The type travels as its <c>ScriptType.ScriptIndex</c> — a schema position both peers derive
    /// from the same build — and the values as the deterministic leaves of the same layout plan
    /// components use, so an <see cref="Entity"/> field contributes only its <c>(Id, Revision)</c>.
    /// </remarks>
    private void WriteScripts(BinaryWriter writer)
    {
        // Counted first, since a script-bearing entity may hold several.
        int total = 0;
        int count = store.ScriptEntityCount;
        for (int n = 0; n < count; n++) {
            store.GetScriptsAt(n, out _, out var scripts);
            total += scripts.Length;
        }
        writer.Write(total);

        var leaves = Array.Empty<ulong>();
        for (int n = 0; n < count; n++) {
            store.GetScriptsAt(n, out int entityId, out var scripts);
            foreach (var script in scripts) {
                writer.Write(entityId);
                writer.Write(EntityStore.GetScriptIndex(script));
                int written = ScriptLayout.Capture(script, ref leaves);
                writer.Write(written);
                for (int i = 0; i < written; i++) {
                    writer.Write(leaves[i]);
                }
            }
        }
    }

    // ------------------------------------------------------------------ read

    /// <summary>Replace the store's state with <paramref name="data"/>. Returns the serialized tick.</summary>
    internal int Deserialize(byte[] data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));

        using var buffer = new MemoryStream(data, writable: false);
        using var reader = new BinaryReader(buffer);

        int version = reader.ReadInt32();
        if (version != FormatVersion) {
            throw new InvalidDataException($"unsupported state format {version}, expected {FormatVersion}");
        }
        int currentTick = reader.ReadInt32();
        int sequenceId  = reader.ReadInt32();
        int entityCount = reader.ReadInt32();
        int capacity    = reader.ReadInt32();

        var recycleIds   = ReadRecycleIds(reader, out int recycleCount);
        var archetypes   = ReadArchetypes(reader);
        ReadRelations(reader);

        store.EnsureNodesLengthInternal(capacity);
        ReadNodes(reader, capacity, archetypes);

        store.SequenceId          = sequenceId;
        store.SetRecycleIds(recycleIds, recycleCount);
        store.EntityCountInternal = entityCount;

        // After ReadNodes: rebuilding the child pool walks the nodes to find each TreeNode, and
        // attaching a script needs the owning entity's restored revision.
        ReadHierarchy(reader);
        ReadScripts(reader);

        store.RebuildIndexesAndRelations();
        return currentTick;
    }

    private static int[] ReadRecycleIds(BinaryReader reader, out int count)
    {
        count     = reader.ReadInt32();
        var ids   = new int[Math.Max(count, 1)];
        for (int n = 0; n < count; n++) {
            ids[n] = reader.ReadInt32();
        }
        return ids;
    }

    /// <summary>Recreate the serialized archetypes, returning them in serialized order.</summary>
    private List<Archetype> ReadArchetypes(BinaryReader reader)
    {
        int count  = reader.ReadInt32();
        var result = new List<Archetype>(count);
        for (int i = 0; i < count; i++) {
            result.Add(ReadArchetype(reader, relation: false));
        }
        // Archetypes present locally but absent from the payload must not keep stale entities.
        for (int archIndex = 0; archIndex < store.ArchsCount; archIndex++) {
            var archetype = store.GetArchetypeAt(archIndex);
            if (!result.Contains(archetype)) {
                archetype.SetEntityCount(0);
            }
        }
        return result;
    }

    private void ReadRelations(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++) {
            ReadArchetype(reader, relation: true);
        }
    }

    private Archetype ReadArchetype(BinaryReader reader, bool relation)
    {
        long tag0      = reader.ReadInt64();
        long tag1      = reader.ReadInt64();
        long tag2      = reader.ReadInt64();
        long tag3      = reader.ReadInt64();
        int  heapCount = reader.ReadInt32();
        var  structIndices = new int[heapCount];
        for (int h = 0; h < heapCount; h++) {
            structIndices[h] = reader.ReadInt32();
        }
        // Resolving by content creates the archetype locally when the receiver has never used it.
        var archetype = relation
            ? store.GetRelationArchetype(structIndices[0])
            : store.GetArchetypeByContent(structIndices, tag0, tag1, tag2, tag3);

        int entityCount = reader.ReadInt32();
        archetype.EnsureCapacity(entityCount);
        var entityIds = archetype.GetEntityIds();
        for (int n = 0; n < entityCount; n++) {
            entityIds[n] = reader.ReadInt32();
        }
        for (int h = 0; h < heapCount; h++) {
            int length = reader.ReadInt32();
            var bytes  = reader.ReadBytes(length);
            archetype.SetHeapComponentBytes(h, bytes, entityCount);
        }
        archetype.SetEntityCount(entityCount);
        return archetype;
    }

    /// <summary>Restore the parent map and rebuild the child pool from the serialized ids.</summary>
    private void ReadHierarchy(BinaryReader reader)
    {
        int capacity  = reader.ReadInt32();
        var parentMap = new int[Math.Max(capacity, 1)];
        for (int id = 0; id < capacity; id++) {
            parentMap[id] = reader.ReadInt32();
        }

        var childStarts = new int[Math.Max(capacity, 1)];
        var childCounts = new int[Math.Max(capacity, 1)];
        var childIds    = new List<int>();
        for (int id = 0; id < capacity; id++) {
            int count       = reader.ReadInt32();
            childStarts[id] = childIds.Count;
            childCounts[id] = count;
            for (int n = 0; n < count; n++) {
                childIds.Add(reader.ReadInt32());
            }
        }

        store.SetParentMap(parentMap, capacity);
        // Allocates a fresh child list per TreeNode, so the restored handles point into this
        // store's own pool rather than at the sender's offsets.
        store.RebuildChildHeap(childIds.ToArray(), childStarts, childCounts, capacity);
    }

    /// <summary>Recreate every script and attach it to its entity.</summary>
    /// <remarks>
    /// New instances, unlike the rollback path which rewrites the fields of the instances it
    /// already holds: a full state replaces the world wholesale, and the receiving store may never
    /// have held a script of this type at all.
    /// </remarks>
    private void ReadScripts(BinaryReader reader)
    {
        store.ClearScripts();
        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++) {
            int entityId    = reader.ReadInt32();
            int scriptIndex = reader.ReadInt32();
            int leafCount   = reader.ReadInt32();
            var leaves      = new ulong[Math.Max(leafCount, 1)];
            for (int n = 0; n < leafCount; n++) {
                leaves[n] = reader.ReadUInt64();
            }
            var script = EntityStore.CreateScript(scriptIndex);
            // Bind before attaching: Entity-typed fields are re-bound to this store, so the
            // instance is already consistent by the time anything can observe it.
            ScriptLayout.Restore(script, leaves, store);
            store.RestoreScript(entityId, script);
        }
    }

    private void ReadNodes(BinaryReader reader, int capacity, List<Archetype> archetypes)
    {
        for (int id = 0; id < capacity; id++) {
            int   archIndex = reader.ReadInt32();
            int   compIndex = reader.ReadInt32();
            short revision  = reader.ReadInt16();
            int   isOwner   = reader.ReadInt32();
            int   isLinked  = reader.ReadInt32();

            var archetype = archIndex < 0 ? null : archetypes[archIndex];
            store.SetNodeState(id, archetype, compIndex, revision, isOwner, isLinked);
        }
        // Any node beyond the payload belongs to the discarded local timeline.
        for (int id = capacity; id < store.Capacity; id++) {
            store.SetNodeState(id, null, 0, 0, 0, 0);
        }
    }
}
