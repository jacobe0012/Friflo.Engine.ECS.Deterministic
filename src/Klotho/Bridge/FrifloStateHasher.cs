using System;
using System.Collections.Generic;
using Friflo.Engine.ECS;

// ReSharper disable once CheckNamespace
namespace Klotho.Friflo;

/// <summary>
/// Computes a deterministic hash over the component data of an <see cref="EntityStore"/>.
/// </summary>
/// <remarks>
/// The hash must depend only on <i>which entity holds which component values</i>, never on how
/// the store happens to lay them out. Archetype order and the position of an entity within an
/// archetype both change under normal operation (entities are moved between archetypes, and
/// removal swaps in the last entity), so hashing memory in storage order would report a desync
/// where none exists.
///
/// Therefore each entity is hashed independently into a commutative accumulator:
/// per-entity hashes are mixed and then summed, so the total is invariant to visiting order
/// while still depending on every component byte. Component types are identified by their
/// stable struct index, so a value moving between component types changes the hash.
/// </remarks>
internal sealed class FrifloStateHasher
{
    private readonly EntityStore     store;
    private readonly List<Archetype> relationArchetypes = new();
    /// <summary>Scratch for the LIFO id allocator state. Reused so steady-state hashing allocates nothing.</summary>
    private int[]                    recycleIds = new int[8];

    internal FrifloStateHasher(EntityStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    private const ulong FnvOffset = 14695981039346656037;
    private const ulong FnvPrime  =        1099511628211;

    /// <summary>Hash the full component state of the store.</summary>
    internal long ComputeHash()
    {
        ulong total = 0;

        for (int archIndex = 0; archIndex < store.ArchsCount; archIndex++) {
            total += HashArchetype(store.GetArchetypeAt(archIndex));
        }
        // Relation archetypes are not part of store.archs[] - see FrifloStoreSnapshot.
        relationArchetypes.Clear();
        store.GetRelationArchetypes(relationArchetypes);
        foreach (var archetype in relationArchetypes) {
            total += HashArchetype(archetype);
        }
        total += HashScripts();
        total += HashStoreMetadata();
        return unchecked((long)total);
    }

    /// <summary>
    /// Hash store metadata that controls future simulation behaviour but is not represented by a
    /// live component: entity revisions, the sequence allocator and its ordered recycle stack.
    /// </summary>
    /// <remarks>
    /// Two stores can have byte-identical live entities while their recycle stacks have a different
    /// order. The very next <c>CreateEntity()</c> then returns different ids. Likewise, revision is
    /// part of <see cref="Entity"/> identity and changes stale-handle behaviour. Leaving either out
    /// turns a current hash match into a false assurance about the next tick.
    /// <para>
    /// Parent ids and relation ownership/link bits are included too. They are lookup-side state
    /// used directly by Friflo APIs and are serialized in FullState; hashing them prevents a broken
    /// derived table from hiding behind otherwise-correct component bytes. Archetype/slot indices
    /// are deliberately excluded because they are storage layout, not logical state.
    /// </para>
    /// </remarks>
    private ulong HashStoreMetadata()
    {
        ulong hash = FnvOffset;
        // Domain separator: prevents this stream being confused with a component byte stream when
        // its result is folded into the commutative total.
        Mix(ref hash, 0x465249464C4F4D45UL); // "FRIFLOME"

        int sequenceId = store.SequenceId;
        Mix(ref hash, (ulong)(uint)sequenceId);
        Mix(ref hash, (ulong)(uint)store.Count);
        Mix(ref hash, store.RecycleIds ? 1UL : 0UL);
        store.GetStoreRootState(out int storeRootId, out short storeRootRevision);
        Mix(ref hash, (ulong)(uint)storeRootId);
        Mix(ref hash, (ulong)(ushort)storeRootRevision);

        int recycleCount = store.GetRecycleIds(ref recycleIds);
        Mix(ref hash, (ulong)(uint)recycleCount);
        for (int i = 0; i < recycleCount; i++) {
            // Order is meaningful: NewId pops this stack LIFO.
            Mix(ref hash, (ulong)(uint)recycleIds[i]);
        }

        var parentMap = store.GetParentMap();
        for (int id = 1; id <= sequenceId; id++) {
            var live = store.GetEntityNode(id).Archetype != null;
            store.GetNodeState(id, out _, out short revision, out int isOwner, out int isLinked);
            Mix(ref hash, live ? 1UL : 0UL);
            Mix(ref hash, (ulong)(ushort)revision);
            Mix(ref hash, (ulong)(uint)isOwner);
            Mix(ref hash, (ulong)(uint)isLinked);
            Mix(ref hash, (ulong)(uint)(id < parentMap.Length ? parentMap[id] : 0));
        }
        return hash;
    }

    /// <summary>
    /// Sum of the per-script hashes. Scripts live outside the archetypes, so without this a
    /// script field diverging between peers would not be detected.
    /// </summary>
    private ulong HashScripts()
    {
        const ulong prime = 1099511628211;
        ulong sum   = 0;
        int   count = store.ScriptEntityCount;
        for (int n = 0; n < count; n++) {
            store.GetScriptsAt(n, out int entityId, out var scripts);
            foreach (var script in scripts) {
                // Seed with the owning entity and the script type, so moving a script between
                // entities - or swapping one script type for another - changes the hash.
                ulong hash = FnvOffset;
                hash ^= (ulong)(uint)entityId;
                hash *= prime;
                hash ^= HashTypeName(script.GetType());
                hash *= prime;
                hash ^= ScriptLayout.Hash(script);
                hash *= prime;
                sum  += hash;
            }
        }
        return sum;
    }

    /// <summary>Sum of the per-entity hashes of one archetype. Commutative, so slot order is irrelevant.</summary>
    private static ulong HashArchetype(Archetype archetype)
    {
        int count = archetype.Count;
        if (count == 0) {
            return 0;
        }
        ulong sum       = 0;
        var   entityIds = archetype.GetEntityIds();
        int   heapCount = archetype.GetHeapCount();
        // All four words: a tag index of 64 or above lives outside the first one.
        archetype.GetTagBits(out long tag0, out long tag1, out long tag2, out long tag3);

        for (int slot = 0; slot < count; slot++) {
            // Seed with the entity id and the archetype's tags so that moving an entity between
            // archetypes, or changing only its tags, still changes the hash.
            ulong hash = FnvOffset;
            Mix(ref hash, (ulong)(uint)entityIds[slot]);
            Mix(ref hash, (ulong)tag0);
            Mix(ref hash, (ulong)tag1);
            Mix(ref hash, (ulong)tag2);
            Mix(ref hash, (ulong)tag3);

            for (int heapIndex = 0; heapIndex < heapCount; heapIndex++) {
                // Struct index identifies the component type independently of heap ordering.
                Mix(ref hash, (ulong)(uint)archetype.GetHeapStructIndex(heapIndex));
                Mix(ref hash, archetype.HashHeapComponent(heapIndex, slot));
            }
            sum += hash;
        }
        return sum;
    }

    /// <summary>
    /// Hash a type by its full name, character by character.
    /// </summary>
    /// <remarks>
    /// Not <c>string.GetHashCode</c>: .NET randomizes string hashing per process, so it would
    /// differ between two peers running identical code — the exact failure this hash exists to
    /// detect. Cached per type, since the result never changes.
    /// </remarks>
    private ulong HashTypeName(Type type)
    {
        if (typeNameHashes.TryGetValue(type, out var cached)) {
            return cached;
        }
        ulong hash = FnvOffset;
        foreach (var c in type.FullName ?? type.Name) {
            hash ^= c;
            hash *= FnvPrime;
        }
        return typeNameHashes[type] = hash;
    }

    private readonly Dictionary<Type, ulong> typeNameHashes = new();

    private static void Mix(ref ulong hash, ulong value)
    {
        hash ^= value;
        hash *= FnvPrime;
    }
}
