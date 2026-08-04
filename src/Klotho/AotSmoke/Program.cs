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
        aot.RegisterIndexedComponentStruct<Team, int>();
        aot.RegisterRelation<Targeting, Entity>();
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

        var target = NewSimulation(out _);
        target.RestoreFromFullState(data);
        Check(target.GetStateHash() == hash, "full state round-trips with relations");
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
