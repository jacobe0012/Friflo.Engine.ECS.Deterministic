using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using Klotho.Friflo;
using NUnit.Framework;

// ReSharper disable once CheckNamespace
namespace Klotho.Friflo.Tests;

/// <summary>
/// Covers public EntityStore state that lives outside archetypes but changes Friflo API behaviour.
/// </summary>
public static class Test_StoreMetadataRollback
{
    private struct RootMarker : IComponent { public int value; }

    private static FrifloSimulation Simulation(EntityStore store) =>
        new(store, new SystemRoot(store));

    [Test]
    public static void Rollback_RemovesAStoreRootAssignedOnTheAbandonedBranch()
    {
        var store = new EntityStore();
        var root  = store.CreateEntity(new RootMarker { value = 1 });
        var simulation = Simulation(store);
        simulation.SaveSnapshot();

        store.SetStoreRoot(root);
        Assert.AreEqual(root, store.StoreRoot, "precondition: root assignment failed");

        simulation.Rollback(0);

        Assert.IsTrue(store.StoreRoot.IsNull,
            "StoreRoot assigned after the snapshot survived rollback");
    }

    [Test]
    public static void Rollback_RestoresTheRecycleIdsPolicy()
    {
        var store = new EntityStore { RecycleIds = false };
        store.CreateEntity(new RootMarker { value = 1 });
        var simulation = Simulation(store);
        simulation.SaveSnapshot();

        store.RecycleIds = true;
        simulation.Rollback(0);

        Assert.IsFalse(store.RecycleIds,
            "rollback restored the allocator contents but not the public policy that decides whether to use them");
    }

    [Test]
    public static void FullState_RestoresStoreRootAndRecycleIdsPolicy()
    {
        var sourceStore = new EntityStore { RecycleIds = false };
        var sourceRoot  = sourceStore.CreateEntity(new RootMarker { value = 7 });
        sourceStore.SetStoreRoot(sourceRoot);
        var source = Simulation(sourceStore);

        var targetStore = new EntityStore { RecycleIds = true };
        targetStore.CreateEntity(new RootMarker { value = -1 });
        var target = Simulation(targetStore);
        target.RestoreFromFullState(source.SerializeFullState());

        Assert.IsFalse(targetStore.RecycleIds);
        Assert.AreEqual(sourceRoot.Id,       targetStore.StoreRoot.Id);
        Assert.AreEqual(sourceRoot.Revision, targetStore.StoreRoot.Revision);
        Assert.AreSame(targetStore, targetStore.StoreRoot.Store,
            "FullState root must be rebound to the receiving store");
        Assert.AreEqual(source.GetStateHash(), target.GetStateHash());
    }
}
