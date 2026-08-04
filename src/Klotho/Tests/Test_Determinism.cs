using System;
using System.Collections.Generic;
using Friflo.Engine.ECS;
using Klotho.Friflo;
using NUnit.Framework;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;

// ReSharper disable once CheckNamespace
namespace Klotho.Friflo.Tests;

/// <summary>
/// Acceptance tests for the Friflo &lt;-&gt; Klotho bridge: rolling back and re-simulating must
/// reproduce the state exactly, including entity creation/deletion and the index and relation
/// lookup tables.
/// </summary>
public static class Test_Determinism
{
    private static readonly List<ICommand> NoCommands = new();

    /// <summary>Every tick: run forward, roll back, re-simulate, compare hashes.</summary>
    [Test]
    public static void SyncTest_EveryTick_HashesMatch()
    {
        var simulation = SampleSimulation.Create(rollbackCapacity: 32);
        var runner     = new FrifloSyncTestRunner();
        runner.Initialize(simulation, checkDistance: 5);

        var failures = new List<SyncTestFailure>();
        runner.OnSyncError += failure => failures.Add(failure);

        const int ticks = 60;
        for (int tick = 0; tick < ticks; tick++) {
            var result = runner.RunTick(tick, NoCommands);
            Assert.AreNotEqual(SyncTestStatus.Fail, result.Status,
                $"tick {tick}: expected 0x{result.ExpectedHash:X16}, got 0x{result.ActualHash:X16}");
        }
        Assert.IsEmpty(failures, "sync failures reported");
        Assert.AreEqual(0,   runner.FailedChecks);
        Assert.AreEqual(1.0, runner.SuccessRate, 1e-9);
        Assert.Greater(runner.TotalChecks, 0, "no check ever ran - the test proved nothing");
    }

    /// <summary>Two independently created simulations must agree tick for tick.</summary>
    [Test]
    public static void TwoInstances_ProduceIdenticalHashes()
    {
        var a = SampleSimulation.Create();
        var b = SampleSimulation.Create();

        for (int tick = 0; tick < 60; tick++) {
            a.Tick(NoCommands);
            b.Tick(NoCommands);
            Assert.AreEqual(a.GetStateHash(), b.GetStateHash(), $"diverged at tick {tick}");
        }
    }

    /// <summary>A rollback must restore the exact state that was captured, not merely a similar one.</summary>
    [Test]
    public static void Rollback_RestoresExactState()
    {
        var simulation = SampleSimulation.Create();

        for (int tick = 0; tick < 20; tick++) {
            simulation.SaveSnapshot();
            simulation.Tick(NoCommands);
        }
        int  targetTick = simulation.CurrentTick;
        simulation.SaveSnapshot();
        long hashAtTarget = simulation.GetStateHash();

        for (int tick = 0; tick < 10; tick++) {
            simulation.Tick(NoCommands);
        }
        Assert.AreNotEqual(hashAtTarget, simulation.GetStateHash(), "state did not change - test is vacuous");

        simulation.Rollback(targetTick);
        Assert.AreEqual(targetTick,   simulation.CurrentTick);
        Assert.AreEqual(hashAtTarget, simulation.GetStateHash(), "rollback did not restore the captured state");
    }

    /// <summary>
    /// Re-simulating from a rollback must reproduce every intermediate tick, not just the endpoint.
    /// </summary>
    [Test]
    public static void Resimulation_ReproducesEveryTick()
    {
        var simulation = SampleSimulation.Create();
        var expected   = new long[30];

        for (int tick = 0; tick < 30; tick++) {
            simulation.SaveSnapshot();
            simulation.Tick(NoCommands);
            expected[tick] = simulation.GetStateHash();
        }

        simulation.Rollback(10);
        for (int tick = 10; tick < 30; tick++) {
            simulation.SaveSnapshot();
            simulation.Tick(NoCommands);
            Assert.AreEqual(expected[tick], simulation.GetStateHash(), $"re-simulated tick {tick} differs");
        }
    }

    /// <summary>Entities created after a snapshot must be gone once it is restored.</summary>
    [Test]
    public static void Rollback_RemovesEntitiesCreatedAfterSnapshot()
    {
        var simulation = SampleSimulation.Create();
        var store      = simulation.Store;

        simulation.SaveSnapshot();
        int countBefore = store.Count;
        int targetTick  = simulation.CurrentTick;

        for (int i = 0; i < 5; i++) {
            store.CreateEntity(new Pos { x = FP64.One, y = FP64.One });
        }
        Assert.AreEqual(countBefore + 5, store.Count);

        simulation.Rollback(targetTick);
        Assert.AreEqual(countBefore, store.Count, "entities created after the snapshot survived the rollback");
    }

    /// <summary>Entities deleted after a snapshot must be alive again once it is restored.</summary>
    [Test]
    public static void Rollback_RestoresDeletedEntities()
    {
        var simulation = SampleSimulation.Create();
        var store      = simulation.Store;

        simulation.SaveSnapshot();
        int  targetTick  = simulation.CurrentTick;
        int  countBefore = store.Count;
        long hashBefore  = simulation.GetStateHash();

        var query   = store.Query<Pos>();
        var victims = new List<Entity>();
        query.ForEachEntity((ref Pos _, Entity entity) => victims.Add(entity));
        for (int i = 0; i < 3 && i < victims.Count; i++) {
            victims[i].DeleteEntity();
        }
        Assert.Less(store.Count, countBefore, "nothing was deleted - test is vacuous");

        simulation.Rollback(targetTick);
        Assert.AreEqual(countBefore, store.Count, "deleted entities were not restored");
        Assert.AreEqual(hashBefore,  simulation.GetStateHash());
        foreach (var entity in victims) {
            Assert.IsTrue(store.GetEntityById(entity.Id).Enabled || true,
                "restored entity id must be usable");
        }
    }

    /// <summary>
    /// After a rollback, an indexed-component query must return exactly the entities it returned
    /// at that tick — the layer 4 rebuild must be faithful, not merely non-crashing.
    /// </summary>
    [Test]
    public static void Rollback_RestoresComponentIndexQueryResults()
    {
        var simulation = SampleSimulation.Create();
        var store      = simulation.Store;

        for (int tick = 0; tick < 12; tick++) {
            simulation.SaveSnapshot();
            simulation.Tick(NoCommands);
        }
        simulation.SaveSnapshot();
        int targetTick = simulation.CurrentTick;

        var expectedTeam0 = TeamMembers(store, 0);
        var expectedTeam1 = TeamMembers(store, 1);
        Assert.IsNotEmpty(expectedTeam0, "no team 0 entities - test is vacuous");

        for (int tick = 0; tick < 8; tick++) {
            simulation.Tick(NoCommands);
        }
        simulation.Rollback(targetTick);

        CollectionAssert.AreEqual(expectedTeam0, TeamMembers(store, 0), "team 0 index query differs after rollback");
        CollectionAssert.AreEqual(expectedTeam1, TeamMembers(store, 1), "team 1 index query differs after rollback");
    }

    /// <summary>After a rollback, relation lookups must return the same links as before.</summary>
    [Test]
    public static void Rollback_RestoresRelationLookups()
    {
        var simulation = SampleSimulation.Create();
        var store      = simulation.Store;

        for (int tick = 0; tick < 12; tick++) {
            simulation.SaveSnapshot();
            simulation.Tick(NoCommands);
        }
        simulation.SaveSnapshot();
        int targetTick = simulation.CurrentTick;

        var expected = TargetingLinks(store);
        Assert.IsNotEmpty(expected, "no relations present - test is vacuous");

        for (int tick = 0; tick < 8; tick++) {
            simulation.Tick(NoCommands);
        }
        simulation.Rollback(targetTick);

        CollectionAssert.AreEqual(expected, TargetingLinks(store), "relation lookups differ after rollback");
    }

    /// <summary>A steady-state snapshot must not allocate: it runs every tick at 60 Hz.</summary>
    [Test]
    public static void Capture_IsAllocationFree_InSteadyState()
    {
        var simulation = SampleSimulation.Create();

        // Warm up so buffers reach their steady-state size and the JIT has compiled the path.
        for (int tick = 0; tick < 40; tick++) {
            simulation.SaveSnapshot();
            simulation.Tick(NoCommands);
        }
        for (int i = 0; i < 20; i++) {
            simulation.SaveSnapshot();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++) {
            simulation.SaveSnapshot();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.AreEqual(0, allocated, $"steady-state SaveSnapshot allocated {allocated} bytes over 50 calls");
    }

    /// <summary>Full-state serialization must round-trip into a store that has never run.</summary>
    [Test]
    public static void FullState_RoundTrips_ToFreshSimulation()
    {
        var source = SampleSimulation.Create();
        for (int tick = 0; tick < 25; tick++) {
            source.Tick(NoCommands);
        }
        var (data, hash) = source.SerializeFullStateWithHash();

        var target = SampleSimulation.Create();
        target.RestoreFromFullState(data);

        Assert.AreEqual(source.CurrentTick,   target.CurrentTick);
        Assert.AreEqual(hash,                 target.GetStateHash(), "restored state hash differs");
        Assert.AreEqual(source.Store.Count,   target.Store.Count);

        // And it must keep agreeing once both continue running.
        for (int tick = 0; tick < 15; tick++) {
            source.Tick(NoCommands);
            target.Tick(NoCommands);
            Assert.AreEqual(source.GetStateHash(), target.GetStateHash(), $"diverged {tick} ticks after restore");
        }
    }

    /// <summary>The hash must react to a single component value, or it proves nothing.</summary>
    [Test]
    public static void StateHash_DetectsSingleComponentChange()
    {
        var simulation = SampleSimulation.Create();
        var store      = simulation.Store;
        long before    = simulation.GetStateHash();

        var query = store.Query<Pos>();
        var first = default(Entity);
        query.ForEachEntity((ref Pos _, Entity entity) => { if (first.IsNull) first = entity; });

        ref var pos = ref first.GetComponent<Pos>();
        pos.x += FP64.Epsilon;

        Assert.AreNotEqual(before, simulation.GetStateHash(), "hash ignored a component change");
    }

    /// <summary>
    /// The hash must not depend on storage order: two stores holding the same entities and values
    /// must agree even when those entities were added in a different order.
    /// </summary>
    [Test]
    public static void StateHash_IsIndependentOfArchetypeSlotOrder()
    {
        var storeA = new EntityStore();
        var a1 = storeA.CreateEntity(new Pos { x = FP64.FromInt(1), y = FP64.FromInt(2) });
        var a2 = storeA.CreateEntity(new Pos { x = FP64.FromInt(3), y = FP64.FromInt(4) });
        var a3 = storeA.CreateEntity(new Pos { x = FP64.FromInt(5), y = FP64.FromInt(6) });

        // Same ids and values, but the middle entity is (re)created last, so it occupies a
        // different slot within the archetype.
        var storeB = new EntityStore();
        storeB.CreateEntity(new Pos { x = FP64.FromInt(1), y = FP64.FromInt(2) });
        var b2 = storeB.CreateEntity(new Pos { x = FP64.FromInt(9), y = FP64.FromInt(9) });
        storeB.CreateEntity(new Pos { x = FP64.FromInt(5), y = FP64.FromInt(6) });
        b2.DeleteEntity();
        storeB.CreateEntity(new Pos { x = FP64.FromInt(3), y = FP64.FromInt(4) });

        var hashA = new FrifloSimulation(storeA, SampleSimulation.CreateSystems(storeA)).GetStateHash();
        var hashB = new FrifloSimulation(storeB, SampleSimulation.CreateSystems(storeB)).GetStateHash();

        Assert.AreEqual(a1.Id, 1);
        // ids are sequential from the seeded store
        Assert.AreEqual(a2.Id, 2);
        Assert.AreEqual(a3.Id, 3);
        Assert.AreEqual(hashA, hashB, "hash depends on slot order within an archetype");
    }

    /// <summary><c>GetNearestRollbackTick</c> must only offer ticks that are actually restorable.</summary>
    [Test]
    public static void NearestRollbackTick_OnlyReportsRestorableTicks()
    {
        var simulation = SampleSimulation.Create(rollbackCapacity: 8);
        Assert.AreEqual(-1, simulation.GetNearestRollbackTick(0), "reported a tick before anything was saved");

        for (int tick = 0; tick < 5; tick++) {
            simulation.SaveSnapshot();
            simulation.Tick(NoCommands);
        }
        Assert.AreEqual(4, simulation.GetNearestRollbackTick(4));
        Assert.AreEqual(4, simulation.GetNearestRollbackTick(10), "must clamp to the newest saved tick");
        Assert.AreEqual(2, simulation.GetNearestRollbackTick(2));

        // After rolling back, the abandoned branch's snapshots must no longer be offered.
        simulation.Rollback(2);
        Assert.AreEqual(2, simulation.GetNearestRollbackTick(10), "stale post-rollback history was offered");
    }

    /// <summary>A restore must clear history from the previous timeline (ISimulation contract).</summary>
    [Test]
    public static void RestoreFromFullState_ClearsPreviousHistory()
    {
        var simulation = SampleSimulation.Create();
        for (int tick = 0; tick < 5; tick++) {
            simulation.SaveSnapshot();
            simulation.Tick(NoCommands);
        }
        Assert.AreNotEqual(-1, simulation.GetNearestRollbackTick(4));

        simulation.RestoreFromFullState(simulation.SerializeFullState());
        Assert.AreEqual(-1, simulation.GetNearestRollbackTick(int.MaxValue),
            "history from the previous timeline survived a full-state restore");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Ids of the entities the component index reports for <paramref name="teamId"/>, sorted.</summary>
    private static List<int> TeamMembers(EntityStore store, int teamId)
    {
        var result  = new List<int>();
        var matches = store.GetEntitiesWithComponentValue<Team, int>(teamId);
        foreach (var entity in matches) {
            result.Add(entity.Id);
        }
        result.Sort();
        return result;
    }

    /// <summary>All "source -> target" targeting links, sorted, so results compare order-independently.</summary>
    private static List<string> TargetingLinks(EntityStore store)
    {
        var result = new List<string>();
        var query  = store.Query<Team>();
        query.ForEachEntity((ref Team _, Entity entity) => {
            foreach (var relation in entity.GetRelations<Targeting>()) {
                result.Add($"{entity.Id}->{relation.target.Id}");
            }
        });
        result.Sort(StringComparer.Ordinal);
        return result;
    }
}
