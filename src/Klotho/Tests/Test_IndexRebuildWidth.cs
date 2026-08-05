using System;
using System.Collections.Generic;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using NUnit.Framework;
using xpTURN.Klotho.Core;

// ReSharper disable once CheckNamespace
namespace Klotho.Friflo.Tests;

/// <summary>
/// Pins the post-rollback index rebuild down to indexed components whose struct index lies beyond
/// the first bit-set word.
/// </summary>
/// <remarks>
/// The rebuild works out which of an archetype's components are indexed by intersecting the
/// archetype's component bits with the schema's indexed-component bits. Both sets are 256 bits
/// wide. Intersecting only the first 64-bit word silently skips any indexed component numbered 64
/// or above: its index keeps whatever the abandoned timeline left there, so a lookup by value
/// returns entities that were rolled back, or misses entities that exist.
///
/// This one is quiet in a way the component data is not. The values themselves restore correctly
/// and the state hash agrees — only queries disagree, so a peer diverges through the branches its
/// systems take rather than through the state it reports.
///
/// The schema numbers indexed components and relations before all others, so reaching index 64
/// takes 64 indexed types in one schema. The padding below exists solely to get there; the test
/// asks the schema which type actually landed high rather than assuming any particular one.
/// </remarks>
public static class Test_IndexRebuildWidth
{
    private struct Pad00 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad01 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad02 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad03 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad04 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad05 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad06 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad07 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad08 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad09 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad10 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad11 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad12 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad13 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad14 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad15 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad16 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad17 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad18 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad19 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad20 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad21 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad22 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad23 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad24 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad25 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad26 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad27 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad28 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad29 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad30 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad31 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad32 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad33 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad34 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad35 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad36 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad37 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad38 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad39 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad40 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad41 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad42 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad43 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad44 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad45 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad46 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad47 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad48 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad49 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad50 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad51 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad52 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad53 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad54 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad55 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad56 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad57 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad58 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad59 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad60 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad61 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad62 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad63 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad64 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad65 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad66 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad67 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad68 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }
    private struct Pad69 : IIndexedComponent<int> { public int value; public int GetIndexedValue() => value; }

    /// <summary>Add an indexed pad component, and count the entities the index holds for a value.</summary>
    private readonly struct Pad
    {
        internal readonly Type                        Type;
        internal readonly Action<Entity, int>         Add;
        internal readonly Func<EntityStore, int, int> CountWithValue;

        internal Pad(Type type, Action<Entity, int> add, Func<EntityStore, int, int> countWithValue)
        {
            Type           = type;
            Add            = add;
            CountWithValue = countWithValue;
        }
    }

    private static readonly Pad[] Pads = {
        new (typeof(Pad00), (e, v) => e.AddComponent(new Pad00 { value = v }), (s, v) => s.ComponentIndex<Pad00, int>()[v].Count),
        new (typeof(Pad01), (e, v) => e.AddComponent(new Pad01 { value = v }), (s, v) => s.ComponentIndex<Pad01, int>()[v].Count),
        new (typeof(Pad02), (e, v) => e.AddComponent(new Pad02 { value = v }), (s, v) => s.ComponentIndex<Pad02, int>()[v].Count),
        new (typeof(Pad03), (e, v) => e.AddComponent(new Pad03 { value = v }), (s, v) => s.ComponentIndex<Pad03, int>()[v].Count),
        new (typeof(Pad04), (e, v) => e.AddComponent(new Pad04 { value = v }), (s, v) => s.ComponentIndex<Pad04, int>()[v].Count),
        new (typeof(Pad05), (e, v) => e.AddComponent(new Pad05 { value = v }), (s, v) => s.ComponentIndex<Pad05, int>()[v].Count),
        new (typeof(Pad06), (e, v) => e.AddComponent(new Pad06 { value = v }), (s, v) => s.ComponentIndex<Pad06, int>()[v].Count),
        new (typeof(Pad07), (e, v) => e.AddComponent(new Pad07 { value = v }), (s, v) => s.ComponentIndex<Pad07, int>()[v].Count),
        new (typeof(Pad08), (e, v) => e.AddComponent(new Pad08 { value = v }), (s, v) => s.ComponentIndex<Pad08, int>()[v].Count),
        new (typeof(Pad09), (e, v) => e.AddComponent(new Pad09 { value = v }), (s, v) => s.ComponentIndex<Pad09, int>()[v].Count),
        new (typeof(Pad10), (e, v) => e.AddComponent(new Pad10 { value = v }), (s, v) => s.ComponentIndex<Pad10, int>()[v].Count),
        new (typeof(Pad11), (e, v) => e.AddComponent(new Pad11 { value = v }), (s, v) => s.ComponentIndex<Pad11, int>()[v].Count),
        new (typeof(Pad12), (e, v) => e.AddComponent(new Pad12 { value = v }), (s, v) => s.ComponentIndex<Pad12, int>()[v].Count),
        new (typeof(Pad13), (e, v) => e.AddComponent(new Pad13 { value = v }), (s, v) => s.ComponentIndex<Pad13, int>()[v].Count),
        new (typeof(Pad14), (e, v) => e.AddComponent(new Pad14 { value = v }), (s, v) => s.ComponentIndex<Pad14, int>()[v].Count),
        new (typeof(Pad15), (e, v) => e.AddComponent(new Pad15 { value = v }), (s, v) => s.ComponentIndex<Pad15, int>()[v].Count),
        new (typeof(Pad16), (e, v) => e.AddComponent(new Pad16 { value = v }), (s, v) => s.ComponentIndex<Pad16, int>()[v].Count),
        new (typeof(Pad17), (e, v) => e.AddComponent(new Pad17 { value = v }), (s, v) => s.ComponentIndex<Pad17, int>()[v].Count),
        new (typeof(Pad18), (e, v) => e.AddComponent(new Pad18 { value = v }), (s, v) => s.ComponentIndex<Pad18, int>()[v].Count),
        new (typeof(Pad19), (e, v) => e.AddComponent(new Pad19 { value = v }), (s, v) => s.ComponentIndex<Pad19, int>()[v].Count),
        new (typeof(Pad20), (e, v) => e.AddComponent(new Pad20 { value = v }), (s, v) => s.ComponentIndex<Pad20, int>()[v].Count),
        new (typeof(Pad21), (e, v) => e.AddComponent(new Pad21 { value = v }), (s, v) => s.ComponentIndex<Pad21, int>()[v].Count),
        new (typeof(Pad22), (e, v) => e.AddComponent(new Pad22 { value = v }), (s, v) => s.ComponentIndex<Pad22, int>()[v].Count),
        new (typeof(Pad23), (e, v) => e.AddComponent(new Pad23 { value = v }), (s, v) => s.ComponentIndex<Pad23, int>()[v].Count),
        new (typeof(Pad24), (e, v) => e.AddComponent(new Pad24 { value = v }), (s, v) => s.ComponentIndex<Pad24, int>()[v].Count),
        new (typeof(Pad25), (e, v) => e.AddComponent(new Pad25 { value = v }), (s, v) => s.ComponentIndex<Pad25, int>()[v].Count),
        new (typeof(Pad26), (e, v) => e.AddComponent(new Pad26 { value = v }), (s, v) => s.ComponentIndex<Pad26, int>()[v].Count),
        new (typeof(Pad27), (e, v) => e.AddComponent(new Pad27 { value = v }), (s, v) => s.ComponentIndex<Pad27, int>()[v].Count),
        new (typeof(Pad28), (e, v) => e.AddComponent(new Pad28 { value = v }), (s, v) => s.ComponentIndex<Pad28, int>()[v].Count),
        new (typeof(Pad29), (e, v) => e.AddComponent(new Pad29 { value = v }), (s, v) => s.ComponentIndex<Pad29, int>()[v].Count),
        new (typeof(Pad30), (e, v) => e.AddComponent(new Pad30 { value = v }), (s, v) => s.ComponentIndex<Pad30, int>()[v].Count),
        new (typeof(Pad31), (e, v) => e.AddComponent(new Pad31 { value = v }), (s, v) => s.ComponentIndex<Pad31, int>()[v].Count),
        new (typeof(Pad32), (e, v) => e.AddComponent(new Pad32 { value = v }), (s, v) => s.ComponentIndex<Pad32, int>()[v].Count),
        new (typeof(Pad33), (e, v) => e.AddComponent(new Pad33 { value = v }), (s, v) => s.ComponentIndex<Pad33, int>()[v].Count),
        new (typeof(Pad34), (e, v) => e.AddComponent(new Pad34 { value = v }), (s, v) => s.ComponentIndex<Pad34, int>()[v].Count),
        new (typeof(Pad35), (e, v) => e.AddComponent(new Pad35 { value = v }), (s, v) => s.ComponentIndex<Pad35, int>()[v].Count),
        new (typeof(Pad36), (e, v) => e.AddComponent(new Pad36 { value = v }), (s, v) => s.ComponentIndex<Pad36, int>()[v].Count),
        new (typeof(Pad37), (e, v) => e.AddComponent(new Pad37 { value = v }), (s, v) => s.ComponentIndex<Pad37, int>()[v].Count),
        new (typeof(Pad38), (e, v) => e.AddComponent(new Pad38 { value = v }), (s, v) => s.ComponentIndex<Pad38, int>()[v].Count),
        new (typeof(Pad39), (e, v) => e.AddComponent(new Pad39 { value = v }), (s, v) => s.ComponentIndex<Pad39, int>()[v].Count),
        new (typeof(Pad40), (e, v) => e.AddComponent(new Pad40 { value = v }), (s, v) => s.ComponentIndex<Pad40, int>()[v].Count),
        new (typeof(Pad41), (e, v) => e.AddComponent(new Pad41 { value = v }), (s, v) => s.ComponentIndex<Pad41, int>()[v].Count),
        new (typeof(Pad42), (e, v) => e.AddComponent(new Pad42 { value = v }), (s, v) => s.ComponentIndex<Pad42, int>()[v].Count),
        new (typeof(Pad43), (e, v) => e.AddComponent(new Pad43 { value = v }), (s, v) => s.ComponentIndex<Pad43, int>()[v].Count),
        new (typeof(Pad44), (e, v) => e.AddComponent(new Pad44 { value = v }), (s, v) => s.ComponentIndex<Pad44, int>()[v].Count),
        new (typeof(Pad45), (e, v) => e.AddComponent(new Pad45 { value = v }), (s, v) => s.ComponentIndex<Pad45, int>()[v].Count),
        new (typeof(Pad46), (e, v) => e.AddComponent(new Pad46 { value = v }), (s, v) => s.ComponentIndex<Pad46, int>()[v].Count),
        new (typeof(Pad47), (e, v) => e.AddComponent(new Pad47 { value = v }), (s, v) => s.ComponentIndex<Pad47, int>()[v].Count),
        new (typeof(Pad48), (e, v) => e.AddComponent(new Pad48 { value = v }), (s, v) => s.ComponentIndex<Pad48, int>()[v].Count),
        new (typeof(Pad49), (e, v) => e.AddComponent(new Pad49 { value = v }), (s, v) => s.ComponentIndex<Pad49, int>()[v].Count),
        new (typeof(Pad50), (e, v) => e.AddComponent(new Pad50 { value = v }), (s, v) => s.ComponentIndex<Pad50, int>()[v].Count),
        new (typeof(Pad51), (e, v) => e.AddComponent(new Pad51 { value = v }), (s, v) => s.ComponentIndex<Pad51, int>()[v].Count),
        new (typeof(Pad52), (e, v) => e.AddComponent(new Pad52 { value = v }), (s, v) => s.ComponentIndex<Pad52, int>()[v].Count),
        new (typeof(Pad53), (e, v) => e.AddComponent(new Pad53 { value = v }), (s, v) => s.ComponentIndex<Pad53, int>()[v].Count),
        new (typeof(Pad54), (e, v) => e.AddComponent(new Pad54 { value = v }), (s, v) => s.ComponentIndex<Pad54, int>()[v].Count),
        new (typeof(Pad55), (e, v) => e.AddComponent(new Pad55 { value = v }), (s, v) => s.ComponentIndex<Pad55, int>()[v].Count),
        new (typeof(Pad56), (e, v) => e.AddComponent(new Pad56 { value = v }), (s, v) => s.ComponentIndex<Pad56, int>()[v].Count),
        new (typeof(Pad57), (e, v) => e.AddComponent(new Pad57 { value = v }), (s, v) => s.ComponentIndex<Pad57, int>()[v].Count),
        new (typeof(Pad58), (e, v) => e.AddComponent(new Pad58 { value = v }), (s, v) => s.ComponentIndex<Pad58, int>()[v].Count),
        new (typeof(Pad59), (e, v) => e.AddComponent(new Pad59 { value = v }), (s, v) => s.ComponentIndex<Pad59, int>()[v].Count),
        new (typeof(Pad60), (e, v) => e.AddComponent(new Pad60 { value = v }), (s, v) => s.ComponentIndex<Pad60, int>()[v].Count),
        new (typeof(Pad61), (e, v) => e.AddComponent(new Pad61 { value = v }), (s, v) => s.ComponentIndex<Pad61, int>()[v].Count),
        new (typeof(Pad62), (e, v) => e.AddComponent(new Pad62 { value = v }), (s, v) => s.ComponentIndex<Pad62, int>()[v].Count),
        new (typeof(Pad63), (e, v) => e.AddComponent(new Pad63 { value = v }), (s, v) => s.ComponentIndex<Pad63, int>()[v].Count),
        new (typeof(Pad64), (e, v) => e.AddComponent(new Pad64 { value = v }), (s, v) => s.ComponentIndex<Pad64, int>()[v].Count),
        new (typeof(Pad65), (e, v) => e.AddComponent(new Pad65 { value = v }), (s, v) => s.ComponentIndex<Pad65, int>()[v].Count),
        new (typeof(Pad66), (e, v) => e.AddComponent(new Pad66 { value = v }), (s, v) => s.ComponentIndex<Pad66, int>()[v].Count),
        new (typeof(Pad67), (e, v) => e.AddComponent(new Pad67 { value = v }), (s, v) => s.ComponentIndex<Pad67, int>()[v].Count),
        new (typeof(Pad68), (e, v) => e.AddComponent(new Pad68 { value = v }), (s, v) => s.ComponentIndex<Pad68, int>()[v].Count),
        new (typeof(Pad69), (e, v) => e.AddComponent(new Pad69 { value = v }), (s, v) => s.ComponentIndex<Pad69, int>()[v].Count),
    };

    private static void ApplyNothing(EntityStore store, List<ICommand> commands, int tick) { }

    /// <summary>The padding component whose struct index is highest, with that index.</summary>
    private static Pad HighestIndexed(out int structIndex)
    {
        // Touch every pad type so the schema has been built before it is queried.
        var probe = new EntityStore().CreateEntity();
        foreach (var pad in Pads) {
            pad.Add(probe, 0);
        }
        var schema = EntityStore.GetEntitySchema();
        var best   = Pads[0];
        structIndex = -1;
        foreach (var pad in Pads) {
            foreach (var componentType in schema.Components) {
                if (componentType != null && componentType.Type == pad.Type && componentType.StructIndex > structIndex) {
                    structIndex = componentType.StructIndex;
                    best        = pad;
                }
            }
        }
        return best;
    }

    /// <summary>
    /// A rollback must leave a high-numbered component's index agreeing with the restored values.
    /// </summary>
    [Test]
    public static void Rollback_RebuildsIndexesForHighStructIndexComponents()
    {
        var pad = HighestIndexed(out int structIndex);
        Assert.GreaterOrEqual(structIndex, 64,
            $"highest indexed pad ({pad.Type.Name}) is at struct index {structIndex}, below the " +
            "first-word boundary, so this test cannot tell a one-word intersection from a full one");

        var store      = new EntityStore();
        var simulation = new FrifloSimulation(store, new SystemRoot(store), ApplyNothing);

        var entity = store.CreateEntity();
        pad.Add(entity, 1);

        simulation.SaveSnapshot();                  // tick 0: value 1
        simulation.Tick(new List<ICommand>());

        // The branch that gets abandoned moves the entity to a different indexed value.
        pad.Add(entity, 2);
        Assert.AreEqual(1, pad.CountWithValue(store, 2), "the store did not index the new value");

        simulation.Rollback(0);

        // Component data restores either way; the index is what is at stake here.
        Assert.AreEqual(1, pad.CountWithValue(store, 1),
            $"index lookup for the restored value of {pad.Type.Name} (struct index {structIndex}) " +
            "missed the entity: the rollback skipped the index of a component beyond the first " +
            "bit-set word");
        Assert.AreEqual(0, pad.CountWithValue(store, 2),
            $"index lookup for {pad.Type.Name} still returns the entity under its abandoned value");
    }
}
