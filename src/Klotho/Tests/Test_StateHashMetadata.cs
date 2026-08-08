using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using Klotho.Friflo;
using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;

// ReSharper disable once CheckNamespace
namespace Klotho.Friflo.Tests;

/// <summary>
/// The sync hash must cover state that determines future entity allocation, not only the entities
/// that happen to be alive at the instant the hash is taken.
/// </summary>
public static class Test_StateHashMetadata
{
    private struct Marker : IComponent
    {
        public FP64 value;
    }

    private static FrifloSimulation Simulation(EntityStore store) =>
        new(store, new SystemRoot(store));

    /// <summary>
    /// Two stores can have identical live entities but different recycle-stack order. Their next
    /// CreateEntity then returns different ids, so treating the pre-create states as equal lets a
    /// latent desync pass server validation.
    /// </summary>
    [Test]
    public static void StateHash_CoversRecycleOrderThatControlsTheNextEntityId()
    {
        var storeA = new EntityStore();
        var storeB = new EntityStore();
        var a = new Entity[4];
        var b = new Entity[4];
        for (int i = 0; i < 4; i++) {
            var marker = new Marker { value = FP64.FromInt(i + 1) };
            a[i] = storeA.CreateEntity(marker);
            b[i] = storeB.CreateEntity(marker);
        }

        // Same survivors (1 and 4), same slot order, different LIFO recycle order.
        a[1].DeleteEntity();
        a[2].DeleteEntity();
        b[2].DeleteEntity();
        b[1].DeleteEntity();

        var simulationA = Simulation(storeA);
        var simulationB = Simulation(storeB);
        Assert.AreEqual(2, storeA.Count);
        Assert.AreEqual(2, storeB.Count);
        Assert.AreNotEqual(simulationA.GetStateHash(), simulationB.GetStateHash(),
            "the hash ignored recycle-stack order even though it determines the next entity id");

        var nextA = storeA.CreateEntity(new Marker { value = FP64.FromInt(99) });
        var nextB = storeB.CreateEntity(new Marker { value = FP64.FromInt(99) });
        Assert.AreNotEqual(nextA.Id, nextB.Id,
            "precondition failed: the deliberately different recycle order did not affect allocation");
    }

    /// <summary>
    /// A live entity's revision participates in Entity identity and is serialized in FullState.
    /// It therefore has to participate in the validation hash as well.
    /// </summary>
    [Test]
    public static void StateHash_CoversLiveEntityRevision()
    {
        var storeA = new EntityStore();
        var oldA   = storeA.CreateEntity(new Marker { value = FP64.One });
        oldA.DeleteEntity();
        var liveA = storeA.CreateEntity(new Marker { value = FP64.One });

        var storeB = new EntityStore();
        var liveB  = storeB.CreateEntity(new Marker { value = FP64.One });

        Assert.AreEqual(liveA.Id, liveB.Id, "precondition: both stores expose the same live entity id");
        Assert.AreNotEqual(liveA.Revision, liveB.Revision,
            "precondition: one id must have passed through deletion/reuse");
        Assert.AreNotEqual(Simulation(storeA).GetStateHash(), Simulation(storeB).GetStateHash(),
            "the hash ignored Entity revision, so stale-handle semantics could diverge undetected");
    }
}
