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
/// Rollback must restore the parent/child hierarchy: parent links, child order, and the pooled
/// child-list allocator behind <see cref="TreeNode"/>.
/// </summary>
/// <remarks>
/// A <see cref="TreeNode"/> stores an <c>IdArray</c>, i.e. a <c>(start, count)</c> handle into a
/// pooled allocator. <c>start</c> is allocation history, not state, so it can differ between two
/// peers holding the very same tree. These tests therefore assert on the child ids and parents
/// that the API reports, never on the raw component.
/// </remarks>
public static class Test_HierarchyRollback
{
    private static readonly List<ICommand> NoCommands = new();

    private static Entity NewEntity(EntityStore store) =>
        store.CreateEntity(new Pos { x = FP64.One, y = FP64.One });

    /// <summary>Children added after a snapshot must be detached once it is restored.</summary>
    [Test]
    public static void Rollback_RemovesChildrenAddedAfterSnapshot()
    {
        var simulation = SampleSimulation.Create();
        var store      = simulation.Store;
        var parent     = NewEntity(store);
        var child      = NewEntity(store);

        simulation.SaveSnapshot();
        int targetTick = simulation.CurrentTick;

        parent.AddChild(child);
        Assert.AreEqual(1, parent.ChildCount, "child was not added - test is vacuous");

        simulation.Rollback(targetTick);
        Assert.AreEqual(0, parent.ChildCount,   "child added after the snapshot survived the rollback");
        Assert.IsTrue(child.Parent.IsNull,      "parent link was not cleared");
    }

    /// <summary>Children removed after a snapshot must be reattached once it is restored.</summary>
    [Test]
    public static void Rollback_RestoresRemovedChildren()
    {
        var simulation = SampleSimulation.Create();
        var store      = simulation.Store;
        var parent     = NewEntity(store);
        var child      = NewEntity(store);
        parent.AddChild(child);

        simulation.SaveSnapshot();
        int targetTick = simulation.CurrentTick;

        parent.RemoveChild(child);
        Assert.AreEqual(0, parent.ChildCount, "child was not removed - test is vacuous");

        simulation.Rollback(targetTick);
        Assert.AreEqual(1,        parent.ChildCount, "removed child was not restored");
        Assert.AreEqual(child.Id, ChildIds(parent)[0]);
        Assert.AreEqual(parent.Id, child.Parent.Id,  "parent link was not restored");
    }

    /// <summary>Child order is meaningful and must round-trip exactly.</summary>
    [Test]
    public static void Rollback_RestoresChildOrder()
    {
        var simulation = SampleSimulation.Create();
        var store      = simulation.Store;
        var parent     = NewEntity(store);
        var a = NewEntity(store);
        var b = NewEntity(store);
        var c = NewEntity(store);
        parent.AddChild(a);
        parent.AddChild(b);
        parent.AddChild(c);

        simulation.SaveSnapshot();
        int targetTick = simulation.CurrentTick;
        var expected   = ChildIds(parent);
        CollectionAssert.AreEqual(new[] { a.Id, b.Id, c.Id }, expected);

        // Reorder: move the last child to the front.
        parent.RemoveChild(c);
        parent.InsertChild(0, c);
        CollectionAssert.AreNotEqual(expected, ChildIds(parent), "order did not change - test is vacuous");

        simulation.Rollback(targetTick);
        CollectionAssert.AreEqual(expected, ChildIds(parent), "child order was not restored");
    }

    /// <summary>A deeper tree must round-trip at every level.</summary>
    [Test]
    public static void Rollback_RestoresNestedHierarchy()
    {
        var simulation = SampleSimulation.Create();
        var store      = simulation.Store;
        var root   = NewEntity(store);
        var mid    = NewEntity(store);
        var leaf1  = NewEntity(store);
        var leaf2  = NewEntity(store);
        root.AddChild(mid);
        mid.AddChild(leaf1);
        mid.AddChild(leaf2);

        simulation.SaveSnapshot();
        int targetTick = simulation.CurrentTick;

        // Flatten the tree.
        mid.RemoveChild(leaf1);
        mid.RemoveChild(leaf2);
        root.RemoveChild(mid);
        Assert.AreEqual(0, root.ChildCount);

        simulation.Rollback(targetTick);

        CollectionAssert.AreEqual(new[] { mid.Id },           ChildIds(root));
        CollectionAssert.AreEqual(new[] { leaf1.Id, leaf2.Id }, ChildIds(mid));
        Assert.AreEqual(root.Id, mid.Parent.Id);
        Assert.AreEqual(mid.Id,  leaf1.Parent.Id);
        Assert.AreEqual(mid.Id,  leaf2.Parent.Id);
    }

    /// <summary>Repeated rollbacks onto one snapshot must not accumulate children.</summary>
    [Test]
    public static void Rollback_DoesNotDuplicateChildren()
    {
        var simulation = SampleSimulation.Create();
        var store      = simulation.Store;
        var parent     = NewEntity(store);
        var child      = NewEntity(store);
        parent.AddChild(child);

        simulation.SaveSnapshot();
        int targetTick = simulation.CurrentTick;

        for (int i = 0; i < 3; i++) {
            simulation.Rollback(targetTick);
            simulation.SaveSnapshot();
        }
        Assert.AreEqual(1, parent.ChildCount, "rollback duplicated the child list");
        CollectionAssert.AreEqual(new[] { child.Id }, ChildIds(parent));
    }

    /// <summary>The state hash must react to hierarchy changes, or a tree desync goes undetected.</summary>
    [Test]
    public static void StateHash_CoversHierarchy()
    {
        var simulation = SampleSimulation.Create();
        var store      = simulation.Store;
        var parent     = NewEntity(store);
        var child      = NewEntity(store);

        long detached = simulation.GetStateHash();
        parent.AddChild(child);
        long attached = simulation.GetStateHash();

        Assert.AreNotEqual(detached, attached,
            "state hash ignores the hierarchy - tree desyncs would go undetected");
    }

    /// <summary>
    /// Two stores holding the same tree must hash equally even when their child-list allocators
    /// were driven to different internal offsets.
    /// </summary>
    /// <remarks>
    /// This is what makes hashing <c>TreeNode</c> by content rather than by bytes necessary: the
    /// <c>IdArray.start</c> offsets below genuinely differ between the two stores.
    /// </remarks>
    [Test]
    public static void StateHash_IsIndependentOfChildAllocatorHistory()
    {
        var storeA = BuildTree(out _);

        // Same final tree, but reached after churning the pool with add/remove cycles.
        var storeB = BuildTree(out var parentB);
        var churn  = new List<Entity>();
        for (int i = 0; i < 5; i++) {
            churn.Add(storeB.CreateEntity(new Pos { x = FP64.One, y = FP64.One }));
        }
        foreach (var entity in churn) parentB.AddChild(entity);
        foreach (var entity in churn) parentB.RemoveChild(entity);
        foreach (var entity in churn) entity.DeleteEntity();

        var hashA = new FrifloSimulation(storeA, SampleSimulation.CreateSystems(storeA)).GetStateHash();
        var hashB = new FrifloSimulation(storeB, SampleSimulation.CreateSystems(storeB)).GetStateHash();

        Assert.AreEqual(hashA, hashB,
            "hash depends on child-list allocator offsets, which are allocation history rather than state");
    }

    /// <summary>Re-simulating across a rollback must reproduce hierarchy mutations too.</summary>
    [Test]
    public static void Resimulation_ReproducesHierarchy()
    {
        var simulation = SampleSimulation.Create();
        var store      = simulation.Store;
        var parent     = NewEntity(store);
        var children   = new List<Entity>();
        for (int i = 0; i < 4; i++) {
            children.Add(NewEntity(store));
        }

        simulation.SaveSnapshot();
        int targetTick = simulation.CurrentTick;

        foreach (var child in children) parent.AddChild(child);
        long expected = simulation.GetStateHash();
        var  expectedIds = ChildIds(parent);

        simulation.Rollback(targetTick);
        Assert.AreEqual(0, parent.ChildCount);

        // Replay the same mutations: the result must be identical, not merely similar.
        foreach (var child in children) parent.AddChild(child);
        Assert.AreEqual(expected, simulation.GetStateHash(), "re-simulated hierarchy differs");
        CollectionAssert.AreEqual(expectedIds, ChildIds(parent));
    }

    private static EntityStore BuildTree(out Entity parent)
    {
        var store = new EntityStore();
        parent = store.CreateEntity(new Pos { x = FP64.One, y = FP64.One });
        for (int i = 0; i < 3; i++) {
            var child = store.CreateEntity(new Pos { x = FP64.FromInt(i), y = FP64.One });
            parent.AddChild(child);
        }
        return store;
    }

    private static List<int> ChildIds(Entity entity)
    {
        var result = new List<int>();
        foreach (var child in entity.ChildEntities) {
            result.Add(child.Id);
        }
        return result;
    }
}
