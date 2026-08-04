# Deterministic Physics

A deterministic 3D rigid-body physics system based on FP64. All computation runs on fixed-point arithmetic, guaranteeing synchronization across clients (prediction/rollback safe).

> **Engine scope**: the physics **runtime** (`FPPhysicsWorld`, `PhysicsSystem`, colliders, solver, …) is engine-agnostic core — it runs unchanged on Unity, Godot, and the .NET server. The **debug visualization** is provided per engine (Unity components + a Godot runtime adapter); see [PhysicsVisualizer.Godot.md](PhysicsVisualizer.Godot.md). Static colliders are baked from the scene to a `.bytes` asset via an editor exporter (Unity, or Godot `Project > Tools > Klotho: Export Static Colliders`) and loaded at runtime. Cross-engine `.bytes` sharing is **not** supported (coordinate-handedness differs — each engine bakes its own scene).

## Components

| Class | Role |
| ------ | ---- |
| `FPPhysicsWorld` | The world: broadphase → narrowphase → solver → integration; `Step()` |
| `FPPhysicsBody` | Per-body simulation state (rigidbody + collider + transform + flags) |
| `FPRigidBody` | Mass / velocity / damping / material / static·kinematic flags |
| `FPCollider` | Tagged-union shape (`Box` / `Sphere` / `Capsule` / `Mesh`) |
| `FPBoxShape` · `FPSphereShape` · `FPCapsuleShape` · `FPMeshShape` | Shape data (local position/rotation + dimensions) |
| `FPStaticCollider` | Immovable collider (id · material · trigger) + `FPStaticColliderSerializer` |
| `FPContact` | Contact manifold point (point · normal · depth · entityA/B · speculative) |
| `NarrowphaseDispatch` · `CollisionTests` · `FPSweepTests` | Pair collision detection (overlap + sweep/CCD) |
| `FPCollisionResponse` · `FPConstraintSolver` | Impulse-based contact/constraint resolution |
| `FPPhysicsIntegration` | Force → velocity → position integration |
| `FPSpatialGrid` | Dynamic-body broadphase (uniform grid) |
| `FPStaticBVH` · `FPBVHNode` | Static-collider broadphase (BVH) |
| `FPTriggerSystem` | Trigger enter / stay / exit tracking (serialized for rollback) |
| `FPCCDConfig` | Continuous-collision / sweep configuration |
| `FPDistanceJoint` · `FPHingeJoint` | Optional constraints |
| `PhysicsSystem` | ECS adapter (`ISystem`) — syncs components ↔ bodies and calls `Step()` |
| `PhysicsBodyComponent` | ECS component (`[KlothoComponent(25)]`; rigidbody + collider + offset) |
| `IFPPhysicsWorldProvider` · `IFPPhysicsProviderSource` | External read access to bodies/colliders/contacts/triggers |
| `IStaticColliderService` · `IPhysicsRayCaster` | Static-collider load + raycast services |

## File Layout

```text
com.xpturn.klotho/Runtime/Deterministic/Physics/
├── FPPhysicsWorld.cs          # World: broadphase/narrowphase/solver/integration + Step()
├── FPPhysicsBody.cs           # Per-body sim state (id, rigidBody, collider, position, rotation, flags)
├── FPRigidBody.cs             # mass/velocity/damping/material; CreateDynamic/Static/Kinematic
├── FPCollider.cs              # Tagged-union shape (FromBox/Sphere/Capsule/Mesh, GetWorldBounds)
├── FPBoxShape.cs · FPSphereShape.cs · FPCapsuleShape.cs · FPMeshShape.cs
├── FPStaticCollider.cs        # Immovable collider struct
├── FPStaticColliderSerializer.cs   # .bytes save/load (FPSC magic) + JSON sidecar
├── NarrowphaseDispatch.cs · CollisionTests.cs · FPSweepTests.cs   # detection
├── FPCollisionResponse.cs · FPConstraintSolver.cs                 # response/solver
├── FPPhysicsIntegration.cs    # integration
├── FPSpatialGrid.cs           # dynamic broadphase
├── FPStaticBVH.cs · FPBVHNode.cs   # static broadphase
├── FPTriggerSystem.cs         # trigger pair tracking
├── FPCCDConfig.cs             # CCD/sweep config
├── FPDistanceJoint.cs · FPHingeJoint.cs   # constraints
├── IFPPhysicsWorldProvider.cs · IFPPhysicsProviderSource.cs       # external read access
└── IStaticColliderService.cs · IPhysicsRayCaster.cs               # services

com.xpturn.klotho/Runtime/Deterministic/Geometry/
├── FPContact.cs · FPBounds3.cs · ShapeType.cs · FPMeshData.cs · FPRay3.cs   # shared geometry

com.xpturn.klotho/Runtime/Gameplay/
├── Systems/PhysicsSystem.cs            # ECS adapter system (ISystem + providers)
└── Components/PhysicsBodyComponent.cs  # ECS component ([KlothoComponent(25)])
```

## Step Pipeline

`FPPhysicsWorld.Step()` advances the world by a fixed `dt`:

```text
ECS components (Transform + PhysicsBody [+ Velocity])
        │  PhysicsSystem: components → FPPhysicsBody[] copy
        ▼
FPPhysicsWorld.Step(bodies, count, dt, gravity, joints…, solverIterations)
   ┌───────────────────────────────────────────────┐
   │ 1. Integrate forces      │ gravity, damping → velocity (FPPhysicsIntegration)
   │ 2. Broadphase            │ FPSpatialGrid (dynamic) + FPStaticBVH (static)
   │ 3. Narrowphase           │ NarrowphaseDispatch → FPContact manifolds
   │    + CCD/sweep           │ FPSweepTests when body.useCCD/useSweep (FPCCDConfig)
   │ 4. Solve                 │ FPConstraintSolver × solverIterations (contacts + joints)
   │ 5. Integrate velocities  │ velocity → position
   │ 6. Triggers              │ FPTriggerSystem: enter/stay/exit pairs
   └───────────────────────────────────────────────┘
        │  PhysicsSystem: FPPhysicsBody[] → components (reverse sync)
        ▼
   contact / static-contact / trigger-pair snapshots (copied out for providers)
```

All arithmetic is FP64; the world (including the trigger system) serializes via `Serialize`/`Deserialize`, and `PhysicsSystem` is an `ISnapshotParticipant`, so physics is rollback-safe.

## Core Data Structures

### FPPhysicsBody

| Field | Type | Description |
| ---- | ---- | ---- |
| `id` | `int` | Stable body id (entity index) |
| `rigidBody` | `FPRigidBody` | Mass / velocity / material / flags |
| `collider` | `FPCollider` | Shape (local) |
| `meshData` | `FPMeshData` | Vertices/indices for `Mesh` shape (else null) |
| `position` | `FPVector3` | World position |
| `rotation` | `FPQuaternion` | World rotation |
| `colliderOffset` | `FPVector3` | Collider offset from the body origin |
| `isTrigger` | `bool` | Trigger (no contact response, fires trigger pairs) |
| `useCCD` / `useSweep` | `bool` | Continuous collision / sweep for this body |

### FPRigidBody

| Field | Type | Description |
| ---- | ---- | ---- |
| `mass` / `inverseMass` | `FP64` | Mass and its inverse (0 inverseMass ⇒ infinite) |
| `velocity` / `angularVelocity` | `FPVector3` | Linear / angular velocity |
| `force` / `torque` | `FPVector3` | Accumulated force / torque (cleared each step) |
| `linearDamping` / `angularDamping` | `FP64` | Velocity damping |
| `restitution` / `friction` | `FP64` | Bounciness / friction material |
| `isStatic` / `isKinematic` | `bool` | Immovable / script-driven (ignores forces) |

Factory helpers: `FPRigidBody.CreateDynamic(mass)` · `CreateStatic()` · `CreateKinematic()`.

### FPCollider / shapes

`FPCollider` is a tagged union (`type` ∈ `Box`/`Sphere`/`Capsule`/`Mesh`) holding one of `box`/`sphere`/`capsule`/`mesh`. Each shape stores its **local** transform + dimensions:

| Shape | Fields |
| ---- | ---- |
| `FPBoxShape` | `halfExtents` · `position` · `rotation` |
| `FPSphereShape` | `radius` · `position` |
| `FPCapsuleShape` | `halfHeight` · `radius` · `position` · `rotation` |
| `FPMeshShape` | `position` · `rotation` (geometry in the body's `FPMeshData`) |

`FPCollider.GetWorldBounds(meshData = null)` returns the shape's world AABB (`FPBounds3`).

### FPStaticCollider

| Field | Type | Description |
| ---- | ---- | ---- |
| `id` | `int` | Collider id |
| `collider` | `FPCollider` | Shape (position/rotation baked into the shape) |
| `meshData` | `FPMeshData` | For `Mesh` shape |
| `isTrigger` | `bool` | Trigger volume |
| `restitution` / `friction` | `FP64` | Material |

Serialized by `FPStaticColliderSerializer` (`FPSC` magic + version + count; `.bytes` + optional `.json` sidecar).

### PhysicsBodyComponent

A `[KlothoComponent(25)]` ECS component: `RigidBody` (`FPRigidBody`) · `Collider` (`FPCollider`) · `ColliderOffset` (`FPVector3`). `PhysicsSystem` reads it together with `TransformComponent` (position/rotation) and optional `VelocityComponent`.

## Usage

Register `PhysicsSystem` in your `ISimulationCallbacks.RegisterSystems`, load static colliders, and attach `PhysicsBodyComponent` to entities (see [`P2pSimulationCallbacks.cs`](../Samples/GodotP2pSample/Game/P2pSimulationCallbacks.cs)):

```csharp
public void RegisterSystems(EcsSimulation simulation)
{
    // PhysicsSystem(maxEntities) defaults gravity to (0, -10, 0); the world uses a spatial-grid
    // cell size of 10. Use PhysicsSystem(maxEntities, gravity) to override gravity.
    var physics = new PhysicsSystem(64);
    physics.LoadStaticColliders("", new List<FPStaticCollider> { CreateGroundCollider() });
    simulation.AddSystem(physics, SystemPhase.Update);
}

// Per entity — attach a transform + a physics body
var entity = frame.CreateEntity();
frame.Add(entity, new TransformComponent { Position = pos, Rotation = FP64.Zero, Scale = FPVector3.One });
frame.Add(entity, new PhysicsBodyComponent
{
    RigidBody = FPRigidBody.CreateDynamic(mass),
    Collider  = FPCollider.FromBox(new FPBoxShape(halfExtents, FPVector3.Zero)),
    ColliderOffset = FPVector3.Zero,
});
```

`PhysicsSystem` each tick: components → `FPPhysicsBody[]` → `Step()` → reverse-sync back to components, then exposes contact/trigger snapshots.

### Triggers

Bodies/static colliders with `isTrigger = true` generate no contact response; instead `PhysicsSystem` raises structured callbacks — `OnStaticTriggerEnter/Stay/Exit` (dynamic × static) and `OnEntityTriggerEnter/Stay/Exit` (dynamic × dynamic).

### External access (providers)

`PhysicsSystem` implements `IFPPhysicsWorldProvider` (`GetBodies` / `GetStaticColliders` / `GetContacts` / `GetTriggerPairs`), `IStaticColliderService`, and `IPhysicsRayCaster`. Exposing it via `IFPPhysicsProviderSource.PhysicsProvider` on your callbacks lets tools (e.g. the debug visualizer) read the live world:

```csharp
public class MySimulationCallbacks : ISimulationCallbacks, IFPPhysicsProviderSource
{
    private EcsSimulation _simulation;
    public void RegisterSystems(EcsSimulation s) { _simulation = s; /* ... */ }
    public IFPPhysicsWorldProvider PhysicsProvider => _simulation?.GetSystem<PhysicsSystem>();
}
```

## Debug Visualization *(per engine)*

The live world (bodies / static colliders / contacts / triggers) can be drawn for debugging via the provider above.

**Godot** — runtime overlay + on-screen inspector HUD + an editor static-collider viewer; see **[PhysicsVisualizer.Godot.md](PhysicsVisualizer.Godot.md)**. Static colliders are baked with `Project > Tools > Klotho: Export Static Colliders`.

**Unity** — `FPPhysicsWorldVisualizer` component (+ inspector) and `FPStaticColliderVisualizer`.

## Notes

- **Determinism**: FP64 throughout; deterministic broadphase ordering; the world + trigger state serialize for rollback (`ISnapshotParticipant`).
- **Static vs dynamic broadphase**: dynamic bodies use a uniform `FPSpatialGrid`; static colliders use an `FPStaticBVH` (rebuilt on load/unload). `GetStaticFingerprint()` lets the engine detect static-geometry divergence between peers.
- **CCD/sweep** is opt-in per body (`useCCD` / `useSweep`) and bounded by `FPCCDConfig.maxSweepIterations`.
- **Coordinate space**: world is the simulation coordinate space; static-collider `.bytes` are engine-specific (handedness) — load on the engine that baked them.

---

*Last updated: 2026-06-09 — physics overview added; runtime is engine-agnostic, debug visualization per engine (Godot: [PhysicsVisualizer.Godot.md](PhysicsVisualizer.Godot.md)).*
