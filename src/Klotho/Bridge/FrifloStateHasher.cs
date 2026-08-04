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
        return unchecked((long)total);
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

        for (int slot = 0; slot < count; slot++) {
            // Seed with the entity id and the archetype's tags so that moving an entity between
            // archetypes, or changing only its tags, still changes the hash.
            ulong hash = FnvOffset;
            Mix(ref hash, (ulong)(uint)entityIds[slot]);
            Mix(ref hash, (ulong)archetype.GetTagBits());

            for (int heapIndex = 0; heapIndex < heapCount; heapIndex++) {
                // Struct index identifies the component type independently of heap ordering.
                Mix(ref hash, (ulong)(uint)archetype.GetHeapStructIndex(heapIndex));
                Mix(ref hash, archetype.HashHeapComponent(heapIndex, slot));
            }
            sum += hash;
        }
        return sum;
    }

    private static void Mix(ref ulong hash, ulong value)
    {
        hash ^= value;
        hash *= FnvPrime;
    }
}
