using System;
using Friflo.Engine.ECS;
using Klotho.Friflo;
using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;

// ReSharper disable once CheckNamespace
namespace Klotho.Friflo.Tests;

/// <summary>
/// Exercises the reflection-interpreted layout path directly — the path NativeAOT uses when
/// dynamic code is unavailable.
/// </summary>
/// <remarks>
/// This test suite always runs with a JIT, so <c>DeterministicLayout&lt;T&gt;</c> takes its
/// compiled path and the reflection fallback would otherwise never be executed here. Calling
/// <see cref="DeterministicPlan"/> directly keeps the fallback covered by ordinary
/// <c>dotnet test</c> runs, so it cannot rot between AOT publishes.
///
/// The end-to-end AOT check lives in <c>src/Klotho/AotSmoke</c>, which is compiled with
/// <c>PublishAot</c> so <c>RuntimeFeature.IsDynamicCodeSupported</c> is genuinely false.
/// </remarks>
public static class Test_AotFallbackPath
{
    private struct Linked : IComponent
    {
        public Entity target;
        public int    weight;
        public FP64   scale;
    }

    private struct Nested : IComponent
    {
        public Inner inner;
        public bool  flag;
        internal struct Inner { public Entity entity; public long value; }
    }

    private class SampleScript : Script
    {
        public int    counter;
        public Entity peer;
    }

    /// <summary>Compiled and interpreted paths must agree on the hash, or AOT peers desync from JIT peers.</summary>
    [Test]
    public static void InterpretedHash_MatchesCompiledHash()
    {
        var store  = new EntityStore();
        var target = store.CreateEntity();
        var value  = new Linked { target = target, weight = 42, scale = FP64.Half };

        ulong compiled    = DeterministicLayout<Linked>.Hash(value);
        ulong interpreted = DeterministicPlan.Get(typeof(Linked)).HashObject(value);

        Assert.AreEqual(compiled, interpreted,
            "an AOT client and a JIT client would compute different hashes for the same state");
    }

    /// <summary>ID recycling must not make AOT and JIT peers disagree when the short revision wraps.</summary>
    [Test]
    public static void RecycledEntityWithNegativeRevision_HasIdenticalLayouts()
    {
        var store = new EntityStore();
        var entity = store.CreateEntity();
        for (int i = 0; i <= short.MaxValue; i++) {
            entity.DeleteEntity();
            entity = store.CreateEntity();
        }
        Assert.AreEqual(short.MinValue, entity.Revision, "precondition: revision crossed its signed boundary");
        var value = new Linked { target = entity, weight = -7, scale = FP64.Half };
        var plan = DeterministicPlan.Get(typeof(Linked));

        Assert.AreEqual(DeterministicLayout<Linked>.Hash(value), plan.HashObject(value),
            "AOT and JIT disagree about the same recycled entity identity");
        var compiled = new ulong[DeterministicLayout<Linked>.LeafCount];
        var interpreted = new ulong[plan.Leaves.Length];
        DeterministicLayout<Linked>.Write(value, compiled, 0);
        plan.WriteObject(value, interpreted, 0);
        CollectionAssert.AreEqual(compiled, interpreted,
            "negative revisions must have the same FullState representation on both paths");
    }

    /// <summary>The same must hold for a reference nested inside a struct field.</summary>
    [Test]
    public static void InterpretedHash_MatchesCompiledHash_Nested()
    {
        var store  = new EntityStore();
        var entity = store.CreateEntity();
        var value  = new Nested { inner = new Nested.Inner { entity = entity, value = -7 }, flag = true };

        Assert.AreEqual(DeterministicLayout<Nested>.Hash(value),
                        DeterministicPlan.Get(typeof(Nested)).HashObject(value));
    }

    /// <summary>Interpreted write/read must round-trip a value, entity references included.</summary>
    [Test]
    public static void InterpretedWriteRead_RoundTrips()
    {
        var store  = new EntityStore();
        var target = store.CreateEntity();
        var plan   = DeterministicPlan.Get(typeof(Linked));

        var original = new Linked { target = target, weight = 99, scale = FP64.FromInt(3) };
        var leaves   = new ulong[plan.Leaves.Length];
        plan.WriteObject(original, leaves, 0);

        object restored = new Linked();
        plan.ReadObject(ref restored, leaves, 0, store);

        var result = (Linked)restored;
        Assert.AreEqual(target.Id,        result.target.Id, "entity id was not restored");
        Assert.AreEqual(target.Revision,  result.target.Revision);
        Assert.AreEqual(99,               result.weight);
        Assert.AreEqual(FP64.FromInt(3),  result.scale);
        Assert.AreSame(store, result.target.Store, "entity was not bound to the target store");
    }

    /// <summary>A value nested one level down must round-trip too — struct copies make this fragile.</summary>
    [Test]
    public static void InterpretedWriteRead_RoundTrips_Nested()
    {
        var store  = new EntityStore();
        var entity = store.CreateEntity();
        var plan   = DeterministicPlan.Get(typeof(Nested));

        var original = new Nested { inner = new Nested.Inner { entity = entity, value = 1234 }, flag = true };
        var leaves   = new ulong[plan.Leaves.Length];
        plan.WriteObject(original, leaves, 0);

        object restored = new Nested();
        plan.ReadObject(ref restored, leaves, 0, store);

        var result = (Nested)restored;
        Assert.AreEqual(entity.Id, result.inner.entity.Id, "nested entity was not written back into the outer struct");
        Assert.AreEqual(1234,      result.inner.value);
        Assert.IsTrue(result.flag);
    }

    /// <summary>Scripts always use the interpreted path, since a script type is only known at run time.</summary>
    [Test]
    public static void InterpretedPath_HandlesScripts()
    {
        var store  = new EntityStore();
        var peer   = store.CreateEntity();
        var script = new SampleScript { counter = 5, peer = peer };

        ulong[] leaves = null;
        ScriptLayout.Capture(script, ref leaves);
        ulong before = ScriptLayout.Hash(script);

        script.counter = 500;
        script.peer    = default;
        Assert.AreNotEqual(before, ScriptLayout.Hash(script), "hash ignored the change - test is vacuous");

        ScriptLayout.Restore(script, leaves, store);
        Assert.AreEqual(5,       script.counter, "script field was not restored");
        Assert.AreEqual(peer.Id, script.peer.Id, "entity reference in the script was not restored");
        Assert.AreEqual(before,  ScriptLayout.Hash(script));
    }

    /// <summary>The rejection rule must be identical on both paths.</summary>
    [Test]
    public static void InterpretedPath_RejectsClassFields()
    {
        Assert.Throws<NotSupportedException>(() => DeterministicPlan.Get(typeof(WithClassField)));
    }

    private struct WithClassField : IComponent
    {
        public string label;
    }
}
