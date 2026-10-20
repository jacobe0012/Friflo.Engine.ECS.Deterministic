using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using Klotho.Friflo;
using xpTURN.Klotho.Core;

// ReSharper disable once CheckNamespace
namespace Klotho.Friflo.AotSmoke;

/// <summary>
/// NativeAOT smoke test for the bridge paths that inspect types at run time.
/// </summary>
/// <remarks>
/// Relations and scripts cannot be hashed by raw bytes: a relation component holds an
/// <see cref="Entity"/> (whose store reference is process-local) and a script is a class. Both go
/// through a per-type layout plan, which compiles expression trees when the runtime can emit code
/// and interprets the plan reflectively when it cannot. This program runs the second path for
/// real — <c>dotnet test</c> always has a JIT, so it can only ever cover the first.
///
/// Exits non-zero on the first failed check so it can be used as a build gate.
/// </remarks>
public static class Program
{
    private struct Position : IComponent
    {
        public int x, y;
    }

    private struct Team : IIndexedComponent<int>
    {
        public int id;
        public int GetIndexedValue() => id;
    }

    private struct Targeting : ILinkRelation
    {
        public Entity target;
        public Entity GetRelationKey() => target;
    }

    private struct EntityReference : IComponent
    {
        public Entity target;
        public int    weight;
    }

    private readonly struct ImmutableReference : IComponent
    {
        public readonly Entity target;
        public readonly int    weight;
        public ImmutableReference(Entity target, int weight) { this.target = target; this.weight = weight; }
    }

    private struct NestedImmutableReference : IComponent
    {
        public ImmutableReference value;
    }

    private struct UnregisteredState
    {
        public int value;
    }

    private struct UnregisteredReference : IComponent
    {
        public Entity            target;
        public UnregisteredState state;
    }

    private struct NestedState
    {
        public long amount;
    }

    private struct NestedReference : IComponent
    {
        public Entity      target;
        public NestedState state;
    }

    private abstract class BaseCooldown : Script
    {
        public int Remaining { get; set; }
    }

    private sealed class InheritedCooldown : BaseCooldown
    {
        public NestedState state;
    }

    private class Cooldown : Script
    {
        public int    remaining;
        public Entity source;
    }

    private static int failures;

    public static int Main()
    {
        Console.WriteLine($"IsDynamicCodeSupported = {RuntimeFeature.IsDynamicCodeSupported}");
        if (RuntimeFeature.IsDynamicCodeSupported) {
            Console.WriteLine("WARNING: running with dynamic code available - this is NOT the AOT path");
        }

        RegisterAotTypes();

        RelationRollbackWorks();
        ScriptRollbackWorks();
        RelationHashIsIdentityBased();
        FullStateRoundTrips();
        FullStateCarriesScriptsAndHierarchy();
        EntityComponentFieldsSurviveTrimming();
        NestedAndInheritedFieldsSurviveTrimming();
        ReadonlyAndDefaultEntityFieldsRoundTrip();
        NegativeRevisionRoundTripsAcrossRuntimes();
        if (!RuntimeFeature.IsDynamicCodeSupported) {
            MissingNestedRegistrationFailsLoud();
        }

        Console.WriteLine(failures == 0 ? "AOT smoke: PASS" : $"AOT smoke: FAIL ({failures})");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Register the types whose indexes and relations Friflo would otherwise build by reflection.
    /// </summary>
    /// <remarks>
    /// Friflo requires this under NativeAOT for indexed components and relations
    /// (see <c>NativeAOT.RegisterRelation</c>); it is a pre-existing constraint of the engine,
    /// not something the bridge introduces.
    /// <para>
    /// Register each type exactly once, through its most specific method. Every <c>Register*</c>
    /// is guarded by <c>if (typeSet.Add(typeof(T)))</c>, so a second call for the same type is
    /// skipped in full — silently. Calling <c>RegisterComponent&lt;Team&gt;()</c> before
    /// <c>RegisterIndexedComponentStruct&lt;Team, int&gt;()</c> would leave the index factory
    /// uninstalled, and the first entity carrying a <c>Team</c> would throw. The specific method
    /// already registers the component type itself.
    /// </para>
    /// </remarks>
    private static void RegisterAotTypes()
    {
        var aot = new NativeAOT();
        aot.RegisterComponent<Position>();
        aot.RegisterComponent<EntityReference>();
        aot.RegisterComponent<NestedReference>();
        aot.RegisterComponent<ImmutableReference>();
        aot.RegisterComponent<NestedImmutableReference>();
        aot.RegisterComponent<UnregisteredReference>();
        aot.RegisterDeterministicType<NestedState>();
        aot.RegisterScript<InheritedCooldown>();
        aot.RegisterIndexedComponentStruct<Team, int>();
        aot.RegisterLinkRelation<Targeting>();
        aot.RegisterScript<Cooldown>();
        aot.CreateSchema();
    }

    // ------------------------------------------------------------------ checks

    /// <summary>Relations must snapshot and roll back with dynamic code disabled.</summary>
    private static void RelationRollbackWorks()
    {
        var simulation = NewSimulation(out var store);
        var a = store.CreateEntity(new Position { x = 1, y = 1 });
        var b = store.CreateEntity(new Position { x = 2, y = 2 });
        a.AddRelation(new Targeting { target = b });

        simulation.SaveSnapshot();
        int targetTick = simulation.CurrentTick;

        a.RemoveRelation<Targeting>(b);
        Check(a.GetRelations<Targeting>().Length == 0, "relation removed before rollback");

        simulation.Rollback(targetTick);
        var restored = a.GetRelations<Targeting>();
        Check(restored.Length == 1,             "relation restored by rollback");
        Check(restored.Length == 1 && restored[0].target.Id == b.Id, "relation target preserved");
    }

    /// <summary>Scripts must snapshot and roll back with dynamic code disabled.</summary>
    private static void ScriptRollbackWorks()
    {
        var simulation = NewSimulation(out var store);
        var entity = store.CreateEntity(new Position { x = 1, y = 1 });
        var other  = store.CreateEntity(new Position { x = 2, y = 2 });
        var script = new Cooldown { remaining = 5, source = other };
        entity.AddScript(script);

        simulation.SaveSnapshot();
        int targetTick = simulation.CurrentTick;

        script.remaining = 99;
        script.source    = entity;

        simulation.Rollback(targetTick);
        Check(script.remaining == 5,             "script field restored by rollback");
        Check(script.source.Id == other.Id,      "entity reference in script restored");
        Check(ReferenceEquals(script, entity.GetScript<Cooldown>()), "script instance identity kept");
    }

    /// <summary>
    /// Two stores in the same logical state must hash equally, even though their entity
    /// references are different object addresses.
    /// </summary>
    private static void RelationHashIsIdentityBased()
    {
        long hashA = BuildLinked();
        long hashB = BuildLinked();
        Check(hashA == hashB, "relation hash is identity-based, not address-based");

        static long BuildLinked()
        {
            var simulation = NewSimulation(out var store);
            var a = store.CreateEntity(new Position { x = 1, y = 1 });
            var b = store.CreateEntity(new Position { x = 2, y = 2 });
            a.AddRelation(new Targeting { target = b });
            return simulation.GetStateHash();
        }
    }

    /// <summary>Full-state serialization must round-trip relations with dynamic code disabled.</summary>
    private static void FullStateRoundTrips()
    {
        var source = NewSimulation(out var sourceStore);
        var a = sourceStore.CreateEntity(new Position { x = 3, y = 4 }, new Team { id = 1 });
        var b = sourceStore.CreateEntity(new Position { x = 5, y = 6 }, new Team { id = 2 });
        a.AddRelation(new Targeting { target = b });

        var (data, hash) = source.SerializeFullStateWithHash();

        var target = NewSimulation(out var targetStore);
        target.RestoreFromFullState(data);
        Check(target.GetStateHash() == hash, "full state round-trips with relations");
        var restoredOwner = targetStore.GetEntityById(a.Id);
        var restoredTarget = targetStore.GetEntityById(b.Id);
        var restoredRelations = restoredOwner.GetRelations<Targeting>();
        Check(restoredRelations.Length == 1 && restoredRelations[0].target == restoredTarget,
              "relation target fields survive an AOT full state");
        restoredTarget.DeleteEntity();
        Check(restoredOwner.GetRelations<Targeting>().Length == 0,
              "the most-specific AOT relation registration rebuilds incoming links");
    }

    /// <summary>
    /// Full state must carry scripts and the parent/child hierarchy with dynamic code disabled.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="FullStateRoundTrips"/> because it covers a different hazard.
    /// Writing a script's fields means discovering them from its <i>runtime</i> type, with no
    /// static reference for the trimmer to follow — the same footing as the rollback path, which
    /// once silently degraded to a no-op when that rooting was lost. A whole world arriving without
    /// its scripts is how that failure would look to a server-driven client, and a hash comparison
    /// alone would not say which part went missing, so the pieces are checked individually.
    /// </remarks>
    private static void FullStateCarriesScriptsAndHierarchy()
    {
        var source = NewSimulation(out var sourceStore);
        var parent = sourceStore.CreateEntity(new Position { x = 7, y = 8 });
        var child  = sourceStore.CreateEntity(new Position { x = 9, y = 10 });
        parent.AddChild(child);
        parent.AddScript(new Cooldown { remaining = 11, source = child });

        var (data, hash) = source.SerializeFullStateWithHash();

        var target = NewSimulation(out var targetStore);
        target.RestoreFromFullState(data);
        Check(target.GetStateHash() == hash, "full state round-trips with scripts and hierarchy");

        var restoredParent = targetStore.GetEntityById(parent.Id);
        // Reading a child is what throws when the child pool was not rebuilt from the ids.
        Check(restoredParent.ChildCount == 1 && restoredParent.ChildEntities[0].Id == child.Id,
              "hierarchy survives a full state");

        var restored = restoredParent.GetScript<Cooldown>();
        Check(restored != null && restored.remaining == 11, "script field survives a full state");
        Check(restored != null && restored.source.Id == child.Id, "entity reference in a transferred script");
    }

    /// <summary>A statically used component still needs its field metadata rooted for the layout plan.</summary>
    private static void EntityComponentFieldsSurviveTrimming()
    {
        var source = NewSimulation(out var store);
        var target = store.CreateEntity();
        var holder = store.CreateEntity(new EntityReference { target = target, weight = 7 });
        long before = source.GetStateHash();
        holder.GetComponent<EntityReference>().target = default;
        Check(source.GetStateHash() != before, "an Entity component retarget changes the AOT hash");
        holder.GetComponent<EntityReference>().target = target;

        var (data, hash) = source.SerializeFullStateWithHash();
        var receiver = NewSimulation(out var receiverStore);
        receiver.RestoreFromFullState(data);
        var restored = receiverStore.GetEntityById(holder.Id).GetComponent<EntityReference>();
        Check(restored.target == receiverStore.GetEntityById(target.Id) && restored.weight == 7,
              "an Entity component's actual fields survive an AOT full state");
        Check(receiver.GetStateHash() == hash, "Entity component full state hash agrees");
    }

    /// <summary>Registration must also preserve fields reached through structs and script bases.</summary>
    private static void NestedAndInheritedFieldsSurviveTrimming()
    {
        var source = NewSimulation(out var store);
        var target = store.CreateEntity();
        var holder = store.CreateEntity(new NestedReference { target = target, state = new NestedState { amount = 13 } });
        var script = new InheritedCooldown { Remaining = 5, state = new NestedState { amount = 17 } };
        holder.AddScript(script);
        long before = source.GetStateHash();
        source.SaveSnapshot();
        script.Remaining = 99;
        Check(source.GetStateHash() != before, "private inherited script fields affect the AOT hash");
        source.Rollback(source.CurrentTick);
        Check(script.Remaining == 5, "private inherited script fields survive an AOT rollback");

        before = source.GetStateHash();
        holder.GetComponent<NestedReference>().state.amount = 100;
        Check(source.GetStateHash() != before, "nested component fields affect the AOT hash");
        source.Rollback(source.CurrentTick);

        var receiver = NewSimulation(out var receiverStore);
        receiver.RestoreFromFullState(source.SerializeFullState());
        var restored = receiverStore.GetEntityById(holder.Id);
        Check(restored.GetComponent<NestedReference>().state.amount == 13,
              "nested component fields survive an AOT full state");
        var restoredScript = restored.GetScript<InheritedCooldown>();
        Check(restoredScript.Remaining == 5 && restoredScript.state.amount == 17,
              "nested and inherited script fields survive an AOT full state");
    }

    /// <summary>Readonly fields and null entity sentinels must survive the reflection read path too.</summary>
    private static void ReadonlyAndDefaultEntityFieldsRoundTrip()
    {
        var source = NewSimulation(out var store);
        var target = store.CreateEntity();
        var holder = store.CreateEntity(new ImmutableReference(target, 7),
            new NestedImmutableReference { value = new ImmutableReference(target, 11) },
            new EntityReference { weight = 3 });
        var script = new Cooldown();
        holder.AddScript(script);
        source.SaveSnapshot();
        script.source = target;
        source.Rollback(source.CurrentTick);
        Check(script.source == default, "default Entity equality survives an AOT script rollback");

        var receiver = NewSimulation(out var receiverStore);
        receiver.RestoreFromFullState(source.SerializeFullState());
        var restored = receiverStore.GetEntityById(holder.Id);
        var direct = restored.GetComponent<ImmutableReference>();
        var nested = restored.GetComponent<NestedImmutableReference>().value;
        Check(direct.target == receiverStore.GetEntityById(target.Id) && direct.weight == 7,
              "readonly Entity fields survive an AOT full state");
        Check(nested.target == direct.target && nested.weight == 11,
              "nested readonly fields survive an AOT full state");
        Check(restored.GetComponent<EntityReference>().target == default,
              "default Entity equality survives an AOT component full state");
    }

    /// <summary>The signature is compared between this native executable and the same JIT build.</summary>
    private static void NegativeRevisionRoundTripsAcrossRuntimes()
    {
        var source = NewSimulation(out var store);
        var target = store.CreateEntity();
        for (int i = 0; i <= short.MaxValue; i++) {
            target.DeleteEntity();
            target = store.CreateEntity();
        }
        Check(target.Revision == short.MinValue, "entity reuse reaches a negative revision");
        var holder = store.CreateEntity(new EntityReference { target = target, weight = -7 });
        var (data, hash) = source.SerializeFullStateWithHash();
        var receiver = NewSimulation(out var receiverStore);
        receiver.RestoreFromFullState(data);
        Check(receiverStore.GetEntityById(holder.Id).GetComponent<EntityReference>().target ==
              receiverStore.GetEntityById(target.Id), "a negative Entity revision survives a full state");
        Check(receiver.GetStateHash() == hash, "negative revision full state hash agrees");
        Console.WriteLine($"CROSS_RUNTIME_NEGATIVE_REVISION_HASH={hash}");
    }

    /// <summary>A partial plan is not proof that all nested state survived trimming.</summary>
    private static void MissingNestedRegistrationFailsLoud()
    {
        var source = NewSimulation(out var store);
        var target = store.CreateEntity();
        store.CreateEntity(new UnregisteredReference { target = target, state = new UnregisteredState { value = 7 } });
        try {
            source.GetStateHash();
            Check(false, "an unregistered nested type is rejected before hashing partial state");
        }
        catch (NotSupportedException error) {
            Check(error.Message.Contains("RegisterDeterministicType<UnregisteredState>()"),
                  "missing metadata names the nested type and its required registration");
        }
    }

    // ------------------------------------------------------------------ helpers

    private static FrifloSimulation NewSimulation(out EntityStore store)
    {
        store = new EntityStore();
        return new FrifloSimulation(store, new SystemRoot(store));
    }

    private static void Check(bool condition, string description)
    {
        Console.WriteLine($"  [{(condition ? "ok" : "FAIL")}] {description}");
        if (!condition) {
            failures++;
        }
    }
}
