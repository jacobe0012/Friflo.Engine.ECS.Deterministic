using System;
using System.Collections.Generic;
using System.Reflection;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using NUnit.Framework;
using xpTURN.Klotho.Core;

// ReSharper disable once CheckNamespace
namespace Klotho.Friflo.Tests;

/// <summary>
/// Rolling back must reuse the pooled allocators behind the child lists, the component indexes and
/// the relation lookups, rather than allocating a fresh region on each rebuild.
/// </summary>
/// <remarks>
/// Layer 4 of the snapshot rebuilds these lookup structures from scratch instead of deep-copying
/// them, which is what keeps the per-frame path allocation-free. But the id arrays they hold live
/// in <c>IdArrayPool</c>, which reclaims a region only when handed that region's start offset back.
/// Clearing the owning dictionary drops the last reference to every handle at once, so there is
/// nothing left to hand back: each rebuild takes a fresh region and the pool's backing array grows
/// for as long as the match runs.
///
/// <para>Nothing existing could see this. The pool offsets are allocation history, deliberately
/// excluded from the state hash (see <c>Test_HierarchyRollback</c>), so both peers agree
/// throughout; the leaked memory is reachable, so it is not a GC leak; and the per-tick allocation
/// assertion covers <c>SaveSnapshot</c>, whereas the growth happens in <c>Rollback</c>. It shows up
/// only as a client whose memory climbs with the number of mispredictions — measured below at
/// roughly 26 KB per thousand rollbacks per pool, on a store holding four child entities.</para>
///
/// <para>These tests read the pool through reflection. That is deliberate: the point is the
/// allocator's internal bookkeeping, which has no public surface, and asserting on a proxy such as
/// managed heap size would be far more fragile.</para>
/// </remarks>
public static class Test_LookupPoolReuse
{
    private struct Marker : IComponent { public int value; }
    private struct Squad  : IIndexedComponent<int> { public int id; public int GetIndexedValue() => id; }
    private struct Owes   : IRelation<int> { public int debtor; public int amount; public int GetRelationKey() => debtor; }
    private struct Follows: ILinkRelation { public Entity leader; public Entity GetRelationKey() => leader; }

    /// <summary>How many rollbacks a leak needs before it is unmistakably a trend and not a resize.</summary>
    private const int Rollbacks = 200;

    private static void ApplyNothing(EntityStore store, List<ICommand> commands, int tick) { }

    // ------------------------------------------------------------------ reflection helpers

    private static object FieldValue(object target, string name)
    {
        for (var type = target.GetType(); type != null; type = type.BaseType) {
            var field = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            if (field != null) {
                return field.GetValue(target);
            }
        }
        throw new InvalidOperationException($"no field '{name}' on {target.GetType().Name}");
    }

    /// <summary>Number of live allocations across every pool of an <c>IdArrayHeap</c>.</summary>
    private static int Allocations(object idArrayHeap)
    {
        int total = 0;
        foreach (var pool in (Array)FieldValue(idArrayHeap, "pools")) {
            if (pool != null) {
                total += (int)FieldValue(pool, "count");
            }
        }
        return total;
    }

    /// <summary>Total capacity the pools hold, in ints — what actually grows when regions leak.</summary>
    private static int PooledCapacity(object idArrayHeap)
    {
        int total = 0;
        foreach (var pool in (Array)FieldValue(idArrayHeap, "pools")) {
            if (pool != null) {
                total += ((int[])FieldValue(pool, "ids")).Length;
            }
        }
        return total;
    }

    /// <summary>The struct index the schema assigned to <typeparamref name="T"/>.</summary>
    private static int StructIndexOf<T>() where T : struct
    {
        foreach (var componentType in EntityStore.GetEntitySchema().Components) {
            if (componentType != null && componentType.Type == typeof(T)) {
                return componentType.StructIndex;
            }
        }
        throw new InvalidOperationException($"{typeof(T).Name} is not in the schema");
    }

    /// <summary>The store's component index for <typeparamref name="T"/>, or null if it has none.</summary>
    private static object ComponentIndexOf<T>(EntityStore store) where T : struct
    {
        var indexMap = (Array)FieldValue(FieldValue(store, "extension"), "indexMap");
        if (indexMap == null) {
            return null;
        }
        int structIndex = StructIndexOf<T>();
        return structIndex < indexMap.Length ? indexMap.GetValue(structIndex) : null;
    }

    /// <summary>The store's relation bookkeeping for <typeparamref name="T"/>, or null if it has none.</summary>
    private static object RelationsOf<T>(EntityStore store) where T : struct
    {
        var relationsMap = (Array)FieldValue(FieldValue(store, "extension"), "relationsMap");
        if (relationsMap == null) {
            return null;
        }
        int structIndex = StructIndexOf<T>();
        return structIndex < relationsMap.Length ? relationsMap.GetValue(structIndex) : null;
    }

    /// <summary>
    /// Roll back <see cref="Rollbacks"/> times onto one snapshot and assert the pool is exactly as
    /// large at the end as after the first rebuild.
    /// </summary>
    /// <remarks>
    /// The baseline is taken after one rollback rather than before any, so a one-off difference
    /// between the shape the store built organically and the shape a rebuild produces does not read
    /// as a leak. What is asserted is that rebuild number 200 costs nothing over rebuild number 1.
    /// </remarks>
    private static void AssertPoolIsReused(FrifloSimulation simulation, int targetTick, Func<object> heap, string what)
    {
        simulation.Rollback(targetTick);
        simulation.SaveSnapshot();
        int baselineAllocations = Allocations(heap());
        int baselineCapacity    = PooledCapacity(heap());
        Assert.Greater(baselineAllocations, 0,
            $"the {what} pool holds nothing, so this test could not tell a leak from correct reuse");

        for (int n = 0; n < Rollbacks; n++) {
            simulation.Rollback(targetTick);
            simulation.SaveSnapshot();
        }

        Assert.AreEqual(baselineAllocations, Allocations(heap()),
            $"{what}: {Rollbacks} rollbacks left {Allocations(heap()) - baselineAllocations} extra live " +
             "allocations - each rebuild took a fresh region instead of reusing the pool");
        Assert.AreEqual(baselineCapacity, PooledCapacity(heap()),
            $"{what}: pooled capacity grew from {baselineCapacity} to {PooledCapacity(heap())} ints " +
            $"over {Rollbacks} rollbacks - it grows for as long as the match runs");
    }

    /// <summary>The child-list allocator must not grow with the number of rollbacks.</summary>
    [Test]
    public static void Rollback_ReusesTheChildListPool()
    {
        var store      = new EntityStore();
        var simulation = new FrifloSimulation(store, new SystemRoot(store), ApplyNothing);

        var parent = store.CreateEntity(new Marker { value = 1 });
        // Two children at least: an IdArray of count 1 keeps the id inline and never touches the
        // pool, so a single child would leave this test asserting about an allocator it never used.
        for (int i = 0; i < 4; i++) {
            parent.AddChild(store.CreateEntity(new Marker { value = i }));
        }

        simulation.SaveSnapshot();
        AssertPoolIsReused(simulation, simulation.CurrentTick, () => store.GetChildHeap(), "child list");
    }

    /// <summary>The component index's id allocator must not grow with the number of rollbacks.</summary>
    [Test]
    public static void Rollback_ReusesTheComponentIndexPool()
    {
        var store      = new EntityStore();
        var simulation = new FrifloSimulation(store, new SystemRoot(store), ApplyNothing);

        // Several entities sharing one indexed value, so the index stores a pooled id array
        // rather than a single inline id. Added after creation rather than through a creation
        // signature: the signature path decides what to index from a one-word mask, so it would
        // silently skip Squad whenever other test fixtures push its struct index past 64.
        for (int i = 0; i < 4; i++) {
            store.CreateEntity().AddComponent(new Squad { id = 7 });
        }

        simulation.SaveSnapshot();
        Assert.AreEqual(4, store.ComponentIndex<Squad, int>()[7].Count, "the store did not index Squad");
        var index = ComponentIndexOf<Squad>(store);
        Assert.NotNull(index, "the store built no index for Squad - the test never reached the pool");
        AssertPoolIsReused(simulation, simulation.CurrentTick,
            () => FieldValue(index, "idHeap"), "component index");
    }

    /// <summary>The relation position allocator must not grow with the number of rollbacks.</summary>
    [Test]
    public static void Rollback_ReusesTheRelationPositionPool()
    {
        var store      = new EntityStore();
        var simulation = new FrifloSimulation(store, new SystemRoot(store), ApplyNothing);

        // One entity holding several relations: its position list is what gets pooled.
        var debtor = store.CreateEntity(new Marker { value = 1 });
        for (int key = 0; key < 4; key++) {
            debtor.AddRelation(new Owes { debtor = key, amount = 10 * key });
        }

        simulation.SaveSnapshot();
        var relations = RelationsOf<Owes>(store);
        Assert.NotNull(relations, "the store built no relations for Owes - the test never reached the pool");
        AssertPoolIsReused(simulation, simulation.CurrentTick,
            () => FieldValue(relations, "idHeap"), "relation positions");
    }

    /// <summary>The link relation's incoming-link allocator must not grow with the number of rollbacks.</summary>
    /// <remarks>
    /// A link relation keeps a second pool, mapping each target to the entities pointing at it.
    /// It is rebuilt by a different method than the position map above and so can leak on its own.
    /// </remarks>
    [Test]
    public static void Rollback_ReusesTheIncomingLinkPool()
    {
        var store      = new EntityStore();
        var simulation = new FrifloSimulation(store, new SystemRoot(store), ApplyNothing);

        // Several followers on one leader, so the leader's incoming-link list is pooled.
        var leader = store.CreateEntity(new Marker { value = 1 });
        for (int i = 0; i < 4; i++) {
            store.CreateEntity(new Marker { value = i }).AddRelation(new Follows { leader = leader });
        }

        simulation.SaveSnapshot();
        var relations = RelationsOf<Follows>(store);
        Assert.NotNull(relations, "the store built no relations for Follows - the test never reached the pool");
        AssertPoolIsReused(simulation, simulation.CurrentTick,
            () => FieldValue(relations, "linkIdsHeap"), "incoming links");
    }

    /// <summary>
    /// Applying a full state repeatedly must reuse the child pool too.
    /// </summary>
    /// <remarks>
    /// The full-state path rebuilds the same structures through the same routines, so it leaks in
    /// the same way — and it is the path a server-driven client takes on every resync, which is to
    /// say precisely when the network is already misbehaving.
    /// </remarks>
    [Test]
    public static void FullState_ReusesTheChildListPool()
    {
        var source          = new EntityStore();
        var sourceSimulation = new FrifloSimulation(source, new SystemRoot(source), ApplyNothing);
        var parent           = source.CreateEntity(new Marker { value = 1 });
        for (int i = 0; i < 4; i++) {
            parent.AddChild(source.CreateEntity(new Marker { value = i }));
        }
        var state = sourceSimulation.SerializeFullState();

        var target           = new EntityStore();
        var targetSimulation = new FrifloSimulation(target, new SystemRoot(target), ApplyNothing);

        targetSimulation.RestoreFromFullState(state);
        int baselineAllocations = Allocations(target.GetChildHeap());
        int baselineCapacity    = PooledCapacity(target.GetChildHeap());
        Assert.Greater(baselineAllocations, 0, "no pooled child list was restored - the test is vacuous");

        for (int n = 0; n < Rollbacks; n++) {
            targetSimulation.RestoreFromFullState(state);
        }

        Assert.AreEqual(baselineAllocations, Allocations(target.GetChildHeap()),
            "each full-state restore took a fresh region from the child pool instead of reusing it");
        Assert.AreEqual(baselineCapacity, PooledCapacity(target.GetChildHeap()),
            "the child pool grows with every resync");
    }

    /// <summary>
    /// Reusing the pool must not change what the store reports: the same tree, indexes and
    /// relations must survive the repeated rollbacks above.
    /// </summary>
    /// <remarks>
    /// Resetting an allocator that something still points into would corrupt every list at once,
    /// so the fix needs a correctness guard alongside the growth ones. The state hash cannot serve
    /// as that guard here — it deliberately ignores pool offsets — so this reads the lists back.
    /// </remarks>
    [Test]
    public static void Rollback_KeepsLookupsIntactWhileReusingPools()
    {
        var store      = new EntityStore();
        var simulation = new FrifloSimulation(store, new SystemRoot(store), ApplyNothing);

        var parent   = store.CreateEntity(new Marker { value = 1 });
        var children = new List<int>();
        for (int i = 0; i < 4; i++) {
            var child = store.CreateEntity();
            child.AddComponent(new Squad { id = 7 });
            parent.AddChild(child);
            children.Add(child.Id);
            child.AddRelation(new Follows { leader = parent });
        }
        for (int key = 0; key < 4; key++) {
            parent.AddRelation(new Owes { debtor = key, amount = 10 * key });
        }

        simulation.SaveSnapshot();
        int targetTick = simulation.CurrentTick;
        long expectedHash = simulation.GetStateHash();

        for (int n = 0; n < Rollbacks; n++) {
            simulation.Rollback(targetTick);
            simulation.SaveSnapshot();
        }

        var actualChildren = new List<int>();
        foreach (var child in parent.ChildEntities) {
            actualChildren.Add(child.Id);
        }
        CollectionAssert.AreEqual(children, actualChildren, "child list corrupted by pool reuse");
        Assert.AreEqual(4, store.ComponentIndex<Squad, int>()[7].Count, "index lookup corrupted by pool reuse");
        Assert.AreEqual(4, parent.GetRelations<Owes>().Length,          "relations corrupted by pool reuse");
        Assert.AreEqual(4, parent.GetIncomingLinks<Follows>().Count,    "incoming links corrupted by pool reuse");
        Assert.AreEqual(expectedHash, simulation.GetStateHash(),        "state diverged across repeated rollbacks");
    }
}
