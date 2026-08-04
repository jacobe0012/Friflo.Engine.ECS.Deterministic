using System.Collections.Generic;
using Friflo.Engine.ECS;
using Klotho.Friflo;
using NUnit.Framework;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;

// ReSharper disable once CheckNamespace
namespace Klotho.Friflo.Tests;

/// <summary>
/// Full-state serialization must carry <i>everything</i> the state hash covers, into a store that
/// has never run.
/// </summary>
/// <remarks>
/// The other determinism tests exercise the rollback snapshot, which restores into the same store
/// and can therefore keep script instances and archetype indices. Full state is the different
/// problem: it rebuilds a world from bytes on a peer that may share none of that. It is also the
/// path a server-driven client boots from — <c>KlothoEngine.Start</c> skips world initialization
/// on an SD client and the authoritative tick-0 state arrives as a full state instead — so a gap
/// here is not a degraded resync, it is a client that never had the world at all.
/// <para>
/// Scripts and the parent/child hierarchy are the two parts that cannot travel as raw component
/// bytes: a script is a class instance living outside the archetypes, and a <c>TreeNode</c>'s
/// child list is an <c>IdArray</c> handle into a pooled allocator, whose offsets are the sender's
/// allocation history.
/// </para>
/// </remarks>
public static class Test_FullStateTransfer
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

    private static Entity NewEntity(EntityStore store) =>
        store.CreateEntity(new Pos { x = FP64.One, y = FP64.One });

    /// <summary>
    /// A world holding scripts and a parent/child tree must survive a full-state transfer.
    /// </summary>
    /// <remarks>
    /// Both halves used to fail: scripts were not written at all, so the receiving store built a
    /// world with none; and the hierarchy travelled as raw <c>TreeNode</c> bytes, so the restored
    /// handles pointed into a pool that had never made the matching allocation and reading a child
    /// threw. Neither showed up in P2P play, where both peers build their own world and full state
    /// is only used for resync.
    /// </remarks>
    [Test]
    public static void FullState_CarriesScriptsAndHierarchy()
    {
        var source = SampleSimulation.Create();
        var store  = source.Store;

        var parent = NewEntity(store);
        var first  = NewEntity(store);
        var second = NewEntity(store);
        parent.AddChild(first);
        parent.AddChild(second);

        var cooldown = new Cooldown { remaining = 5, charge = FP64.Half };
        parent.AddScript(cooldown);
        first.AddScript(new Follower { leader = parent, distance = 3 });

        long sourceHash = source.GetStateHash();
        var  data       = source.SerializeFullState();

        var target = SampleSimulation.Create();
        target.RestoreFromFullState(data);

        // The hash covers scripts and hashes a TreeNode by its child ids, so agreement here is the
        // headline claim. The assertions below say *which* part broke when it does not hold.
        Assert.AreEqual(sourceHash, target.GetStateHash(), "restored state differs from the state that was sent");

        var restoredParent = new Entity(target.Store, parent.Id, parent.Revision);
        var restoredFirst  = new Entity(target.Store, first.Id,  first.Revision);

        // Reading a child is the operation that threw when the pool was not rebuilt.
        Assert.AreEqual(2, restoredParent.ChildCount, "children were lost");
        Assert.AreEqual(first.Id,  restoredParent.ChildEntities[0].Id, "child order changed");
        Assert.AreEqual(second.Id, restoredParent.ChildEntities[1].Id, "child order changed");
        Assert.AreEqual(parent.Id, restoredFirst.Parent.Id, "parent link was lost");

        var restoredCooldown = restoredParent.GetScript<Cooldown>();
        Assert.IsNotNull(restoredCooldown, "the script was not transferred");
        Assert.AreEqual(5,         restoredCooldown.remaining, "script field value was lost");
        Assert.AreEqual(FP64.Half, restoredCooldown.charge);

        // A fresh instance, not the sender's: the two stores are separate worlds, and sharing an
        // instance would let one peer's rollback rewrite the other's fields.
        Assert.AreNotSame(cooldown, restoredCooldown, "the receiving store shares the sender's script instance");

        var restoredFollower = restoredFirst.GetScript<Follower>();
        Assert.IsNotNull(restoredFollower, "the second entity's script was not transferred");
        Assert.AreEqual(3, restoredFollower.distance);
        // An Entity field carries only (Id, Revision) and is re-bound on arrival, so it must point
        // at the *local* store - holding the sender's would reach across worlds.
        Assert.AreEqual(parent.Id, restoredFollower.leader.Id, "entity reference was not restored");
        Assert.AreSame(target.Store, restoredFollower.leader.Store, "entity reference still points at the sender's store");
    }

    /// <summary>Both worlds must stay identical once they continue running.</summary>
    /// <remarks>
    /// A one-off hash match can come from state that is equal but structurally wrong — a child
    /// pool that reads correctly once, say, but whose free lists are inconsistent. Running both
    /// forward through mutations of the transferred parts is what rules that out.
    /// </remarks>
    [Test]
    public static void FullState_KeepsScriptsAndHierarchyUsable()
    {
        var source = SampleSimulation.Create();
        var store  = source.Store;
        var parent = NewEntity(store);
        parent.AddChild(NewEntity(store));
        parent.AddScript(new Cooldown { remaining = 2 });

        var target = SampleSimulation.Create();
        target.RestoreFromFullState(source.SerializeFullState());
        Assert.AreEqual(source.GetStateHash(), target.GetStateHash(), "diverged before either side ran");

        // Apply the same hierarchy edits to both, exercising the restored pool's allocate and
        // release paths rather than just reading it.
        for (int round = 0; round < 5; round++) {
            MutateHierarchy(source.Store, parent.Id, round);
            MutateHierarchy(target.Store, parent.Id, round);
            source.Tick(NoCommands);
            target.Tick(NoCommands);
            Assert.AreEqual(source.GetStateHash(), target.GetStateHash(), $"diverged in round {round}");
        }
    }

    /// <summary>Add a child, then drop the oldest — so the child pool both grows and frees.</summary>
    private static void MutateHierarchy(EntityStore store, int parentId, int round)
    {
        var parent = store.GetEntityById(parentId);
        parent.AddChild(store.CreateEntity(new Pos { x = FP64.FromInt(round), y = FP64.Zero }));
        if (parent.ChildCount > 2) {
            parent.RemoveChild(parent.ChildEntities[0]);
        }
    }
}
