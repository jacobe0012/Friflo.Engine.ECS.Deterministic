# P2pSample — Minimum P2P Sample

> Target framework: **xpTURN.Klotho v0.2.8**
> Purpose: the smallest end-to-end P2P sample that exercises the deterministic ECS + LiteNetLib transport + view bootstrap surface — useful as a starting point for game devs picking up the package, and as a regression checkpoint for the package itself.
> Audience: game developers consuming `com.xpturn.klotho` via UPM who want a 60-minute "hello world" before tackling the full Brawler sample.
> Source: [`<repo>/Samples/P2pSample/`](../../Samples/P2pSample/)

> Last updated: 2026-05-29 (Step 4 검증 통과 — handshake → spawn → WASD → collision → fall-respawn → 60s GameOverEvent → result panel).

---

## 1. Game Overview

| Item | Description |
|---|---|
| Genre | Top-down "Sumo" (push opponents off) |
| Players | 2 (P2P only, host + 1 guest) |
| Match | 60-second timer, fall = -1 score, respawn at center |
| Win condition | Highest score at timeout. Tie → DRAW. |
| Visuals | Unity built-in primitives only (Cube + Plane) — no external assets |
| Controls | WASD / Arrow keys (XZ move). 4 buttons: Host / Join / Ready / Stop. |

How to run: see [`<repo>/Samples/P2pSample/README.md`](../../Samples/P2pSample/README.md) for the 4-step quick start. The rest of this document walks through how the sample is built, so you can replicate the pattern in your own game.

---

## 2. Klotho Feature Map

| Klotho area | P2pSample usage |
|---|---|
| **ECS** (`Frame`, `[KlothoComponent]`) | 1 user-defined component (`PlayerComponent`) + builtin Transform / PhysicsBody |
| **[KlothoSerializable]** | 1 command (`MoveCommand`) + 1 event (`GameOverEvent`) — both auto-serialized |
| **[KlothoDataAsset]** | 1 asset (`PlayerStatsAsset`) — speed / duration / spawn point / mass |
| **FP64 / FPVector3** | All positions / velocities / spawn offsets |
| **FPPhysicsWorld + FPRigidBody + FPBoxCollider** | 2 dynamic player bodies + 1 static ground |
| **`ICommand` / `ICommandSender`** | WASD → MoveCommand sent each tick via `OnPollInput` |
| **`SimulationEvent` (Synced)** | GameOverEvent on tick `MatchDuration / TickInterval` |
| **`KlothoSessionFlow` + `KlothoSessionDriver`** | Host + Guest entry points + Unity Update lifecycle |
| **`IKlothoNetworkService.OnSyncedEvent`** | View-side hook for GameOverEvent (note: **not** `OnEventConfirmed` — see §6 G2) |
| **`LiteNetLibTransport`** | Default UDP transport, `connectionKey = "xpTURN.P2pSample"` |
| **`EntityViewFactory` + `EntityView`** | 1 prefab (Player.prefab) + 1 subclass (P2pPlayerView) for color differentiation |

Excluded by design (see Brawler sample): ServerDriven · Dedicated Server · NavMesh · Bot HFSM · Replay · LateJoin · Reconnect · Spectator · FaultInjection.

---

## 3. Architecture (2 assemblies)

```
xpTURN.Samples.P2pSample.Sim   (noEngineReferences:true, deterministic only)
  ├─ PlayerComponent           [KlothoComponent(100)]
  ├─ MoveCommand               [KlothoSerializable(100)] : CommandBase, IsContinuousInput=true
  ├─ GameOverEvent             [KlothoSerializable(101)] : SimulationEvent, Mode=Synced
  ├─ PlayerStatsAsset          [KlothoDataAsset(100, AssetId=100, Key="PlayerStats")]
  ├─ MovementSystem            ISystem + ICommandSystem (XZ velocity from input cache)
  ├─ RespawnSystem             ISystem (Y < FallThresholdY → score--, teleport)
  └─ ScoreSystem               ISystem (raise GameOverEvent at matchEndTick)

xpTURN.Samples.P2pSample.View  (Unity-side bootstrap & UI)
  ├─ P2pGameController         MonoBehaviour — Logger / Transport / Flow / Driver hooks / 5 buttons
  ├─ P2pInputCapture           InputAction wrapper (Move composite, WASD + arrows)
  ├─ P2pSimulationCallbacks    ISimulationCallbacks — RegisterSystems / OnInitializeWorld / OnPollInput
  ├─ P2pViewCallbacks          IViewCallbacks — OnGameStart subscribes engine.OnSyncedEvent
  ├─ P2pHud                    uGUI Text — StateText / Score×2 / Timer / Result
  ├─ P2pMenu                   4 Buttons + IP/Port InputFields
  ├─ P2pEntityViewFactory      ScriptableObject — single _playerPrefab
  └─ P2pPlayerView             EntityView subclass — Renderer.material.color by PlayerId
```

Total ~795 LOC across 15 files (Sim 170 / View 625). Of that, `P2pGameController` is the largest single file at 223 LOC because Flow + Driver bootstrap + 5 button handlers + JoinAsync + lifecycle teardown have a natural floor.

---

## 4. Deterministic side (Sim assembly)

### 4-1. Components

The only game-defined component is `PlayerComponent` — score and input cache. Position / physics come from builtin types.

```csharp
[KlothoComponent(100)]
public partial struct PlayerComponent : IComponent
{
    public int PlayerId;
    public int Score;
    public FP64 LastInputH;   // cached from MoveCommand
    public FP64 LastInputV;
}
```

Player entities also carry `TransformComponent` (builtin) and `PhysicsBodyComponent` (builtin, `xpTURN.Klotho.Gameplay` — bundles `FPRigidBody` + `FPCollider` + `ColliderOffset`).

### 4-2. Command + Event

```csharp
[KlothoSerializable(100)]
public partial class MoveCommand : CommandBase
{
    public override bool IsContinuousInput => true;   // sent every tick (rollback / GapFill friendly)
    [KlothoOrder(0)] public FP64 H;
    [KlothoOrder(1)] public FP64 V;
}

[KlothoSerializable(101)]
public partial class GameOverEvent : SimulationEvent
{
    public override EventMode Mode => EventMode.Synced;   // dispatched to all peers on the same verified tick
    [KlothoOrder] public int WinnerPlayerId;              // -1 = draw
}
```

Per-field `[KlothoOrder]` is **required** — without it, the source generator skips serialization for that field, including for DataAssets baked via `JsonToBytes`.

### 4-3. DataAsset

```csharp
[KlothoDataAsset(100, AssetId = 100, Key = "PlayerStats")]
public partial class PlayerStatsAsset : IDataAsset
{
    [KlothoOrder(0)] public FP64 MoveSpeed;
    [KlothoOrder(1)] public FP64 MatchDuration;
    [KlothoOrder(2)] public FP64 FallThresholdY;
    [KlothoOrder(3)] public FPVector3 SpawnPoint;
    [KlothoOrder(4)] public FP64 InitialSpawnOffsetX;
    [KlothoOrder(5)] public FP64 PlayerMass;
    [KlothoOrder(6)] public FP64 PlayerHalfExtent;
}
```

Looked up by systems via `frame.AssetRegistry.Get<PlayerStatsAsset>()` (parameterless 1-shot lookup — single instance per type).

### 4-4. Systems

Three systems registered in `P2pSimulationCallbacks.RegisterSystems`:

```csharp
sim.AddSystem(new CommandSystem(),  SystemPhase.PreUpdate);   // builtin
sim.AddSystem(new MovementSystem(), SystemPhase.PreUpdate);
sim.AddSystem(new PhysicsSystem(64), SystemPhase.Update);     // builtin, maxEntities=64
sim.AddSystem(new RespawnSystem(),  SystemPhase.LateUpdate);
sim.AddSystem(new ScoreSystem(),    SystemPhase.LateUpdate);
sim.AddSystem(events,               SystemPhase.LateUpdate); // builtin EventSystem
```

**MovementSystem** caches input on `ICommandSystem.OnCommand` then applies XZ velocity each `Update`:

```csharp
public void OnCommand(ref Frame frame, ICommand command)   // no playerId param — read CommandBase.PlayerId
{
    if (command is not MoveCommand m) return;
    // find PlayerComponent matching m.PlayerId, update LastInputH/V
}
public void Update(ref Frame frame)
{
    var stats = frame.AssetRegistry.Get<PlayerStatsAsset>();
    var filter = frame.Filter<PlayerComponent, PhysicsBodyComponent>();
    while (filter.Next(out var entity))
    {
        ref var p    = ref frame.Get<PlayerComponent>(entity);
        ref var phys = ref frame.Get<PhysicsBodyComponent>(entity);
        phys.RigidBody.velocity.x = p.LastInputH * stats.MoveSpeed;
        phys.RigidBody.velocity.z = p.LastInputV * stats.MoveSpeed;
        // Y kept — gravity applied by PhysicsSystem
    }
}
```

**RespawnSystem** teleports + decrements score when `t.Position.y < FallThresholdY`.

**ScoreSystem** raises `GameOverEvent` exactly when `frame.Tick == matchEndTick`. `matchEndTick = (MatchDuration*1000) / frame.DeltaTimeMs` — recomputed each tick (no state to roll back):

```csharp
var evt = EventPool.Get<GameOverEvent>();    // GC-free
evt.WinnerPlayerId = ResolveWinner(ref frame);
frame.EventRaiser.RaiseEvent(evt);
```

### 4-5. World initialization

`P2pSimulationCallbacks.OnInitializeWorld` runs once per peer. It spawns `MaxPlayers` entities (P0 left, P1 right of `SpawnPoint`) and registers a 10×0.2×10 static box collider for the ground:

```csharp
for (int i = 0; i < engine.SessionConfig.MaxPlayers; i++)
{
    var entity = frame.CreateEntity();
    frame.Add(entity, new TransformComponent { Position = initialPos, Rotation = FP64.Zero, Scale = FPVector3.One });
    frame.Add(entity, new PhysicsBodyComponent {
        RigidBody = FPRigidBody.CreateDynamic(stats.PlayerMass),
        Collider  = FPCollider.FromBox(new FPBoxShape(halfExt, FPVector3.Zero)),
    });
    frame.Add(entity, new PlayerComponent { PlayerId = i, ... });
}
var physics = ((EcsSimulation)engine.Simulation).GetSystem<PhysicsSystem>();
physics.LoadStaticColliders("", new List<FPStaticCollider> { ground });
```

The static collider is registered in code (not baked from `BoxCollider` via the editor exporter) because the sample's stage geometry is fixed — keeps the minimal-asset story intact.

---

## 5. View side (Unity assembly)

### 5-1. Bootstrap (P2pGameController)

```
Awake → CreateLogger + wire driver hooks (PreSessionUpdate / Stopping)
        (idle transport pumping is owned by the driver via BindTransport — no IdlePoll hook)
Start → load DataAsset.bytes → registry build
        → transport + input + flow with CallbacksFactory
        → wire menu buttons + initial Host/Port

OnBtnHost  → flow.StartHost(simCfg, sessionCfg)
             session.HostGame("Game", MaxPlayers)         ← required to activate host role
             transport.Listen(host, port, MaxPlayers)
OnBtnJoin  → guarded by _joining flag → flow.JoinP2PAsync(...)
OnBtnReady → _hud.SetLocalReady(true); _session.SetReady(true)
OnBtnStop  → _sessionDriver.DetachAndStop()
```

Of the four `IKlothoSessionObserver` state callbacks the controller implements only `OnPhaseChanged` — `OnStateChanged` is redundant (1:1 with phase) and `OnAllPlayersReadyChanged` is host-only (see §6 G7).

### 5-2. Callbacks

- **`P2pSimulationCallbacks`** implements `ISimulationCallbacks`. `OnPollInput(playerId, tick, sender)` filters to local player, allocates from `CommandPool.Get<MoveCommand>()` (GC-free), populates from `P2pInputCapture`, and calls `sender.Send(cmd)`.
- **`P2pViewCallbacks`** implements `IViewCallbacks`. In `OnGameStart` it caches the engine, attaches it to the HUD, and subscribes `engine.OnSyncedEvent` (Synced events do not flow through `OnEventConfirmed` — that's for Regular events only). `OnTickExecuted` calls `_hud.RefreshScoreAndTimer()` once per verified tick.

### 5-3. Rendering

`P2pEntityViewFactory` returns the single Player.prefab for any entity that has `PlayerComponent`. `P2pPlayerView` (subclass of `EntityView`) reads `PlayerComponent.PlayerId` in `OnActivate` and tints its `Renderer.material.color` — host blue, guest orange. No external textures; just two `Color` fields on the view.

### 5-4. HUD

`P2pHud` displays:
- **`StateText`** — current `SessionPhase` (`Synchronized` / `Countdown` / `Playing` / …). When you press Ready during `Synchronized`, a `(Ready)` suffix appears; Countdown and later phases drop the suffix because "all ready" is implied.
- **Score × 2 + Timer** — polled from PlayerComponent each `OnTickExecuted`.
- **Result panel** — activated on `GameOverEvent`. Shows WIN / LOSE / DRAW based on `engine.LocalPlayerId`.

---

## 6. Common pitfalls

These are framework-level behaviors that the initial design pass missed; they are worth knowing before you write your own sample.

| # | Pitfall | Symptom | Fix |
|---|---|---|---|
| G1 | `flow.StartHost(...)` alone leaves `IsHost=False` | Guest's `PlayerJoinMessage` is never processed → 15s `LateJoin timeout` | Call `session.HostGame(roomName, MaxPlayers)` then `transport.Listen(...)` (mirrors Brawler) |
| G2 | Synced events are **not** dispatched via `OnEventConfirmed` | GameOverEvent never reaches view side | Subscribe `engine.OnSyncedEvent` for Synced; `OnEventConfirmed` is Regular-only |
| G3 | `USimulationConfig.MaxRollbackTicks < SyncCheckInterval` | `Config validation failed` on Initialize | Keep defaults (50 / 30) or lower both together |
| G4 | `[KlothoDataAsset]` fields without `[KlothoOrder(N)]` | Baked `.bytes` is only header + AssetId (e.g. 24 bytes); other fields read as zero (e.g. `mass=0` → DivideByZeroException) | Add `[KlothoOrder(N)]` to every field, re-run `Tools > Klotho > Convert > DataAsset JsonToBytes` |
| G5 | `OnPhaseChanged` fires only on transition (not on entry) | HUD shows blank until the next phase change | Push `session.NetworkService?.Phase` once inside `OnSessionCreated` |
| G6 | `flow.JoinP2PAsync` cancellation doesn't unregister the LiteNetLib peer | Second click → `Peer already registered id=0` | Block re-entry with a `_joining` flag; only one join in flight at a time |
| G7 | `AllPlayersReadyChanged(true)` fires synchronously on host but not on guest | UI flag asymmetric between peers | Don't depend on it for shared UI; derive from `SessionPhase` (Countdown/Playing implies all ready) |

---

## 7. Project Settings (must-set before running)

Beyond the standard Unity 6.3 / URP setup:

- **Run In Background** = ✅ (`Project Settings > Player > Resolution`). Required when running host+guest on the same machine — Unity stops the update loop in inactive windows by default, which freezes the network polling and stalls the handshake.
- **Active Input Handling** = `Input System Package (New)` (or Both). `P2pInputCapture` uses `UnityEngine.InputSystem.InputAction`.
- **Color Space** = Linear (URP convention).

See [`<repo>/Samples/P2pSample/README.md`](../../Samples/P2pSample/README.md) for the full quick-start sequence.

---

## 8. Where to read next

- **Brawler sample** — [`Brawler.md`](Brawler.md) for the full-feature reference (ServerDriven, HFSM bots, Replay, etc.).
- **Game dev API overview** — [`../GameDevAPI.md`](../GameDevAPI.md) and [`../GameDevWorkflow.md`](../GameDevWorkflow.md) for the broader authoring pattern.
