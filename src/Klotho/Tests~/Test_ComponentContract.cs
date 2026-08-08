using System;
using Friflo.Engine.ECS;
using Klotho.Friflo;
using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;

// ReSharper disable once CheckNamespace
namespace Klotho.Friflo.Tests;

/// <summary>
/// Pins down which component shapes may take part in a deterministic simulation.
/// </summary>
/// <remarks>
/// The rule is about <i>reproducibility</i>, not about references as such:
/// <list type="bullet">
///   <item>An <see cref="Entity"/> field is fine. Its 16 bytes are a store reference plus
///         <c>Id</c> and <c>Revision</c>; only the identity pair is hashed, and the reference is
///         re-bound to the local store on restore. This has to work — <c>ILinkRelation</c>
///         requires an <see cref="Entity"/>, since it is the relation key.</item>
///   <item>A class-typed field is rejected. Its bytes are a heap address that differs between
///         processes and peers, and the object it points to is not part of the store's state, so
///         no snapshot can reproduce it on the other side.</item>
/// </list>
/// </remarks>
public static class Test_ComponentContract
{
    /// <summary>A component holding an entity reference — the shape every link relation has.</summary>
    private struct EntityRef : IComponent
    {
        public Entity other;
        public int    weight;
    }

    /// <summary>A component holding a managed reference. Not reproducible across peers.</summary>
    private struct ClassRef : IComponent
    {
        public string label;
    }

    /// <summary>A component whose nested struct holds a managed reference.</summary>
    private struct NestedClassRef : IComponent
    {
        public Inner inner;
        internal struct Inner { public int[] values; }
    }

    /// <summary>Entity-bearing components hash by identity, so equal identities hash equally.</summary>
    [Test]
    public static void EntityField_IsHashedByIdentity()
    {
        var storeA = new EntityStore();
        var targetA = storeA.CreateEntity();
        storeA.CreateEntity(new EntityRef { other = targetA, weight = 7 });

        // A separate store: same ids and values, but every object address differs.
        var storeB = new EntityStore();
        var targetB = storeB.CreateEntity();
        storeB.CreateEntity(new EntityRef { other = targetB, weight = 7 });

        var hashA = new FrifloSimulation(storeA, SampleSimulation.CreateSystems(storeA)).GetStateHash();
        var hashB = new FrifloSimulation(storeB, SampleSimulation.CreateSystems(storeB)).GetStateHash();

        Assert.AreEqual(targetA.Id, targetB.Id, "precondition: both stores assign the same ids");
        Assert.AreEqual(hashA, hashB,
            "two peers in the same logical state disagreed - the store reference leaked into the hash");
    }

    /// <summary>Pointing at a different entity must change the hash.</summary>
    [Test]
    public static void EntityField_DetectsRetarget()
    {
        var store = new EntityStore();
        var first  = store.CreateEntity();
        var second = store.CreateEntity();
        var holder = store.CreateEntity(new EntityRef { other = first, weight = 1 });

        var simulation = new FrifloSimulation(store, SampleSimulation.CreateSystems(store));
        long before = simulation.GetStateHash();

        holder.GetComponent<EntityRef>().other = second;

        Assert.AreNotEqual(before, simulation.GetStateHash(), "hash ignored a retargeted entity reference");
    }

    /// <summary>An entity reference must survive a rollback with its identity intact.</summary>
    [Test]
    public static void EntityField_SurvivesRollback()
    {
        var store  = new EntityStore();
        var target = store.CreateEntity();
        var holder = store.CreateEntity(new EntityRef { other = target, weight = 3 });
        var other  = store.CreateEntity();

        var simulation = new FrifloSimulation(store, SampleSimulation.CreateSystems(store));
        simulation.SaveSnapshot();
        int targetTick = simulation.CurrentTick;

        holder.GetComponent<EntityRef>().other = other;
        simulation.Rollback(targetTick);

        ref var restored = ref holder.GetComponent<EntityRef>();
        Assert.AreEqual(target.Id, restored.other.Id, "entity reference was not restored");
        Assert.AreEqual(3,         restored.weight);
        Assert.AreSame(store, restored.other.Store, "restored entity was not bound to the local store");
    }

    /// <summary>A class-typed field must be rejected, naming the offending component.</summary>
    [Test]
    public static void ClassField_IsRejected()
    {
        var store = new EntityStore();
        store.CreateEntity(new ClassRef { label = "not reproducible" });
        var simulation = new FrifloSimulation(store, SampleSimulation.CreateSystems(store));

        var exception = Assert.Throws<NotSupportedException>(() => simulation.GetStateHash());
        StringAssert.Contains(nameof(ClassRef), exception.Message,
            "the error should name the component the developer has to fix");
    }

    /// <summary>A reference nested inside a struct field must be rejected as well.</summary>
    [Test]
    public static void NestedClassField_IsRejected()
    {
        var store = new EntityStore();
        store.CreateEntity(new NestedClassRef { inner = new NestedClassRef.Inner { values = new[] { 1 } } });
        var simulation = new FrifloSimulation(store, SampleSimulation.CreateSystems(store));

        Assert.Throws<NotSupportedException>(() => simulation.GetStateHash(),
            "a reference hidden one level down was not detected");
    }

    /// <summary>Plain unmanaged components take the fast byte path and are unaffected.</summary>
    [Test]
    public static void UnmanagedComponent_IsAccepted()
    {
        var store = new EntityStore();
        store.CreateEntity(new Pos { x = FP64.One, y = FP64.Half });
        var simulation = new FrifloSimulation(store, SampleSimulation.CreateSystems(store));

        Assert.DoesNotThrow(() => simulation.GetStateHash());
    }
}
