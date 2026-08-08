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
/// Rollback must restore relations completely: the relation values themselves, the outgoing
/// lookups, and the incoming (reverse) link lookups.
/// </summary>
/// <remarks>
/// Relation data lives in an archetype that is NOT registered in <c>store.archs[]</c>
/// (<c>AbstractEntityRelations</c> builds it with <c>new Archetype(...)</c>, bypassing
/// <c>AddArchetype</c>). A snapshot that only walks <c>store.archs[]</c> therefore drops every
/// relation silently — and, because the state hash walks the same archetypes, the resulting
/// corruption does not change the hash and desync detection never fires. These tests assert on
/// the relations directly rather than on the hash, so they fail even in that blind spot.
/// </remarks>
public static class Test_RelationRollback
{
    private static readonly List<ICommand> NoCommands = new();

    /// <summary>A non-link relation keyed by an int, to cover the plain relation path too.</summary>
    private struct Carrying : IRelation<int>
    {
        public int  slot;
        public int  itemId;
        public int GetRelationKey() => slot;
    }

    /// <summary>Relation values added after a snapshot must be gone once it is restored.</summary>
    [Test]
    public static void Rollback_RemovesRelationsAddedAfterSnapshot()
    {
        var simulation = SampleSimulation.Create();
        var store      = simulation.Store;

        var a = store.CreateEntity(new Pos { x = FP64.One, y = FP64.One });
        var b = store.CreateEntity(new Pos { x = FP64.One, y = FP64.One });

        simulation.SaveSnapshot();
        int targetTick = simulation.CurrentTick;

        a.AddRelation(new Targeting { target = b });
        Assert.AreEqual(1, a.GetRelations<Targeting>().Length, "relation was not added - test is vacuous");

        simulation.Rollback(targetTick);
        Assert.AreEqual(0, a.GetRelations<Targeting>().Length,
            "relation added after the snapshot survived the rollback");
    }

    /// <summary>Relation values removed after a snapshot must be back once it is restored.</summary>
    [Test]
    public static void Rollback_RestoresRemovedRelations()
    {
        var simulation = SampleSimulation.Create();
        var store      = simulation.Store;

        var a = store.CreateEntity(new Pos { x = FP64.One, y = FP64.One });
        var b = store.CreateEntity(new Pos { x = FP64.One, y = FP64.One });
        a.AddRelation(new Targeting { target = b });

        simulation.SaveSnapshot();
        int targetTick = simulation.CurrentTick;

        a.RemoveRelation<Targeting>(b);
        Assert.AreEqual(0, a.GetRelations<Targeting>().Length, "relation was not removed - test is vacuous");

        simulation.Rollback(targetTick);
        var restored = a.GetRelations<Targeting>();
        Assert.AreEqual(1,    restored.Length, "removed relation was not restored");
        Assert.AreEqual(b.Id, restored[0].target.Id, "restored relation points at the wrong entity");
    }

    /// <summary>
    /// The reverse lookup (<c>GetIncomingLinks</c>) must be restored too, not just the forward one.
    /// </summary>
    [Test]
    public static void Rollback_RestoresIncomingLinks()
    {
        var simulation = SampleSimulation.Create();
        var store      = simulation.Store;

        var target  = store.CreateEntity(new Pos { x = FP64.One, y = FP64.One });
        var source1 = store.CreateEntity(new Pos { x = FP64.One, y = FP64.One });
        var source2 = store.CreateEntity(new Pos { x = FP64.One, y = FP64.One });
        source1.AddRelation(new Targeting { target = target });
        source2.AddRelation(new Targeting { target = target });

        simulation.SaveSnapshot();
        int targetTick = simulation.CurrentTick;
        var expected   = IncomingIds(target);
        CollectionAssert.AreEqual(new[] { source1.Id, source2.Id }, expected);

        // Drop one link, then roll back.
        source1.RemoveRelation<Targeting>(target);
        Assert.AreEqual(1, IncomingIds(target).Count, "link was not removed - test is vacuous");

        simulation.Rollback(targetTick);
        CollectionAssert.AreEqual(expected, IncomingIds(target),
            "incoming link lookup was not restored");
    }

    /// <summary>Restoring must not leave duplicate entries behind in the lookup tables.</summary>
    [Test]
    public static void Rollback_DoesNotDuplicateRelations()
    {
        var simulation = SampleSimulation.Create();
        var store      = simulation.Store;

        var a = store.CreateEntity(new Pos { x = FP64.One, y = FP64.One });
        var b = store.CreateEntity(new Pos { x = FP64.One, y = FP64.One });
        a.AddRelation(new Targeting { target = b });

        simulation.SaveSnapshot();
        int targetTick = simulation.CurrentTick;

        // Roll back repeatedly onto the same snapshot: a rebuild that appends instead of
        // replacing would accumulate entries here.
        for (int i = 0; i < 3; i++) {
            simulation.Rollback(targetTick);
            simulation.SaveSnapshot();
        }
        Assert.AreEqual(1, a.GetRelations<Targeting>().Length, "relation was duplicated by rollback");
        Assert.AreEqual(1, IncomingIds(b).Count,              "incoming link was duplicated by rollback");
    }

    /// <summary>Multi-relation state (several relations per entity) must round-trip exactly.</summary>
    [Test]
    public static void Rollback_RestoresMultipleRelationsPerEntity()
    {
        var simulation = SampleSimulation.Create();
        var store      = simulation.Store;

        var carrier = store.CreateEntity(new Pos { x = FP64.One, y = FP64.One });
        carrier.AddRelation(new Carrying { slot = 0, itemId = 100 });
        carrier.AddRelation(new Carrying { slot = 1, itemId = 200 });
        carrier.AddRelation(new Carrying { slot = 2, itemId = 300 });

        simulation.SaveSnapshot();
        int targetTick = simulation.CurrentTick;

        carrier.RemoveRelation<Carrying, int>(1);
        carrier.AddRelation(new Carrying { slot = 3, itemId = 400 });
        Assert.AreEqual(3, carrier.GetRelations<Carrying>().Length);

        simulation.Rollback(targetTick);

        var relations = carrier.GetRelations<Carrying>();
        Assert.AreEqual(3, relations.Length, "relation count differs after rollback");
        var bySlot = new Dictionary<int, int>();
        foreach (var relation in relations) {
            bySlot[relation.slot] = relation.itemId;
        }
        Assert.AreEqual(100, bySlot[0]);
        Assert.AreEqual(200, bySlot[1], "the removed relation was not restored with its value");
        Assert.AreEqual(300, bySlot[2]);
        Assert.IsFalse(bySlot.ContainsKey(3), "relation added after the snapshot survived");
    }

    /// <summary>
    /// The state hash must react to relation data, so a desync in relations is actually detectable.
    /// </summary>
    /// <remarks>
    /// This is the assertion that fails if relation archetypes are left out of the hash: without
    /// it, relation corruption is invisible to Klotho's desync detection.
    /// </remarks>
    [Test]
    public static void StateHash_CoversRelationData()
    {
        var simulation = SampleSimulation.Create();
        var store      = simulation.Store;

        var a = store.CreateEntity(new Pos { x = FP64.One, y = FP64.One });
        var b = store.CreateEntity(new Pos { x = FP64.One, y = FP64.One });
        var c = store.CreateEntity(new Pos { x = FP64.One, y = FP64.One });

        a.AddRelation(new Targeting { target = b });
        long hashTargetingB = simulation.GetStateHash();

        // Re-point the relation. Nothing else in the store changes.
        a.RemoveRelation<Targeting>(b);
        a.AddRelation(new Targeting { target = c });
        long hashTargetingC = simulation.GetStateHash();

        Assert.AreNotEqual(hashTargetingB, hashTargetingC,
            "state hash ignores relation data - relation desyncs would go undetected");
    }

    /// <summary>Deleting the target of a link must be undone by a rollback, links included.</summary>
    [Test]
    public static void Rollback_RestoresLinksAfterTargetDeleted()
    {
        var simulation = SampleSimulation.Create();
        var store      = simulation.Store;

        var target = store.CreateEntity(new Pos { x = FP64.One, y = FP64.One });
        var source = store.CreateEntity(new Pos { x = FP64.One, y = FP64.One });
        source.AddRelation(new Targeting { target = target });

        simulation.SaveSnapshot();
        int targetTick = simulation.CurrentTick;
        int targetId   = target.Id;

        // Deleting the target also clears the link from the source (Friflo cascades this).
        target.DeleteEntity();
        Assert.AreEqual(0, source.GetRelations<Targeting>().Length, "link was not cleared - test is vacuous");

        simulation.Rollback(targetTick);

        var restored = source.GetRelations<Targeting>();
        Assert.AreEqual(1,        restored.Length,        "link to the deleted target was not restored");
        Assert.AreEqual(targetId, restored[0].target.Id,  "restored link points at the wrong entity");
        CollectionAssert.AreEqual(new[] { source.Id }, IncomingIds(store.GetEntityById(targetId)),
            "incoming link of the restored target was not rebuilt");
    }

    /// <summary>Ids of the entities linking to <paramref name="target"/>, sorted.</summary>
    private static List<int> IncomingIds(Entity target)
    {
        var result = new List<int>();
        foreach (var link in target.GetIncomingLinks<Targeting>()) {
            result.Add(link.Entity.Id);
        }
        result.Sort();
        return result;
    }
}
