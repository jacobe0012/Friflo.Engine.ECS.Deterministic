# Game Developer Workflow

> Audience: game developers building gameplay logic on top of the xpTURN.Klotho framework.
>
> Related: [API Overview](GameDevAPI.md)

---

## 1. Game Developer Scope

In xpTURN.Klotho, the area owned by the game developer is the **gameplay-logic layer** that sits on top of the framework layer.

```
┌────────────────────────────────────────────────────────────────────┐
│                  Game-Developer Authoring Area                     │
│                                                                    │
│  1 Component definition  2 System impl       3 Callbacks (det.)    │
│  [KlothoComponent(N)]    ISystem.Update()    ISimulationCallbacks  │
│  partial struct MyComp   ICommandSystem       · RegisterSystems    │
│                          IInitSystem          · OnInitializeWorld  │
│                                               · OnPollInput        │
│                                                                    │
│  4 Command definition    5 Event definition  6 View callbacks      │
│  CommandBase subclass    SimulationEvent     IViewCallbacks        │
│  [KlothoSerializable]    EventMode.Regular    · OnGameStart        │
│                          EventMode.Synced     · OnTickExecuted     │
│                                               · OnLateJoinActivated│
└────────────────────────────────────────────────────────────────────┘
                                │ KlothoSession.Create(setup)
┌────────────────────────────────────────────────────────────────────┐
│                   xpTURN.Klotho Framework                          │
│   ISimulationCallbacks · IViewCallbacks · KlothoSession · Engine   │
│   EcsSimulation · Frame · SystemRunner · EntityManager             │
└────────────────────────────────────────────────────────────────────┘
```

---

## 2. Recommended Workflow

> **Determinism guardrail (build-time)** — the `DeterminismAnalyzer` shipped in `KlothoGenerator.dll` flags determinism hazards while you author, before they surface as replay/rollback desync. Inside a deterministic-context type (one implementing a deterministic interface / inheriting a deterministic base, or a ref-`Frame` helper method) it warns on: `KLOTHO_DET002` float/double; `KLOTHO_DET003` non-deterministic API/type (`Mathf`, `Random`, `System.Math`, `DateTime`, float-backed `UnityEngine.Vector2/3/4`/`Quaternion`/`Matrix4x4`); `KLOTHO_DET004` `UnityEngine.Time`. Use `FP64` / `FPVector*` / `DeterministicRandom` (seeded from the `RandomSeedComponent` singleton) instead. The FP64 conversion boundary (`FromFloat` / `ToFloat` / …) is exempt, and test / tool assemblies are skipped.

### Step 1: Define Components (use IDs ≥ 100)

Use IDs of 100 or above to avoid colliding with the built-in component-ID range (1–99). The source generator emits `Serialize` / `Deserialize` / `GetSerializedSize` / `GetHash` automatically. Duplicate IDs are caught at compile time.

```csharp
[KlothoComponent(100)]
public partial struct HeroComponent : IComponent
{
    public int Level;
    public int Experience;
    public int ClassId;
}
```

### Step 2: Define Commands

Inherit from `CommandBase` and apply `[KlothoSerializable(N)]`. `CommandType`, `SerializeData`, and `DeserializeData` are emitted by the source generator.

```csharp
[KlothoSerializable(100)]
public partial class CastSkillCommand : CommandBase
{
    [KlothoOrder] public int SkillId;
    [KlothoOrder] public FPVector3 TargetPosition;
}
```

### Step 3: Implement Systems

```csharp
public class HeroSystem : ISystem, IInitSystem
{
    public void OnInit(ref Frame frame)
    {
        // Create the initial hero entity
        var hero = frame.CreateEntity();
        frame.Add(hero, new TransformComponent());
        frame.Add(hero, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });
        frame.Add(hero, new HeroComponent { Level = 1, ClassId = 1 });
    }

    public void Update(ref Frame frame)
    {
        var filter = frame.Filter<HeroComponent, HealthComponent>();
        while (filter.Next(out var entity))
        {
            ref var hero = ref frame.Get<HeroComponent>(entity);
            // hero logic
        }
    }
}
```

### Step 4: Implement Callbacks & Create a Session

Callbacks are split into two interfaces.
- **`ISimulationCallbacks`** — common to the deterministic side (server, client, replay all behave the same). `RegisterSystems`, `OnInitializeWorld`, `OnPollInput`, `OnPlayerJoinedWorld`.
- **`IViewCallbacks`** — client view only (non-determinism allowed). `OnGameStart`, `OnTickExecuted`, `OnLateJoinActivated`.

`RegisterSystems` is called immediately after `EcsSimulation` construction and before `KlothoEngine.Initialize()`. Construct `EventSystem` without arguments; it references `frame.EventRaiser` directly each tick.

```csharp
public class MySimulationCallbacks : ISimulationCallbacks
{
    public void RegisterSystems(EcsSimulation sim)
    {
        var events = new EventSystem();
        sim.AddSystem(new CommandSystem(),        SystemPhase.PreUpdate);
        sim.AddSystem(new HeroSystem(),           SystemPhase.Update);
        sim.AddSystem(new CombatSystem(events),   SystemPhase.Update);
        sim.AddSystem(new MovementSystem(),       SystemPhase.Update);
        sim.AddSystem(events,                     SystemPhase.LateUpdate);
    }

    public void OnInitializeWorld(IKlothoEngine engine)
    {
        // Called before SaveSnapshot(0). Runs identically on every peer — deterministic code only.
        // Examples: fixed-terrain / item placement, initial world spawns
    }

    public void OnPollInput(int playerId, int tick, ICommandSender sender)
    {
        // Per-tick command send (no send → EmptyCommand auto-injected)
        var cmd = CommandPool.Get<MoveCommand>();
        // ... fill input ...
        sender.Send(cmd);
    }

    public void OnPlayerJoinedWorld(IKlothoEngine engine, Frame frame, int playerId)
    {
        // Late-join analog of OnInitializeWorld — seed this player's deterministic world state
        // (e.g. an entitlement-derived loadout). Leave empty if there is no per-join state.
    }
}

public class MyViewCallbacks : IViewCallbacks
{
    public void OnGameStart(IKlothoEngine engine)           { /* spawn commands · UI init */ }
    public void OnTickExecuted(int tick)                    { /* view update */ }
    public void OnLateJoinActivated(IKlothoEngine engine)   { /* late-join initial logic */ }
}

// Construct a KlothoSessionFlow once during startup and reuse it for every mode.
// KlothoFlowSetup carries the long-lived dependencies — entry methods only take per-call params.
_flow = new KlothoSessionFlow(new KlothoFlowSetup
{
    Logger            = logger,
    Transport         = transport,
    AssetRegistry     = dataAssetRegistry,
    CredentialsStore  = credentialsStore,         // optional — warm-reconnect ticket persistence
    AppVersion        = Application.version,        // Godot: ProjectSettings.GetSetting("application/config/version") or a literal
    DeviceIdProvider  = new UnityDeviceIdProvider(), // Godot: new GodotDeviceIdProvider() (OS.GetUniqueId()); or use .WithGodotDefaults() on the builder (recommended)
    LifecycleObserver = this,                     // bulk-subscribed IKlothoSessionObserver
    CallbacksFactory  = (simCfg, sessionCfg) =>
        new SessionCallbacks(new MySimulationCallbacks(), new MyViewCallbacks()),

    // Auto-send PlayerConfig on guest / reconnect paths (skipped on spectator / replay).
    // The factory runs per-session so it always observes the latest user selection.
    InitialPlayerConfigFactory = () => new MyPlayerConfig { /* ... */ },

    // Spectator transport — library calls this from `SpectateAsync(host, port, roomId, ct)`.
    SpectatorTransportFactory  = () => new LiteNetLibTransport(logger, connectionKey: ConnectionKey),
});

// Session creation + state changes arrive through IKlothoSessionObserver
// (KlothoFlowSetup.LifecycleObserver = this) — one callback set for all modes, no per-frame polling.
// Branch on `kind`, not simCfg.Mode:
public void OnSessionCreated(KlothoSession session, SessionEntryKind kind)
{
    _sessionDriver.Attach(session);
    if (kind is SessionEntryKind.Host or SessionEntryKind.Guest) OnHostOrGuestSessionCreated(session);
    else                                                         OnReplayOrSpectatorSessionCreated(session); // Replay / Spectator
}
public void OnStateChanged(KlothoState s)      => UpdateStateUI(s);
public void OnPhaseChanged(SessionPhase p)     => UpdatePhaseUI(p);
public void OnPlayerCountChanged(int n)        => UpdatePlayerCountUI(n);
public void OnAllPlayersReadyChanged(bool r)   => UpdateReadyUI(r);

// Entry points — pick one per game mode. Branch by KlothoModeStrategy.Resolve(simCfg),
// not by inspecting simCfg.Mode directly.
_session = _flow.StartHostAndListen(uSimulationConfig, uSessionConfig, "MyRoom", "0.0.0.0", 9050); // P2P host (StartHost + HostGame + Listen, auto-teardown on failure)
_session = _flow.StartHost(uSimulationConfig, uSessionConfig);                                 // P2P host (low-level — caller drives HostGame + Listen)
_session = await _flow.JoinP2PAsync(transport, host, port, uSessionConfig, ct);                // P2P guest
_session = await _flow.JoinServerDrivenAsync(transport, host, port, roomId, uSessionConfig, ct); // SD client
_session = await _flow.ReconnectAsync(transport, creds, uSessionConfig, ct);                   // cold-start reconnect (creds: PersistedReconnectCredentials)
_session = await _flow.SpectateAsync(host, port, roomId, ct);                                  // spectator (transport via factory)
_session = _flow.StartReplayFromFile(replayPath);                                              // replay (throws ReplayLoadException)

// FaultInjection: macro-agnostic — call without #if KLOTHO_FAULT_INJECTION. Undefined builds
// return a null stub (release cost stays at zero — library-internal readers retain their macro guards).
FaultInjectionRuntime.AttachToSession(_session, transport, logger, /* roleLabel */ "host",
    reconnectFn: ct => ReconnectAsync(ct), _sessionDriver);
```

> **Escape hatch — `KlothoSession.Create(KlothoSessionSetup)`**: the direct factory is still available for games whose architecture does not fit the Flow pattern (custom retry orchestration, multi-session test harnesses, etc.). The Flow is a recommended thin wrapper, not a wall.

#### NetworkService handle (opt-in)

If your `ISimulationCallbacks` implementation needs the `IKlothoNetworkService` handle on host/guest entry (e.g. to issue room-level network operations alongside per-tick callbacks), implement the `INetworkServiceReceiver` marker interface — the Flow auto-dispatches `SetNetworkService` just before invoking the observer's `OnSessionCreated` callback (kind-gated to Host/Guest), so the handle is ready when `OnSessionCreated` runs. Most game callbacks don't need it; omit the interface and Flow skips dispatch entirely.

```csharp
public class MySimulationCallbacks : ISimulationCallbacks, INetworkServiceReceiver
{
    private IKlothoNetworkService _net;
    public void SetNetworkService(IKlothoNetworkService svc) { _net = svc; }
    // ... rest of callbacks ...
}
```

#### Reliable-once command (spawn / room-state / one-shot)

For commands that must reach the deterministic timeline exactly once despite duplicate / past-tick rejects, use `engine.IssueOnce`. The framework `ReliableCommandTracker` owns the retry-interval cooldown, past-tick escalation, empty-move collision avoidance, and resync reset.

> Mark the command `IReliableCommand` (subtype of `ISystemCommand`; add a serialized `[KlothoOrder] int SequenceNumber { get; set; }` + `OrderKey`) to route it onto the reliable channel — in **ServerDriven** the server assigns the execution tick (confirmed-only, no retry/escalation/`WouldCollideAt`); in **P2P** (or for a plain `IssueOnce` command) it uses the legacy path below. The call site is identical for both. Reliable commands are **not predicted** — latency-insensitive actions only (spawn / purchase / surrender).

```csharp
private Func<ICommand>          _spawnBuilder;     // bound delegate, single-alloc
private IReliableCommandHandle  _spawnHandle;

public MySimulationCallbacks(/* ... */)
{
    _spawnBuilder = () => new SpawnCharacterCommand(_selectedClass);   // payload re-evaluated per retry
}

private void SendSpawn(IKlothoEngine engine)
{
    _spawnHandle = engine.IssueOnce(_spawnBuilder);   // ReliabilityPolicy.Default
}

public void OnPollInput(int playerId, int tick, ICommandSender sender)
{
    if (_spawnHandle != null && _spawnHandle.WouldCollideAt(tick)) return;   // empty-move skip
    if (HasCharacterFor(playerId)) _spawnHandle?.Confirm();                  // state-driven ack
    // ... regular per-tick input ...
}
```

`ReliabilityPolicy` exposes `RetryIntervalTicks` / `ExtraDelayStep` / `ExtraDelayMax` / `TreatDuplicateAsAck` / `TreatPastTickAsEscalation`. `ReliabilityPolicy.Default` (20 / 4 / 40 / true / true) matches the prior Brawler spawn invariant; supply a custom policy for other reliable-input scenarios.

#### System lookup from a callback boundary

`EcsSimulation.GetSystem<T>()` / `TryGetSystem<T>` / `GetSystems<T>(buffer)` expose a registered system's secondary interface (e.g. `PhysicsSystem` → `IFPPhysicsWorldProvider`) without a process-wide static slot. Stash `simulation` on `RegisterSystems(EcsSimulation simulation)` entry; resolve in the property getter.

```csharp
private EcsSimulation _simulation;

public void RegisterSystems(EcsSimulation simulation)
{
    _simulation = simulation;                                       // stash for later lookup
    // ... AddSystem(...) ...
}

public IFPPhysicsWorldProvider PhysicsProvider
    => _simulation?.GetSystem<PhysicsSystem>();                     // first-match lookup, alloc-free
```

### Step 5: Define Events

Inherit from `SimulationEvent` and apply `[KlothoSerializable(N)]`. `EventTypeId`, `Serialize`, `Deserialize`, and `GetContentHash` are emitted by the source generator. Duplicate TypeIds are caught at compile time.

```csharp
[KlothoSerializable(100)]
public partial class CastSkillEvent : SimulationEvent
{
    [KlothoOrder]
    public EntityRef Caster;
    [KlothoOrder]
    public int SkillId;
}
```

### Step 6: Subscribe to Events (View Layer)

```csharp
var engine = session.Engine;
engine.OnEventPredicted  += (tick, evt) => HandleEventPredicted(evt);
engine.OnEventConfirmed  += (tick, evt) => HandleEventConfirmed(evt);
engine.OnEventCanceled   += (tick, evt) => HandleEventCanceled(evt);
engine.OnSyncedEvent     += (tick, evt) => HandleSyncedEvent(evt);

// OnEventPredicted : First firing of a Regular event on a Predicted tick
//                    (play VFX immediately; may be canceled).
// OnEventConfirmed : First firing of a Regular event that lands directly on Verified
//                    without a Predicted firing (verified-direct, replay,
//                    new-on-rollback / content-changed). No re-fire if Predicted preceded —
//                    write the handler the same as Predicted.
// OnEventCanceled  : Fires when a Predicted event is invalidated by rollback.
// OnSyncedEvent    : EventMode.Synced events — fired only on Verified ticks
//                    (game over, level up, round timer, etc. — confirmed-only state changes).
```

#### Helper — `EngineEventOneShot.Subscribe` (one-shot Predicted/Confirmed/Canceled triple)

For a per-entity one-shot event (animation trigger, attack VFX) that needs the same handler on Predicted and Confirmed (with optional late-dispatch guard) plus a cancel-side cleanup, use `EngineEventOneShot.Subscribe`. The helper wraps all three channels into a single subscription with the de-dupe / late-guard wiring done for you.

```csharp
private EngineEventSubscription _attackSub;

public override void OnActivate(FrameRef frame)
{
    _attackSub = EngineEventOneShot.Subscribe<AttackActionEvent>(
        Engine,
        filter:    e => e.Attacker.Index == EntityRef.Index,
        onPlay:    _ => PlayAttackAnimation(),
        onCancel:  _ => CancelActionTrigger(),
        lateGuard: HasActiveAction);   // skip stale Predicted/Confirmed when action already ended
}

public override void OnDeactivate()
{
    _attackSub?.Dispose();             // IDisposable cleanup is required
}
```

Scope is limited to the Predicted+Confirmed+Canceled triple. Verified-time fallback events (e.g. `ActionCompletedEvent`) keep using the `OnSyncedEvent` channel — `EngineEventOneShot` doesn't replace them.

### Step 7: View Sync — EntityViewFactory / EntityViewUpdater

The View layer is three pieces on both engines: a **Factory** (decides per-entity `BindBehaviour` (Verified / NonVerified) + `ViewFlags`, and creates the view), a single-scene **Updater** that runs Reconcile on every `OnTickExecuted` to spawn/destroy automatically, and the per-entity **View** itself. The engines differ only in the host types and how the view is instantiated:

| Piece | Unity | Godot |
| ---- | ---- | ---- |
| Factory | `EntityViewFactory` (ScriptableObject; `ResolvePrefab → GameObject`) | `EntityViewFactory` (abstract class; `ResolvePrefab → PackedScene`) |
| View creation | `async UniTask<EntityView> CreateAsync` | **synchronous** `EntityViewNode Create` |
| Updater | `EntityViewUpdater` (MonoBehaviour, `Update`) | `EntityViewUpdaterNode` (`Node`, `_Process`, `ProcessPriority = 1000`) |
| View | `EntityView` (prefab MonoBehaviour) | `EntityViewNode` (`Node3D` in a `.tscn`) |

The decision API (`TryGetBindBehaviour` / `GetViewFlags`) and the view lifecycle callbacks (`OnInitialize` / `OnActivate` / `OnUpdateView` / `OnLateUpdateView` / `OnDeactivate`) are **identical** on both.

**Unity:**

```csharp
// 1. Factory subclass — author as a ScriptableObject and assign to the scene's EntityViewUpdater
[CreateAssetMenu(menuName = "MyGame/HeroViewFactory", fileName = "HeroViewFactory")]
public class HeroViewFactory : EntityViewFactory
{
    [SerializeField] private GameObject _heroPrefab;

    // Decide whether this entity is rendered as a view and which BindBehaviour to bind under.
    // (false → spawn skipped). Engine queries are allowed only inside this method
    // (and GetViewFlags / CreateAsync).
    public override bool TryGetBindBehaviour(Frame frame, EntityRef entity, out BindBehaviour behaviour)
    {
        if (!frame.Has<HeroComponent>(entity)) { behaviour = BindBehaviour.Verified; return false; }

        bool isLocal = frame.Has<OwnerComponent>(entity)
                    && frame.GetReadOnly<OwnerComponent>(entity).OwnerId == Engine.LocalPlayerId;
        // Local: predicted (NonVerified) / Remote: verified — trades responsiveness vs. accuracy
        behaviour = isLocal ? BindBehaviour.NonVerified : BindBehaviour.Verified;
        return true;
    }

    // View options such as snapshot-interpolation on/off — local is immediate, remote is interpolated
    public override ViewFlags GetViewFlags(Frame frame, EntityRef entity)
    {
        bool isLocal = frame.Has<OwnerComponent>(entity)
                    && frame.GetReadOnly<OwnerComponent>(entity).OwnerId == Engine.LocalPlayerId;
        return isLocal ? ViewFlags.None : ViewFlags.EnableSnapshotInterpolation;
    }

    // Prefab instantiation — Rent if a Pool is present, otherwise Instantiate directly
    public override async UniTask<EntityView> CreateAsync(
        Frame frame, EntityRef entity, BindBehaviour behaviour, ViewFlags flags)
    {
        if (Pool != null) return await Pool.Rent(_heroPrefab);

        var go   = Object.Instantiate(_heroPrefab);
        return go.GetComponent<EntityView>();
    }

    // Override if needed (default: Object.Destroy; auto-Return when a Pool is present)
    // public override void Destroy(EntityView view) { ... }
}

// 2. Attach an EntityView subclass to the prefab — the Updater injects EntityRef/Engine and drives the lifecycle
public class HeroView : EntityView
{
    private int _ownerId;

    public override void OnInitialize()           { base.OnInitialize();       /* once on first prefab creation (skipped on pool reuse) */ }
    public override void OnActivate(FrameRef frame){
        base.OnActivate(frame);
        // Cache the owner from the entity at spawn time; consumed by OwnerMatches below.
        if (frame.Frame.Has<OwnerComponent>(EntityRef))
            _ownerId = frame.Frame.GetReadOnly<OwnerComponent>(EntityRef).OwnerId;
    }
    public override void OnDeactivate()           { base.OnDeactivate();       /* just before destroy / pool return */ }
    public override void OnUpdateView()           { base.OnUpdateView();       /* per tick — inside InternalUpdateView from EVU.OnTickExecuted */ }
    public override void OnLateUpdateView()       { base.OnLateUpdateView();   /* per frame — inside EVU.LateUpdate */ }

    // REQUIRED override for any view bound to an entity with OwnerComponent. EVU uses this on
    // Reconcile to detect entity-slot reuse with owner swap (e.g. player A's character respawn
    // landing on the same ECS entity slot previously held by player B during rollback). The
    // base implementation returns false on purpose — without override, EVU rebinds every
    // Reconcile, which surfaces as continuous churn in [ViewLife][Rebind] logs / profiler.
    // Owner-agnostic views (no OwnerComponent on the bound entity) do NOT need to override.
    public override bool OwnerMatches(int ownerId) => _ownerId == ownerId;
}

// 3. Scene wiring — bind the Factory asset and (optionally) DefaultEntityViewPool to EntityViewUpdater.
//    Call Initialize during session bootstrap.
evu.Initialize(session.Engine);

// 4. On session shutdown — return active views, unsubscribe OnTickExecuted
//    (GameObjects are preserved for reuse).
evu.Cleanup();
```

**Transform pipeline (base-delegated)** — Do **not** override `ApplyTransform` / `LateUpdate` / hand-roll a lerp in subclasses. The base `EntityView` performs lerp + `ApplyTransform` + `UpdatePositionParameter` populate inside `InternalLateUpdateView` (fused with `_errorVisual.Tick`) so that tick-rate < frame-rate environments reflect every per-frame `PredictedAlpha` change without stale-lerp stutter. `UpdatePositionParameter` zeros `ErrorVisualVector` / `ErrorVisualQuaternion` when `EnableSnapshotInterpolation` is set so the verified-frame interpolation path doesn't double-correct the rollback delta. Game subclasses override `OnUpdateView` / `OnLateUpdateView` for game-data cache + visual-feedback toggles only.

**How it works**
- **Reconcile timing** — runs each tick on the `IKlothoEngine.OnTickExecuted` hook
  1. Scans `VerifiedFrame` / `PredictedFrame` and collects entities whose `TryGetBindBehaviour` matches the corresponding path
  2. New entities → asynchronous spawn via `CreateAsync` (a spawn-sequence counter + `EntityRef.Version` prevent duplicate / stale calls)
  3. Disappeared entities → `OnDeactivate`, then `Factory.Destroy` (auto-return when a Pool is present)
- **Hybrid dedup (`EntityRef.Version` + Owner)** — on every Reconcile, EVU compares the live view against the current frame on two axes:
  - `EntityRef.Version` mismatch → entity slot was reused after destroy / rollback → **Rebind** (destroy old view, spawn new) and emit `[ViewLife][Rebind]` (Debug)
  - For entities with `OwnerComponent`, EVU also calls `EntityView.OwnerMatches(currentOwnerId)`. Mismatch → stale destroy. Owner-bearing views **must** override `OwnerMatches`; the default returns `false` to fail loudly.
- **Async safety** — if an entity disappears mid-spawn (or its slot is reused) before `CreateAsync` resolves, the result is discarded automatically via the spawn-counter + version mismatch.
- **Factory init constraint** — do not query `Engine.LocalPlayerId` / `IsServer` from the constructor or `OnEnable`. Those values are only guaranteed inside `TryGetBindBehaviour` / `GetViewFlags` / `CreateAsync`.
- **Pool** — wiring `DefaultEntityViewPool` to the `EntityViewUpdater._pool` field enables prefab reuse via `Rent` / `Return` (optional).

**Godot:**

The same three pieces, with `Node`-based hosts. The factory is a plain abstract class injected with a `PackedScene`, and `Create` is **synchronous** (instancing a `PackedScene` does not need `await`):

```csharp
// 1. Factory subclass — instantiated in code with the player scene (no ScriptableObject asset).
public class HeroViewFactory : EntityViewFactory
{
    private readonly PackedScene _heroScene;
    public HeroViewFactory(PackedScene heroScene) => _heroScene = heroScene;

    protected override bool ShouldRender(Frame frame, EntityRef entity)
        => frame.Has<HeroComponent>(entity);

    protected override PackedScene ResolvePrefab(Frame frame, EntityRef entity)
        => _heroScene;

    // TryGetBindBehaviour / GetViewFlags: same signatures + semantics as Unity (override as needed).
    // Create() is provided by the base (instantiates ResolvePrefab's PackedScene, root = EntityViewNode);
    // override only for custom instancing.
}

// 2. View — subclass EntityViewNode (root of the .tscn). Same lifecycle callbacks as Unity's EntityView.
public partial class HeroView : EntityViewNode
{
    public override void OnActivate(FrameRef frame) { base.OnActivate(frame); /* cache owner, etc. */ }
    public override void OnUpdateView()            { base.OnUpdateView();     /* per-tick game data */ }
    // OnInitialize / OnLateUpdateView / OnDeactivate / OwnerMatches — same contract as Unity.
}

// 3. Scene wiring — add an EntityViewUpdaterNode to the scene, assign the factory, Initialize on bootstrap.
//    The node self-drives Reconcile via _Process (ProcessPriority = 1000, runs after the session driver).
evu.Factory = new HeroViewFactory(heroScene);
evu.Initialize(session.Engine);
```

The transform pipeline, Reconcile timing, hybrid dedup, and pooling (`DefaultGodotEntityViewPool`) behave the same as Unity — only the host types (`Node3D` / `.tscn` vs MonoBehaviour / prefab) and the synchronous `Create` differ.

---

Last updated: 2026-06-18 — IReliableCommand reliable command channel
