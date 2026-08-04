using System;
using System.Collections.Generic;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using Klotho.Friflo;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;

// ReSharper disable once CheckNamespace
namespace Klotho.Friflo.Tests;

// A minimal but non-trivial simulation used to verify the bridge. Between them the four systems
// exercise every layer of FrifloStoreSnapshot:
//
//   MoveSystem    pos += vel          -> layer 1 (component data)
//   BounceSystem  reflect at walls    -> layer 1 (value-dependent branching)
//   SpawnSystem   create / destroy    -> layers 2 and 3 (entity structure, nodes, id recycling)
//   TargetSystem  indexed lookup      -> layer 4 (index and relation lookup tables)
//
// All simulation values are fixed point (FP64): float results are not identical across
// platforms, which is exactly the desync this integration exists to prevent.

#region components

/// <summary>Fixed-point position.</summary>
public struct Pos : IComponent
{
    public FP64 x, y;
}

/// <summary>Fixed-point velocity.</summary>
public struct Vel : IComponent
{
    public FP64 x, y;
}

/// <summary>Lifetime in ticks. The entity is destroyed when it reaches zero.</summary>
public struct Lifetime : IComponent
{
    public int ticks;
}

/// <summary>Team id, indexed so entities can be looked up by team.</summary>
public struct Team : IIndexedComponent<int>
{
    public int id;
    public int GetIndexedValue() => id;
}

/// <summary>Link relation pointing at the entity currently targeted.</summary>
public struct Targeting : ILinkRelation
{
    public Entity target;
    public Entity GetRelationKey() => target;
}

/// <summary>
/// Simulation tick counter, held as a component on a singleton entity.
/// </summary>
/// <remarks>
/// Counters that drive simulation decisions must live in the store. A field on the system object
/// is invisible to the snapshot, so a rollback would rewind the entities but not the counter,
/// and the re-simulation would take different branches — a desync that only appears when the
/// rollback distance is not a multiple of the counter's period.
/// </remarks>
public struct SimClock : IComponent
{
    public int tick;
}

#endregion

#region systems

/// <summary>Integrates position by velocity. One fixed-point add per axis, per tick.</summary>
public sealed class MoveSystem : QuerySystem<Pos, Vel>
{
    protected override void OnUpdate()
    {
        Query.ForEachEntity((ref Pos pos, ref Vel vel, Entity _) => {
            pos.x += vel.x;
            pos.y += vel.y;
        });
    }
}

/// <summary>Reflects velocity at the bounds, so component values drive branching.</summary>
public sealed class BounceSystem : QuerySystem<Pos, Vel>
{
    private static readonly FP64 Min = FP64.FromInt(0);
    private static readonly FP64 Max = FP64.FromInt(100);

    protected override void OnUpdate()
    {
        Query.ForEachEntity((ref Pos pos, ref Vel vel, Entity _) => {
            if (pos.x < Min || pos.x > Max) vel.x = -vel.x;
            if (pos.y < Min || pos.y > Max) vel.y = -vel.y;
        });
    }
}

/// <summary>
/// Spawns an entity every <see cref="Interval"/> ticks and destroys entities whose lifetime
/// expired. Drives entity creation, deletion and id recycling.
/// </summary>
public sealed class SpawnSystem : BaseSystem
{
    private const int Interval = 5;

    private readonly List<Entity> doomed = new();
    private EntityStore store;

    protected override void OnAddStore(EntityStore entityStore) => store = entityStore;

    protected override void OnUpdateGroup()
    {
        // The tick counter is read from the store, so a rollback rewinds it with everything else.
        var clockQuery = store.Query<SimClock>();
        int tickCount  = 0;
        clockQuery.ForEachEntity((ref SimClock clock, Entity _) => tickCount = clock.tick);

        // --- destroy expired entities.
        // Entities are collected first: deleting inside the query loop is a structural change.
        doomed.Clear();
        var expiring = store.Query<Lifetime>();
        expiring.ForEachEntity((ref Lifetime life, Entity entity) => {
            if (--life.ticks <= 0) {
                doomed.Add(entity);
            }
        });
        foreach (var entity in doomed) {
            entity.DeleteEntity();
        }

        // --- spawn on schedule
        if (tickCount % Interval == 0) {
            store.CreateEntity(
                new Pos      { x = FP64.FromInt(10), y = FP64.FromInt(10) },
                new Vel      { x = FP64.One,         y = FP64.Half },
                new Lifetime { ticks = 12 },
                new Team     { id = tickCount % 2 });
        }
        clockQuery.ForEachEntity((ref SimClock clock, Entity _) => clock.tick++);
    }
}

/// <summary>
/// Each entity targets an entity of the opposing team, found through the <see cref="Team"/>
/// component index, and records it as a <see cref="Targeting"/> relation. Exercises both
/// lookup structures that layer 4 rebuilds.
/// </summary>
public sealed class TargetSystem : BaseSystem
{
    private EntityStore store;

    protected override void OnAddStore(EntityStore entityStore) => store = entityStore;

    protected override void OnUpdateGroup()
    {
        var query = store.Query<Team>();
        query.ForEachEntity((ref Team team, Entity entity) => {
            int enemyTeam = 1 - team.id;
            var enemies   = store.GetEntitiesWithComponentValue<Team, int>(enemyTeam);
            if (enemies.Count == 0) {
                return;
            }
            // Lowest id keeps the choice independent of enumeration order.
            var chosen = enemies[0];
            for (int i = 1; i < enemies.Count; i++) {
                if (enemies[i].Id < chosen.Id) chosen = enemies[i];
            }
            entity.AddRelation(new Targeting { target = chosen });
        });
    }
}

#endregion

/// <summary>Builds the sample simulation.</summary>
public static class SampleSimulation
{
    /// <summary>Create a store seeded with a deterministic set of entities.</summary>
    public static EntityStore CreateStore()
    {
        // Default PidType.UsePidAsId: PidType.RandomPids would seed ids from a random source.
        var store = new EntityStore();
        store.CreateEntity(new SimClock { tick = 0 });
        for (int i = 0; i < 8; i++) {
            store.CreateEntity(
                new Pos  { x = FP64.FromInt(i * 7),     y = FP64.FromInt(i * 3) },
                new Vel  { x = FP64.FromInt(i % 3 - 1), y = FP64.FromInt(i % 2) },
                new Team { id = i % 2 });
        }
        return store;
    }

    /// <summary>Create the system pipeline. Order is fixed: it is part of the simulation contract.</summary>
    public static SystemRoot CreateSystems(EntityStore store)
    {
        var root = new SystemRoot(store) {
            new MoveSystem(),
            new BounceSystem(),
            new SpawnSystem(),
            new TargetSystem(),
        };
        return root;
    }

    /// <summary>Create a simulation over a freshly seeded store.</summary>
    public static FrifloSimulation Create(int rollbackCapacity = 32)
    {
        var store   = CreateStore();
        var systems = CreateSystems(store);
        return new FrifloSimulation(store, systems, ApplyCommands, rollbackCapacity);
    }

    /// <summary>Applies commands of a tick. The sample drives everything from systems, so this is a no-op.</summary>
    private static void ApplyCommands(EntityStore store, List<ICommand> commands, int tick) { }
}
