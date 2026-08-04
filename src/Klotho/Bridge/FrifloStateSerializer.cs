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
/// This runs at join/resync time, not per tick, so it allocates freely.
/// </remarks>
internal sealed class FrifloStateSerializer
{
    private const int FormatVersion = 1;

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
        writer.Write(archetype.GetTagBits());
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
        long tagBits   = reader.ReadInt64();
        int  heapCount = reader.ReadInt32();
        var  structIndices = new int[heapCount];
        for (int h = 0; h < heapCount; h++) {
            structIndices[h] = reader.ReadInt32();
        }
        // Resolving by content creates the archetype locally when the receiver has never used it.
        var archetype = relation
            ? store.GetRelationArchetype(structIndices[0])
            : store.GetArchetypeByContent(structIndices, tagBits);

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
