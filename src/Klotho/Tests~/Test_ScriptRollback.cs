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
/// Rollback must restore <see cref="Script"/> state: which entity owns which script, and the
/// deterministic field values of each instance.
/// </summary>
/// <remarks>
/// Scripts are class instances living outside the archetypes, so they cannot be bulk-copied like
/// components. The snapshot keeps each instance and moves its field values in and out — which
/// also means references the game holds to a script stay valid across a rollback.
/// </remarks>
public static class Test_ScriptRollback
{
    private static readonly List<ICommand> NoCommands = new();

    /// <summary>A script carrying plain deterministic state.</summary>
    private class Cooldown : Script
    {
        public int  remaining;
        public FP64 charge;
    }

    /// <summary>A script referencing another entity, to cover the identity-only rule.</summary>
    private class Follower : Script
    {
        public Entity leader;
        public int    distance;
    }

    private abstract class BaseCooldown : Script
    {
        public int Remaining { get; set; }
    }

    private sealed class InheritedCooldown : BaseCooldown
    {
        public int visible;
    }

    /// <summary>Private backing fields declared on a base script are still simulation state.</summary>
    [Test]
    public static void InheritedPrivateFields_ParticipateInHashRollbackAndFullState()
    {
        var simulation = SampleSimulation.Create();
        var entity = NewEntity(simulation.Store);
        var script = new InheritedCooldown { Remaining = 5, visible = 3 };
        entity.AddScript(script);
        long before = simulation.GetStateHash();
        simulation.SaveSnapshot();

        script.Remaining = 99;
        Assert.AreNotEqual(before, simulation.GetStateHash(),
            "a private base field changed without changing the hash");
        simulation.Rollback(simulation.CurrentTick);
        Assert.AreEqual(5, script.Remaining, "rollback skipped the private base field");
        Assert.AreSame(script, entity.GetScript<InheritedCooldown>());

        var receiver = SampleSimulation.Create();
        receiver.RestoreFromFullState(simulation.SerializeFullState());
        var restored = receiver.Store.GetEntityById(entity.Id).GetScript<InheritedCooldown>();
        Assert.AreEqual(5, restored.Remaining, "full state skipped the private base field");
        Assert.AreEqual(3, restored.visible);
        Assert.AreEqual(before, receiver.GetStateHash());
    }

    private static Entity NewEntity(EntityStore store) =>
        store.CreateEntity(new Pos { x = FP64.One, y = FP64.One });

    /// <summary>Field values changed after a snapshot must be rolled back.</summary>
    [Test]
    public static void Rollback_RestoresScriptFieldValues()
    {
        var simulation = SampleSimulation.Create();
        var store      = simulation.Store;
        var entity     = NewEntity(store);
        var script     = new Cooldown { remaining = 5, charge = FP64.Half };
        entity.AddScript(script);

        simulation.SaveSnapshot();
        int targetTick = simulation.CurrentTick;

        script.remaining = 99;
        script.charge    = FP64.FromInt(42);

        simulation.Rollback(targetTick);
        Assert.AreEqual(5,         script.remaining, "script field was not restored");
        Assert.AreEqual(FP64.Half, script.charge);
    }

    /// <summary>The restored values must land on the original instance, not a replacement.</summary>
    [Test]
    public static void Rollback_KeepsScriptInstanceIdentity()
    {
        var simulation = SampleSimulation.Create();
        var store      = simulation.Store;
        var entity     = NewEntity(store);
        var script     = new Cooldown { remaining = 7 };
        entity.AddScript(script);

        simulation.SaveSnapshot();
        int targetTick = simulation.CurrentTick;

        script.remaining = 1;
        simulation.Rollback(targetTick);

        Assert.AreSame(script, entity.GetScript<Cooldown>(),
            "rollback replaced the script instance - references held by game code would go stale");
        Assert.AreEqual(7, script.remaining);
    }

    /// <summary>Scripts added after a snapshot must be detached once it is restored.</summary>
    [Test]
    public static void Rollback_RemovesScriptsAddedAfterSnapshot()
    {
        var simulation = SampleSimulation.Create();
        var store      = simulation.Store;
        var entity     = NewEntity(store);

        simulation.SaveSnapshot();
        int targetTick = simulation.CurrentTick;

        entity.AddScript(new Cooldown { remaining = 3 });
        Assert.IsNotNull(entity.GetScript<Cooldown>(), "script was not added - test is vacuous");

        simulation.Rollback(targetTick);
        Assert.IsNull(entity.GetScript<Cooldown>(),
            "script added after the snapshot survived the rollback");
    }

    /// <summary>Scripts removed after a snapshot must be back once it is restored.</summary>
    [Test]
    public static void Rollback_RestoresRemovedScripts()
    {
        var simulation = SampleSimulation.Create();
        var store      = simulation.Store;
        var entity     = NewEntity(store);
        entity.AddScript(new Cooldown { remaining = 4 });

        simulation.SaveSnapshot();
        int targetTick = simulation.CurrentTick;

        entity.RemoveScript<Cooldown>();
        Assert.IsNull(entity.GetScript<Cooldown>(), "script was not removed - test is vacuous");

        simulation.Rollback(targetTick);
        var restored = entity.GetScript<Cooldown>();
        Assert.IsNotNull(restored, "removed script was not restored");
        Assert.AreEqual(4, restored.remaining, "script was restored without its field values");
    }

    /// <summary>An entity reference inside a script must survive with its identity intact.</summary>
    [Test]
    public static void Rollback_RestoresEntityReferenceInScript()
    {
        var simulation = SampleSimulation.Create();
        var store      = simulation.Store;
        var follower   = NewEntity(store);
        var leader     = NewEntity(store);
        var other      = NewEntity(store);
        var script     = new Follower { leader = leader, distance = 2 };
        follower.AddScript(script);

        simulation.SaveSnapshot();
        int targetTick = simulation.CurrentTick;

        script.leader   = other;
        script.distance = 9;

        simulation.Rollback(targetTick);
        Assert.AreEqual(leader.Id, script.leader.Id, "entity reference in the script was not restored");
        Assert.AreEqual(2,         script.distance);
        Assert.AreSame(store, script.leader.Store,   "restored entity was not bound to the local store");
    }

    /// <summary>Repeated rollbacks onto one snapshot must not duplicate script attachments.</summary>
    [Test]
    public static void Rollback_DoesNotDuplicateScripts()
    {
        var simulation = SampleSimulation.Create();
        var store      = simulation.Store;
        var entity     = NewEntity(store);
        entity.AddScript(new Cooldown { remaining = 6 });

        simulation.SaveSnapshot();
        int targetTick = simulation.CurrentTick;

        for (int i = 0; i < 3; i++) {
            simulation.Rollback(targetTick);
            simulation.SaveSnapshot();
        }
        Assert.AreEqual(1, entity.Scripts.Length, "rollback duplicated the script attachment");
    }

    /// <summary>The state hash must react to script fields, or a script desync goes undetected.</summary>
    [Test]
    public static void StateHash_CoversScriptFields()
    {
        var simulation = SampleSimulation.Create();
        var store      = simulation.Store;
        var entity     = NewEntity(store);
        var script     = new Cooldown { remaining = 5 };
        entity.AddScript(script);

        long before = simulation.GetStateHash();
        script.remaining = 6;

        Assert.AreNotEqual(before, simulation.GetStateHash(),
            "state hash ignores script fields - script desyncs would go undetected");
    }

    /// <summary>Moving a script to a different entity must change the hash.</summary>
    [Test]
    public static void StateHash_CoversScriptOwnership()
    {
        var simulation = SampleSimulation.Create();
        var store      = simulation.Store;
        var first  = NewEntity(store);
        var second = NewEntity(store);

        first.AddScript(new Cooldown { remaining = 5 });
        long onFirst = simulation.GetStateHash();

        first.RemoveScript<Cooldown>();
        second.AddScript(new Cooldown { remaining = 5 });
        long onSecond = simulation.GetStateHash();

        Assert.AreNotEqual(onFirst, onSecond, "state hash ignores which entity owns a script");
    }

    /// <summary>Re-simulating across a rollback must reproduce script state exactly.</summary>
    [Test]
    public static void Resimulation_ReproducesScriptState()
    {
        var simulation = SampleSimulation.Create();
        var store      = simulation.Store;
        var entity     = NewEntity(store);
        var script     = new Cooldown { remaining = 10 };
        entity.AddScript(script);

        simulation.SaveSnapshot();
        int targetTick = simulation.CurrentTick;

        for (int i = 0; i < 4; i++) script.remaining--;
        long expected = simulation.GetStateHash();

        simulation.Rollback(targetTick);
        Assert.AreEqual(10, script.remaining);

        for (int i = 0; i < 4; i++) script.remaining--;
        Assert.AreEqual(expected, simulation.GetStateHash(), "re-simulated script state differs");
    }
}
