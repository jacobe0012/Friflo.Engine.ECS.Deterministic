# Brawler Appendix E — Bootstrap Order

> Related: [Brawler.md](Brawler.md) §11 (Phase 8 — Callbacks & Session Wiring)
> Target: `BrawlerGameController` Awake → Start → HostGame / JoinGame flow + field-injection mapping
>
> ⚠️ **Note**: The code in this appendix is a condensed view of the actual `BrawlerGameController` structure. Refer to the real source for cancellation-token handling, async exception paths, and Ready-transition details. Method names, signatures, and event names match the actual source.

---

## E-1. End-to-End Initialization Flow

All 6 session-entry points (StartHost / JoinP2PAsync / JoinServerDrivenAsync / ReconnectAsync / SpectateAsync / StartReplayFromFile) go through `KlothoSessionFlow` + `KlothoSessionDriver`. The controller does not call `KlothoSession.Create` / `KlothoSession.CreateSpectator` / `ReplaySystem.LoadFromFile` / `_session.Update` directly — those primitives are reserved as escape hatches.

```
┌───────────────────────────────────────────────────────────────┐
│ [Unity scene loads]                                           │
│                                                               │
│ BrawlerGameController.Awake()                                 │
│   • DontDestroyOnLoad                                         │
│   • CreateLogger()  → KlothoLogger.CreateDefault              │
│   • _sessionDriver hooks wired:                               │
│       PreSessionUpdate += OnPreSessionUpdate                  │
│       Stopping         += OnSessionDriverStopping             │
│       BindTransport(_transport, this, _flow)                  │
│                                                               │
│ BrawlerGameController.Start()                                 │
│   • Pre-load: StaticColliders, NavMesh, DataAssets, Registry  │
│   • new PlayerPrefsReconnectCredentialsStore()                │
│   • new LiteNetLibTransport(_logger, …)                       │
│   • new BrawlerInputCapture() + Enable()                      │
│   • Mode-specific roomId reset (P2P → -1)                     │
│   • _flow = new KlothoSessionFlow(KlothoFlowSetup { ... })    │
│       (CallbacksFactory = BuildCallbacks)                     │
│   • IKlothoSessionObserver.OnSessionCreated(session, kind)    │
│   • [KLOTHO_FAULT_INJECTION] ApplyFaultInjection()            │
│   • KlothoAutoReconnect.TryStart(...) — cold-start gate       │
│       (SD client / P2P guest only)                            │
│   • GameMenu.SetActionType(CreateRoom / JoinRoom)             │
│                                                               │
│ [Wait — GameMenu button input]                                │
│                                                               │
│ ┌─ StartHost() ────────┐  ┌─ JoinGame() ─────────────┐        │
│ │ _flow.StartHost(     │  │ JoinGameAsync (UniTask)  │        │
│ │   simCfg, sessionCfg)│  │  _flow.JoinAsync(        │        │
│ │ session.HostGame +   │  │    strategy, transport,  │        │
│ │   Transport.Listen   │  │    host, port, roomId,   │        │
│ │ SendPlayerConfig     │  │    sessionConfig, ct)    │        │
│ │                      │  │ SendPlayerConfig         │        │
│ └──────────────────────┘  └──────────────────────────┘        │
│                                                               │
│ ┌─ StartSpectator() ──────────┐  ┌─ ReconnectAsync()  ─┐      │
│ │ StartSpectatorAsync(ct)     │  │ _flow.ReconnectAsync│      │
│ │  var spTransport = new ...  │  │   (transport,       │      │
│ │  _flow.SpectateAsync(       │  │    creds,           │      │
│ │    spTransport, host, port, │  │    sessionConfig,   │      │
│ │    roomId, ct)              │  │    ct)              │      │
│ └─────────────────────────────┘  └─────────────────────┘      │
│                                                               │
│ ┌─ OnSessionCreated(session, kind) — fires on all 5 paths─┐   │
│ │  _sessionDriver.Attach(session)                         │   │
│ │  [KLOTHO_FAULT_INJECTION] FaultInjectionRuntime         │   │
│ │     .AttachToSession(session, _transport, ...)          │   │
│ │  InitializeViewSync(session.Engine, session.Simulation) │   │
│ │  (SetNetworkService is dispatched by Flow automatically │   │
│ │   when callbacks implement INetworkServiceReceiver —    │   │
│ │   Brawler callbacks do not implement it; no call site)  │   │
│ └─────────────────────────────────────────────────────────┘   │
│                                                               │
│ [Game loop — driven by KlothoSessionDriver]                   │
│   • Driver.Update → PreSessionUpdate hook (input capture)     │
│                   → session.Update(dt)                        │
│                   → PostSessionUpdate hook (no-op)            │
│   • Idle → driver pumps bound transport itself               │
│   • ISimulationCallbacks.OnInitializeWorld (once)             │
│   • Engine auto-injects InitialStateSnapshot on replay path   │
│   • IViewCallbacks.OnGameStart (once, on game start)          │
│   • Per tick: OnPollInput → Simulation.Tick → OnTickExecuted  │
└───────────────────────────────────────────────────────────────┘
```

Hook-vs-method ownership:

| Concern | Owner | Wire site |
|---|---|---|
| dt computation + `session.Update` | `KlothoSessionDriver` (Runtime.Unity) | `Awake` registers `PreSessionUpdate` hook only |
| Stop teardown order (ViewSync / EVU / replay save) | Driver `Stopping` hook | `OnSessionDriverStopping` (game side) |
| Idle transport polling | Driver pumps bound transport while idle | `BindTransport(_transport, this, _flow)`; idle disconnect → `IKlothoSessionObserver.OnIdleDisconnected` |
| RTT spike / Disconnect schedule (`KLOTHO_FAULT_INJECTION`) | `FaultInjectionRuntime` (Runtime.Unity) | `OnSessionCreated` calls `AttachToSession(...)` |
| F12 chain-stall hotkey | `FaultInjectionHotkeyDriver` MonoBehaviour | prefab serialization; `Attach(session, logger)` after session ready |
| Cold-start credentials gate | `KlothoAutoReconnect.TryStart` (Runtime.Unity helper) | `Start()` one-liner |
| Logger factory | `KlothoLogger.CreateDefault` (Runtime.Unity helper) | `Awake()` one-liner |

---

## E-2. BrawlerGameController Field Layout

```csharp
[DefaultExecutionOrder(-100)]
public class BrawlerGameController : MonoBehaviour, IKlothoSessionObserver
{
    const string KLOTHO_CONNECTION_KEY = "xpTURN.Brawler";

    [Header("Debug")]
    [SerializeField] private LogLevel _logLevel = LogLevel.Information;

    [Header("Settings")]
    [SerializeField] private BrawlerSettings _brawlerSettings = new BrawlerSettings();
    [SerializeField] private USimulationConfig _simulationConfig;
    [SerializeField] private USessionConfig    _sessionConfig;  // host-decided session policy (MaxPlayers / grace / late-join…)

    [Header("Scene References")]
    [SerializeField] private GameMenu _gameMenu;
    [SerializeField] private BrawlerViewSync _viewSync;
    // EVU reference. If the prefab has an EntityView, EVU auto-spawns it.
    // Inspector-null → EVU hook is skipped.
    [SerializeField] private xpTURN.Klotho.EntityViewUpdater _entityViewUpdater;
    // Drives KlothoSession.Update / Stop teardown through Unity Update lifecycle.
    [SerializeField] private KlothoSessionDriver _sessionDriver;
    // F12 chain-stall hotkey. Surface compile-on; Attach gated by KLOTHO_FAULT_INJECTION.
    [SerializeField] private FaultInjectionHotkeyDriver _faultInjectionHotkey;

    [Header("Static Colliders")]
    [SerializeField] private TextAsset _staticCollidersAsset;
    [Header("NavMesh")]
    [SerializeField] private TextAsset _navMeshAsset;
    [Header("DataAssets")]
    [SerializeField] private TextAsset _dataAsset;

    // Runtime state
    private ILogger _logger;
    private List<FPStaticCollider> _staticColliders;
    private FPNavMesh _navMesh;
    private List<IDataAsset> _dataAssets;
    private IDataAssetRegistry _assetRegistry;

    private KlothoSession _session;
    private KlothoSessionFlow _flow;                      // 6 entry-point builder
    private IKlothoModeStrategy _modeStrategy;            // mode dispatcher (replaces simCfg.Mode if-chain)
    private LiteNetLibTransport _transport;
    private Camera _mainCamera;
    private CancellationTokenSource _connectCts;          // cancels in-flight JoinGameAsync / ReconnectAsync
    private IReconnectCredentialsStore _credentialsStore; // PlayerPrefs-backed cold-start credentials
    // `_isStopping` removed — re-entry is guarded centrally by the idempotent `_sessionDriver.DetachAndStop` (internal guard).

    private BrawlerInputCapture _input;
    private BrawlerSimulationCallbacks _simCallbacks;
    private BrawlerViewCallbacks _viewCallbacks;

    // Spectator-mode bootstrap is delegated to _flow.SpectateAsync — the resulting KlothoSession
    // is stored in _session (same field as host / guest paths). Spectator vs replay are identified
    // at runtime via session.Engine.IsSpectatorMode / IsReplayMode (canonical signal — not the
    // NetworkService null heuristic).

    private string _replayPath = Application.dataPath + "/../Replays/brawler.rply";

    // RTT spike / disconnect schedule is owned by FaultInjectionRuntime — the controller
    // holds no _rttScheduleAnchorTime / _rttScheduleNextIdx / _lastTicks fields.

    public bool IsHost => _brawlerSettings._isHost;
    public KlothoState State => _session?.State ?? KlothoState.Idle;
    public SessionPhase Phase => _session?.NetworkService?.Phase ?? SessionPhase.None;
    // ActiveEngine / ActiveSimulation helpers removed — _session.Engine / _session.Simulation
    // are valid for host / guest / spectator / replay alike, since the framework now owns
    // the spectator engine construction (no separate _spectatorEngine field).
}

[Serializable]
public class BrawlerSettings
{
    [Header("ServerSettings")]
    [SerializeField] public string _hostAddress = "localhost";
    [SerializeField] public int _port = 777;
    // NetworkMode is sourced from _simulationConfig.Mode (single SoT — was BrawlerSettings._mode).

    [Header("ServerDriven")]
    [SerializeField] public int _roomId = 0;          // SD: 0 = single room / N = multi-room slot. P2P: forced to -1 at Start()

    [Header("P2P")]
    [SerializeField] public bool _isHost = true;
    [SerializeField] public int _botCount = 0;
    // _maxPlayers removed — MaxPlayers is sourced from _sessionConfig.MaxPlayers (single SoT).

    [Header("PlayerSettings")]
    [SerializeField] public int _characterClass = 0;  // 0=Warrior, 1=Mage, 2=Rogue, 3=Knight
}
```

Notes:
- `BrawlerGameController.Update()` no longer polls session state. The per-frame `UpdateStatus` push was lifted — state / phase / player-count / ready-state changes are routed through the `IKlothoSessionObserver` callbacks `OnStateChanged` / `OnPhaseChanged` / `OnPlayerCountChanged` / `OnAllPlayersReadyChanged` (implemented on `BrawlerGameController`, registered via `KlothoFlowSetup.LifecycleObserver`). The session-Update loop lives in `KlothoSessionDriver` — wired via `Awake` hook registration. The spectator path also routes through the same driver since the framework now returns a `KlothoSession` from `_flow.SpectateAsync`.
- `_entityViewUpdater` is the EntityViewUpdater field name (renamed from the older `_viewUpdater`); its setup runs via `InitializeViewSync(engine, simulation)` rather than a direct `Initialize(engine)` call. EVU also owns the built-in **PlayerViewRegistry** (player ↔ view lookup) — previously a sample-side helper.
- `_credentialsStore` underpins cold-start auto-reconnect (`Start()` → `KlothoAutoReconnect.TryStart(...)` → `ReconnectAsync(ct)`). It is injected into the Flow via `KlothoFlowSetup.CredentialsStore` — no separate `InjectCredentialsStoreIntoSession()` call. Process-exit teardown (`OnApplicationQuit` / `OnDestroy` → `TeardownAll`) calls `_sessionDriver?.DetachAndStop(keepReconnectCredentials: true)` so persisted credentials survive into the next launch — `StopGame` (match-end / explicit stop) keeps the default `false` and discards them via the graceful session-end path.
- `BrawlerGameController` implements `IKlothoSessionObserver`, and `KlothoFlowSetup.LifecycleObserver = this` registers it as the single subscription site for session lifecycle (replaces per-event `+=` wiring across StartHost / JoinGame / Reconnect / StopGame).
- `BrawlerPlayerConfig` is sent automatically on guest / reconnect paths via `KlothoFlowSetup.InitialPlayerConfigFactory`. The 3 controller-side `session.SendPlayerConfig(...)` calls (formerly in `StartHost` / `JoinGameAsync` / `ReconnectAsync`) are removed.
- The spectator transport is instantiated by `KlothoFlowSetup.SpectatorTransportFactory` — `StartSpectatorAsync` no longer creates a `LiteNetLibTransport` inline. Replay loading goes through `_flow.StartReplayFromFile(path)` which throws `ReplayLoadException` on failure.
- `FaultInjectionRuntime` / `FaultInjectionLoader` / `FaultInjection` calls are made without `#if KLOTHO_FAULT_INJECTION` guards (the library surface is macro-agnostic; undefined builds return null / false / empty stubs). The 4 controller-side `#if` guards (+ 2 in `BrawlerSimulationCallbacks`) are removed.

---

## E-3. Awake / Start

### Awake()

```csharp
private void Awake()
{
    DontDestroyOnLoad(gameObject);
    CreateLogger();   // KlothoLogger.CreateDefault helper

    // Driver hooks wired here so they are live even when Start() returns early via cold-start reconnect.
    _sessionDriver.PreSessionUpdate += OnPreSessionUpdate;
    _sessionDriver.Stopping        += OnSessionDriverStopping;
    _sessionDriver.BindTransport(_transport, this, _flow);
}

private void CreateLogger()
{
    _logger = KlothoLogger.CreateDefault(
        level: _logLevel,
        filePrefix: "Client",
        categoryName: "Client");
    _logger?.KInformation($"Klotho logging started!");
}

private void OnPreSessionUpdate(KlothoSession session, float dt)
{
    if (session.State == KlothoState.Running)
    {
        _input.CaptureInput();
        _input.AimDirection = GetFacingAimDirection();
    }
}

private void OnSessionDriverStopping(KlothoSession session)
{
    // Diagnostic hotkey detach first (no exception surface).
    _faultInjectionHotkey?.Detach();

    // Cleanup that requires Engine to be alive — fires before session.Stop.
    _viewSync.OnLocalCharacterSpawned   -= OnLocalCharacterSpawned;
    _viewSync.OnLocalCharacterDespawned -= OnLocalCharacterDespawned;
    _viewSync.Cleanup();
    _entityViewUpdater?.Cleanup();
    // engine.SaveReplayToFile is called from StopGame body after DetachAndStop returns,
    // preserving the original Engine.Stop → SaveReplayToFile order.
}
```

### Start()

```csharp
private void Start()
{
    // 1) Pre-load static assets
    _staticColliders = FPStaticColliderSerializer.Load(_staticCollidersAsset.bytes);
    _navMesh         = FPNavMeshSerializer.Deserialize(_navMeshAsset.bytes);
    _dataAssets      = DataAssetReader.LoadMixedCollectionFromBytes(_dataAsset.bytes);

    IDataAssetRegistryBuilder registryBuilder = new DataAssetRegistry();
    registryBuilder.RegisterRange(_dataAssets);
    _assetRegistry = registryBuilder.Build();

    _mainCamera       = Camera.main;
    _credentialsStore = new PlayerPrefsReconnectCredentialsStore();

    // 2) Transport — connectionKey gates non-Brawler clients at the LiteNetLib layer
    var logLevels = new[] { LiteNetLib.NetLogLevel.Warning, LiteNetLib.NetLogLevel.Error };
    _transport = new LiteNetLibTransport(_logger, logLevels, connectionKey: KLOTHO_CONNECTION_KEY);
    _transport.OnDisconnected += OnDisconnected;

    // 3) Input capture
    _input = new BrawlerInputCapture();
    _input.Enable();

    // 4) Mode strategy + P2P uses _roomId = -1 by convention; SD keeps the Inspector value (0..N).
    //    Strategy absorbs `simCfg.Mode != ...` branching across the controller.
    _modeStrategy = KlothoModeStrategy.Resolve(_simulationConfig);
    if (_modeStrategy.Mode != NetworkMode.ServerDriven)
        _brawlerSettings._roomId = -1;

    // 5) Flow setup — 6 entry-point builder with a single CallbacksFactory + auto-send / spectator transport factories.
    _flow = new KlothoSessionFlow(new KlothoFlowSetup
    {
        Logger            = _logger,
        Transport         = _transport,
        AssetRegistry     = _assetRegistry,
        CredentialsStore  = _credentialsStore,
        AppVersion        = Application.version,
        DeviceIdProvider  = new UnityDeviceIdProvider(),
        LifecycleObserver = this,
        CallbacksFactory  = BuildCallbacks,   // (simCfg, sessionCfg) → SessionCallbacks

        // Auto SendPlayerConfig on guest / reconnect (skipped on spectator / replay).
        InitialPlayerConfigFactory = () => new BrawlerPlayerConfig
        {
            SelectedCharacterClass = _brawlerSettings._characterClass,
        },
        // Library instantiates the spectator transport on SpectateAsync(host,port,roomId,ct).
        SpectatorTransportFactory  = () => new LiteNetLibTransport(_logger, connectionKey: KLOTHO_CONNECTION_KEY),
    });

    // Single observer callback; branch on kind and attach the driver.
    public void OnSessionCreated(KlothoSession session, SessionEntryKind kind)
    {
        _sessionDriver.Attach(session);
        switch (kind)
        {
            case SessionEntryKind.Host:
            case SessionEntryKind.Guest:
                OnHostOrGuestSessionCreated(session);
                break;
            case SessionEntryKind.Replay:
            case SessionEntryKind.Spectator:
                OnReplayOrSpectatorSessionCreated(session);
                break;
        }
    }

    // Macro-agnostic. Populate static fault-injection schedule unconditionally —
    // OnSessionCreated.AttachToSession reads this state regardless of which entry path runs.
    // Undefined builds: ApplyFaultInjection is a no-op via library stubs.
    ApplyFaultInjection();   // see E-10

    _gameMenu.IsHost = IsHost;

    // 6) Cold-start auto-reconnect helper — SD clients and P2P guests only.
    //    P2P host's death ends the session, so it is never a reconnect target.
    bool isP2PHost = _modeStrategy.Mode == NetworkMode.P2P && _brawlerSettings._isHost;
    if (!isP2PHost)
    {
        _connectCts = new CancellationTokenSource();
        bool started = KlothoAutoReconnect.TryStart(
            _credentialsStore,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Application.version,
            ct => ReconnectAsync(ct).Forget(),
            _connectCts.Token);
        if (started)
        {
            _gameMenu.SetActionType(GameMenu.ActionType.Reconnect);
            return;
        }
        _connectCts.Dispose();
        _connectCts = null;
    }
    _gameMenu.SetActionType(_brawlerSettings._isHost
        ? GameMenu.ActionType.CreateRoom
        : GameMenu.ActionType.JoinRoom);
}

// CallbacksFactory — invoked by Flow after SimulationConfig + SessionConfig are confirmed
// (host: from Inspector, guest: from ConnectionResult, spectator: from SpectatorAcceptMessage).
private SessionCallbacks BuildCallbacks(ISimulationConfig simCfg, ISessionConfig sessionCfg)
{
    int maxPlayers = sessionCfg?.MaxPlayers ?? InitialMaxPlayersGuess();
    _simCallbacks = new BrawlerSimulationCallbacks(
        _input, _logger, _staticColliders, _navMesh,
        maxPlayers, _brawlerSettings._botCount);
    _viewCallbacks = new BrawlerViewCallbacks(_simCallbacks, _logger);
    return new SessionCallbacks(_simCallbacks, _viewCallbacks);
}

// OnSessionCreated — fires once per Flow entry point right after KlothoSession.Create.
// Centralizes Driver attach + FaultInjection wiring + InitializeViewSync — game-side
// wiring drift across 5 entry points eliminated. SetNetworkService is no longer called
// here: Flow auto-dispatches it on host/guest entry when SimulationCallbacks implements
// INetworkServiceReceiver. Brawler callbacks omit that interface, so there's
// no game-side call site for SetNetworkService at all.
public void OnSessionCreated(KlothoSession session, SessionEntryKind kind)
{
    _sessionDriver.Attach(session);

#if KLOTHO_FAULT_INJECTION
    if (kind is SessionEntryKind.Host or SessionEntryKind.Guest)   // spectator/replay use their own transport
    {
        string roleLabel = IsHost ? "host" : "guest";
        FaultInjectionRuntime.AttachToSession(
            session, _transport, _logger, roleLabel,
            ct => { _ = ReconnectAsync(ct); },
            _sessionDriver);
        _faultInjectionHotkey?.Attach(session, _logger);
    }
#endif

    InitializeViewSync(session.Engine, session.Simulation);
}
```

GameMenu button wiring (`_btnHost / _btnGuest / _btnAction / _btnReplay / _btnSpectator`) is done in `OnEnable` (and torn down in `OnDisable`), not in `Start`. The action button dispatches to `StartHost()` / `JoinGame()` based on `_gameMenu.CurrentAction`.

---

## E-4. StartHost — Host Flow (P2P only)

Host entry reduces to a `_flow.StartHost(...)` call. Callbacks construction, EVU/ViewSync wiring, FaultInjection attach, and Driver attach all move into `BuildCallbacks` / `OnSessionCreated` (single source per concern, §E-3). The host method's remaining body is just the host-only steps: Mode validation, transport.Listen, SendPlayerConfig, UI transition.

```csharp
private void StartHost()
{
    if (_sessionConfig == null)
    {
        _logger?.ZLogError($"[Brawler] SessionConfig is required for host");
        _gameMenu.SetActionType(GameMenu.ActionType.CreateRoom);
        return;
    }

    _logger?.ZLogInformation($"[Brawler] Hosting game");

    // 1) Reject incompatible Inspector setting up front — silent ScriptableObject mutation
    //    is avoided (Editor play mode would persist Mode = P2P back to the .asset).
    ISimulationConfig simulationConfig;
    if (_simulationConfig != null)
    {
        if (_simulationConfig.Mode != NetworkMode.P2P)
        {
            _logger?.ZLogError($"[Brawler] StartHost requires SimulationConfig.Mode = P2P (got {_simulationConfig.Mode})");
            _gameMenu.SetActionType(GameMenu.ActionType.CreateRoom);
            return;
        }
        simulationConfig = _simulationConfig;
    }
    else
    {
        var sc = new SimulationConfig();
        sc.Mode = NetworkMode.P2P;
        simulationConfig = sc;
    }

    // 2) Flow entry — internally calls KlothoSession.Create. Flow's CallbacksFactory
    //    (BuildCallbacks, §E-3) is invoked. OnSessionCreated fires synchronously after
    //    Create — that's where Driver.Attach / InitializeViewSync run (centralized).
    _session = _flow.StartHost(simulationConfig, _sessionConfig);
    _session.HostGame("Game", _sessionConfig.MaxPlayers);

    // 3) Transport listen — host-specific. Early-return on bind failure.
    if (!_transport.Listen(_brawlerSettings._hostAddress,
                           _brawlerSettings._port,
                           _sessionConfig.MaxPlayers))
    {
        _logger?.ZLogError($"[Brawler] Failed to host on port {_brawlerSettings._port}");
        StopGame();
        return;
    }

    // 4) Broadcast the local player's character selection.
    _session.SendPlayerConfig(new BrawlerPlayerConfig
    {
        SelectedCharacterClass = _brawlerSettings._characterClass,
    });

    _gameMenu.SetActionType(GameMenu.ActionType.Ready);
}
```

Notes:
- The host path is P2P-only. SD does not use `StartHost()` — the dedicated server (Appendix H) is the SD authority. The Inspector `Mode = ServerDriven` setting is rejected with a logged error rather than silently mutated to P2P.
- `BrawlerSimulationCallbacks` construction lives in `BuildCallbacks` (§E-3) — fires once per session entry. `MaxPlayers` derives from the `sessionCfg` parameter the Flow passes in (server-authoritative for guest/spectator; Inspector value for host).
- The host no longer needs `OnGameStart += InjectInitialStateSnapshot`. The replay snapshot is auto-injected by `Engine.StartReplay`; the live host path does not need it.
- `InitializeViewSync(...)` is no longer called inline — it moves to `OnSessionCreated` so the wiring runs identically for every entry point (host / guest / reconnect / spectator / replay). `SetNetworkService(...)` is not called by the game side at all — Flow auto-dispatches it via the `INetworkServiceReceiver` opt-in marker; Brawler callbacks omit the interface.

---

## E-5. JoinGame — Guest Flow (async)

`JoinGame()` (synchronous entry) cancels any in-flight token and dispatches `JoinGameAsync(ct).Forget()`. Both P2P and SD clients route through `_flow.JoinAsync(strategy, ...)` where `strategy = KlothoModeStrategy.Resolve(simCfg)` — the strategy supplies the pre-join handshake and roomId normalization, so the call site does not branch on mode. The Flow's `UniTask` wrapper internally calls `KlothoConnectionAsync.ConnectAsync` then `flow.CreateForConnection` (`KlothoSession.Create` is not called directly by the controller).

```csharp
private void JoinGame()
{
    // Both P2P and SD (single / multi room) use the async path through Flow.
    _connectCts?.Cancel();
    _connectCts?.Dispose();
    _connectCts = new CancellationTokenSource();
    JoinGameAsync(_connectCts.Token).Forget();
}

private async UniTaskVoid JoinGameAsync(CancellationToken ct)
{
    _logger?.ZLogInformation($"[Brawler] Joining game");
    _gameMenu.ReconnectStatus = "Connecting...";

    try
    {
        // Mode dispatch goes through KlothoModeStrategy — the controller no longer
        // branches on `_simulationConfig.Mode` directly. Flow's UniTask wrapper internally
        // calls ConnectAsync + CreateForConnection; CallbacksFactory (BuildCallbacks, §E-3)
        // is invoked once ConnectionResult.SimulationConfig is confirmed. OnSessionCreated
        // fires synchronously after Create — Driver attach + simCallbacks.SetNetworkService
        // + InitializeViewSync all run there.
        _session = _modeStrategy.Mode switch
        {
            NetworkMode.ServerDriven => await _flow.JoinServerDrivenAsync(
                _transport,
                _brawlerSettings._hostAddress, _brawlerSettings._port,
                _brawlerSettings._roomId, _sessionConfig, ct),
            _ => await _flow.JoinP2PAsync(
                _transport,
                _brawlerSettings._hostAddress, _brawlerSettings._port,
                _sessionConfig, ct),
        };

        // SendPlayerConfig is no longer called here — KlothoFlowSetup.InitialPlayerConfigFactory
        // (§E-3 Start) sends BrawlerPlayerConfig automatically on guest / reconnect paths.

        _gameMenu.ReconnectStatus = null;
        _gameMenu.SetActionType(GameMenu.ActionType.Ready);
    }
    catch (OperationCanceledException) { /* canceled — cleanup + back to JoinRoom UI */ }
    catch (Exception e)                 { _logger?.ZLogError(e, $"[Brawler] JoinGame failed"); }
}
```

`ReconnectAsync(ct)` mirrors this shape with `_flow.ReconnectAsync(_transport, creds, _sessionConfig, ct)` — Flow internally calls `KlothoConnectionAsync.ReconnectAsync` and reads `roomId` from the persisted credentials (`creds.RoomId`). It catches `ReconnectFailedException` and branches on `e.Reason` (a `ReconnectRejectReason` byte; e.g. `AlreadyConnected` triggers a user-choice fallback flow). EVU/ViewSync wiring is identical (single source: `OnSessionCreated`).

IKlothoSessionObserver wires the lifecycle callbacks once at the Flow constructor (`LifecycleObserver = this`). The forwarded handler names match the prior `OnReconnecting / OnReconnectFailed(byte) / OnReconnected / OnLateJoinActive (forwarded from `IKlothoSessionObserver.OnCatchupComplete`) / OnResyncCompleted(int)` shape; the framework unsubscribes them at `Stop()` and then dispatches `OnSessionStopped()` so the game can do its own teardown (transport disconnect, null-out `_session`, etc.).

---

## E-5b. StartSpectator — Spectator Flow (async, framework-driven)

`StartSpectator()` delegates to `_flow.SpectateAsync(...)`. The Flow's UniTask wrapper internally calls `KlothoSession.CreateSpectator` (Runtime.Core factory — escape hatch for advanced users). The framework owns `SpectatorService` / two-config await / Engine + Simulation construction. The game's `CallbacksFactory` (set on the Flow constructor) fires **after** `SpectatorAcceptMessage` delivers server-authoritative `SimulationConfig` + `SessionConfig`, so callback objects (e.g. `BrawlerSimulationCallbacks(maxPlayers=...)`) can size against server values rather than the local Inspector field.

```csharp
private async UniTaskVoid StartSpectatorAsync(CancellationToken ct)
{
    try
    {
        // No-transport overload — library calls KlothoFlowSetup.SpectatorTransportFactory
        // (registered at §E-3 Start) to instantiate the transport. The transport-injection overload
        // remains as an escape hatch for custom transports.
        _session = await _flow.SpectateAsync(
            _brawlerSettings._hostAddress, _brawlerSettings._port,
            _brawlerSettings._roomId, ct);

        _gameMenu.SetActionType(GameMenu.ActionType.Playing);
    }
    catch (OperationCanceledException) { _gameMenu.SetActionType(GameMenu.ActionType.JoinRoom); }
    catch (Exception e)                 { _logger?.ZLogError(e, $"[Brawler] Spectator connect failed"); }
}
```

Notes:
- The returned `_session` is an ordinary `KlothoSession` — `KlothoSessionDriver` (attached inside `OnSessionCreated`) drives the spectator tick. There is no separate `_spectatorEngine` / `_spectatorSimulation` field any more.
- Spectator vs replay is identified at runtime via `session.Engine.IsSpectatorMode` / `IsReplayMode` (canonical signal). `OnSessionCreated` skips FaultInjection attach for spectator (spectator uses a separate `spectatorTransport`). `SetNetworkService` is no longer called by the game side at all — Flow auto-dispatches it via the `INetworkServiceReceiver` opt-in, and Brawler callbacks omit the interface so spectator/replay are naturally skipped at the Flow boundary.
- Spectator mode uses the same `IKlothoSessionObserver` for lifecycle (`OnSessionStopped`, `OnResyncCompleted`, etc.). Error-correction (capture-pre-rollback / PuP) is enabled in spectator mode just like the regular client; the EC pair is wired internally when a batch of verified input arrives.
- `SpectatorSessionSetup` has no `CredentialsStore` / `SessionConfig` / `MaxPlayers` fields — those values arrive over the wire and are owned by the framework. Only `CallbacksFactory` is required.

---

## E-6. BrawlerSimulationCallbacks — Fields, State, Reactive Hooks

> **Updated (2026-05-25)**: The spawn-retry / past-tick escalation loop documented below has been **absorbed by the framework `ReliableCommandTracker`**. The current Brawler code uses a 1-line `engine.IssueOnce(_spawnBuilder)` call (see §E-6-5 below) instead of the 7 fields + `HandleCommandRejected` / `OnResyncCompleted` / `OnPollInput` spawn-only branches shown in §E-6-1..E-6-4. The legacy listing is preserved here for context until a future revision rewrites the section against the current code.

### Replacement pattern (current shape)

```csharp
public class BrawlerSimulationCallbacks : ISimulationCallbacks
{
    private readonly Func<ICommand>     _spawnBuilder;   // bound delegate, single-alloc
    private IReliableCommandHandle      _spawnHandle;

    public BrawlerSimulationCallbacks(/* ... */)
    {
        _spawnBuilder = BuildSpawnCommand;
    }

    private void SendSpawnCommand(IKlothoEngine engine)
        => _spawnHandle = engine.IssueOnce(_spawnBuilder);   // ReliabilityPolicy.Default

    private ICommand BuildSpawnCommand()
        => new SpawnCharacterCommand(_playerConfig.SelectedCharacterClass);

    public void OnPollInput(IKlothoEngine engine, int playerId, int tick)
    {
        if (_spawnHandle != null && _spawnHandle.WouldCollideAt(tick)) return;
        if (HasCharacterFor(playerId)) _spawnHandle?.Confirm();
        // ... regular input handling ...
    }
}
```

`ReliabilityPolicy.Default` (RetryIntervalTicks=20 / ExtraDelayStep=4 / ExtraDelayMax=40 / TreatDuplicateAsAck=true / TreatPastTickAsEscalation=true) matches the prior invariant. `OnResyncCompleted` is now framework-internal (tracker resets every outstanding handle's `LastAttemptTick=-1`); the game side no longer needs to wire it explicitly. `FaultInjection.DropSpawnCommandPlayerIds` / `ForceSpawnRetryPlayerIds` hooks remain effective via tracker-internal semantics — no game-side branching.

### Legacy listing (historical context)

The callbacks class has grown to host two reactive-escalation paths and a state-driven spawn loop on top of its core responsibilities. Subsections below mirror the actual source layout.

### E-6-1. Fields & Constructor

```csharp
public class BrawlerSimulationCallbacks : ISimulationCallbacks
{
    private readonly BrawlerInputCapture _input;
    private readonly ILogger _logger;
    private readonly List<FPStaticCollider> _staticColliders;
    private readonly FPNavMesh _navMesh;
    private readonly int _maxPlayers;
    private readonly int _botCount;
    private readonly List<IDataAsset> _dataAssets;

    private IKlothoEngine _engine;

    // Spawn lifecycle — state-driven (ECS Frame query). No more "Spawned" latch.
    private int _lastSpawnAttemptTick = -1;
    private const int SpawnRetryInterval = 20;   // ~500ms @ 40Hz

    // Spawn cmd extra lead. Escalates by SPAWN_DELAY_STEP on each PastTick reject; latched until
    // the match boundary (BrawlerGameController re-news _simCallbacks).
    private int _extraSpawnDelay = 0;
    private const int SPAWN_DELAY_STEP = 4;       // ~100ms @ 40Hz
    private const int SPAWN_DELAY_MAX  = 40;      // ~1s cap — triggers one-shot Error + post-cap latch
    private bool _capHitLogged = false;
    private int  _capHitRejectCount = 0;

    public FPNavMesh     NavMesh      => _navMesh;
    public FPNavMeshQuery NavQuery    { get; private set; }
    public BotFSMSystem  BotFSMSystem { get; private set; }

    public BrawlerSimulationCallbacks(BrawlerInputCapture input, ILogger logger,
                                      List<FPStaticCollider> colliders, FPNavMesh navMesh,
                                      int maxPlayers, int botCount,
                                      List<IDataAsset> dataAssets = null)
    {
        _input = input; _logger = logger;
        _staticColliders = colliders; _navMesh = navMesh;
        _maxPlayers = maxPlayers; _botCount = botCount;
        _dataAssets = dataAssets;     // currently always passed as default (registry is shared elsewhere)
    }

    // Retained as a no-op for API symmetry; bot spawn is decided by _botCount.
    public void SetNetworkService(IKlothoNetworkService _) { }
}
```

> The previous client-reactive PastTick / rollback-burst escalation fields (`_lastServerPushTick`, `_reactiveWindowStartTick`, `SERVER_PUSH_GRACE_TICKS`, `REACTIVE_*`, `ROLLBACK_*`) were lifted into the framework class **`DynamicInputDelayPolicy`** (`com.xpturn.klotho/Runtime/Core/Engine/DynamicInputDelayPolicy.cs`). The policy is attached automatically by `KlothoSession` on non-host sessions; thresholds are sourced from `SimulationConfig` (`ServerPushGraceTicks`, `ReactiveWindowTicks`, `ReactiveEscalateThreshold`, `ReactiveStep`, `ReactiveMax`, `RollbackBurstCount`, `RollbackWindowTicks`, `ReactiveEscalateCooldownTicks`). The sample only keeps the spawn-cmd-specific escalation (above).

### E-6-2. Engine wiring — `SetEngine`

```csharp
public void SetEngine(IKlothoEngine engine)
{
    _engine = engine;
    engine.OnCommandRejected += HandleCommandRejected;       // spawn-cmd-specific only
}

public void OnInitializeWorld(IKlothoEngine engine)
{
    SetEngine(engine);
    BrawlerSimSetup.InitializeWorldState(engine, _maxPlayers, _botCount);
}

public void OnResyncCompleted(int _)
{
    // FullState resync reconstructs ECS — the previous spawn-attempt tick is no longer meaningful.
    _lastSpawnAttemptTick = -1;
}
```

### E-6-3. `OnPollInput` — state-driven spawn loop + input dispatch

```csharp
public void OnPollInput(int playerId, int tick, ICommandSender sender)
{
    if (_engine == null) return;

#if KLOTHO_FAULT_INJECTION
    // Force-retry path: bypass HasOwnCharacter so spawn cmd re-fires even after success.
    // Returns early so a Move/Attack send in the same poll does NOT overwrite the spawn cmd
    // in the InputBuffer (single command per (tick, playerId) slot).
    if (FaultInjection.ForceSpawnRetryPlayerIds.Contains(playerId)) { /* SendSpawnCommand + return */ }
#endif

    var frame = ((EcsSimulation)_engine.Simulation).Frame;
    if (!HasOwnCharacter(frame, playerId))
    {
        if (_lastSpawnAttemptTick < 0 || tick >= _lastSpawnAttemptTick + SpawnRetryInterval)
            SendSpawnCommand(_engine);

        // Skip emptyMove for two ticks:
        //   (a) the spawn-send tick itself
        //   (b) the tick whose emptyMove target tick equals the spawn cmd's target tick —
        //       collision would last-write-wins overwrite the spawn cmd in the server's InputBuffer.
        if (_lastSpawnAttemptTick >= 0
            && tick > _lastSpawnAttemptTick
            && tick != _lastSpawnAttemptTick + _extraSpawnDelay)
        {
            // emit a no-op MoveInputCommand so the tick advances on the server side
        }
        return;
    }

    // Normal poll path — capture input and dispatch Move/Attack/Skill commands.
    // _input.CaptureInput()/ConsumeOneShot() book-end the dispatch as before.
}

private static bool HasOwnCharacter(Frame frame, int playerId)
{
    var filter = frame.Filter<OwnerComponent, CharacterComponent>();
    while (filter.Next(out var entity))
    {
        ref readonly var owner = ref frame.GetReadOnly<OwnerComponent>(entity);
        if (owner.OwnerId == playerId) return true;
    }
    return false;
}
```

### E-6-4. Rejection hook (spawn-cmd only)

```csharp
// Receives only LocalPlayer's command rejections (server-unicast CommandRejectedMessage).
// Non-spawn PastTick / rollback-burst handling lives in DynamicInputDelayPolicy now.
private void HandleCommandRejected(int tick, int cmdTypeId, RejectionReason reason)
{
    if (cmdTypeId != SpawnCharacterCommand.TYPE_ID) return;

    if (reason == RejectionReason.Duplicate) { _lastSpawnAttemptTick = -1; return; }
    if (reason == RejectionReason.PastTick)
    {
        _lastSpawnAttemptTick = -1;
        if (_extraSpawnDelay < SPAWN_DELAY_MAX) _extraSpawnDelay += SPAWN_DELAY_STEP;
        else /* one-shot Error log + post-cap reject counter */;
    }
}
```

### E-6-5. `SendSpawnCommand` — uses `extraDelay` parameter

```csharp
public void SendSpawnCommand(IKlothoEngine engine)
{
    int playerId = engine.LocalPlayerId;
#if KLOTHO_FAULT_INJECTION
    if (FaultInjection.DropSpawnCommandPlayerIds.Contains(playerId))
    { _lastSpawnAttemptTick = engine.CurrentTick; return; }   // exercise self-heal path
#endif
    var rules    = ((EcsSimulation)engine.Simulation).Frame.AssetRegistry.Get<BrawlerGameRulesAsset>(1001);
    int spawnIdx = playerId % rules.SpawnPositions.Length;
    FPVector3 pos = rules.SpawnPositions[spawnIdx];

    var playerConfig = engine.GetPlayerConfig<BrawlerPlayerConfig>(playerId);
    var cmd = CommandPool.Get<SpawnCharacterCommand>();
    cmd.CharacterClass = playerConfig?.SelectedCharacterClass ?? 0;
    cmd.SpawnPosition  = new FPVector2(pos.x, pos.z);

    // Engine fills PlayerId/Tick; pass extra lead so retries overshoot far enough to pass server check.
    _lastSpawnAttemptTick = engine.CurrentTick;
    engine.InputCommand(cmd, extraDelay: _extraSpawnDelay);
}
```

`RegisterSystems(EcsSimulation)` retains the prior shape — NavMesh query / bot HFSM build / `BrawlerSimSetup.RegisterSystems`.

---

## E-7. BrawlerViewCallbacks — Fields & Constructor

```csharp
public class BrawlerViewCallbacks : IViewCallbacks
{
    private readonly BrawlerSimulationCallbacks _sim;
    private readonly ILogger _logger;

    public BrawlerViewCallbacks(BrawlerSimulationCallbacks sim, ILogger logger)
    {
        _sim = sim;
        _logger = logger;
    }

    public void OnGameStart(IKlothoEngine engine)
    {
        _sim.SetEngine(engine);
        if (!engine.IsReplayMode)
            _sim.SendSpawnCommand(engine);
    }

    public void OnTickExecuted(int tick) { }      // HUD updates: EVU + GameHUD subscribe directly

    public void OnLateJoinActivated(IKlothoEngine engine)
    {
        _sim.SetEngine(engine);
        _sim.SendSpawnCommand(engine);
    }
    // Note: A previous `Respawn(IKlothoEngine)` helper was removed — respawn is now driven by
    // the state-driven spawn loop in OnPollInput (see E-6-3).
}
```

---

## E-8. Cross-Reference Caveats

- `BrawlerSimulationCallbacks._engine` is null until `SetEngine` runs (called from `OnInitializeWorld`). `OnPollInput` guards on `_engine == null` to stay safe before that point.
- `SetNetworkService` is currently a **no-op** on `BrawlerSimulationCallbacks` — bot spawn is decided by `_botCount` and the engine reference (from `SetEngine`) covers everything else. The setter is kept for API symmetry only.
- `BrawlerViewCallbacks` only obtains `engine` at `OnGameStart`. Don't use it before that.
- View wiring goes through `InitializeViewSync(engine, simulation)` (an internal helper of `BrawlerGameController`) rather than a direct `EntityViewUpdater.Initialize(engine)` call. Call sites: every successful StartHost / JoinGameAsync / ReconnectAsync / StartReplay path.
- After the registry is built, `DataAsset` is **immutable** — runtime additions are not allowed.

---

## E-9. Replay Bootstrap

Replay re-runs locally without networking. ReplaySystem construction, `LoadFromFile`, metadata extraction, and `simConfig.Validate()` are folded into `_flow.StartReplayFromFile(path)`. Load failure throws `xpTURN.Klotho.Replay.ReplayLoadException`. `OnSessionCreated` (§E-3) then attaches the Driver and wires EVU/ViewSync — same wiring as the live paths.

```csharp
private void StartReplay()
{
    if (Phase != SessionPhase.None && Phase != SessionPhase.Disconnected) return;

    try
    {
        _session = _flow.StartReplayFromFile(_replayPath);
        _gameMenu.SetActionType(GameMenu.ActionType.Playing);
    }
    catch (xpTURN.Klotho.Replay.ReplayLoadException e)
    {
        _logger?.ZLogError(e, $"[Brawler] Replay load failed: {_replayPath}");
        _gameMenu.ReconnectStatus = "Replay load failed";
    }
}
```

> **Note**: The escape-hatch overload `_flow.StartReplay(IReplayData, ISimulationConfig)` is retained for advanced flows that source `IReplayData` from somewhere other than a file (network stream, embedded asset, test fixture). `StartReplay` auto-injects the `InitialStateSnapshot` from `IReplayData.Metadata` via `Simulation.RestoreFromFullState`, so the game no longer needs `OnGameStart += InjectInitialStateSnapshot`. The previous `Engine.StartReplayFromFile` convenience is superseded by `KlothoSessionFlow.StartReplayFromFile`.

---

## E-10. FaultInjection / RTT Spike Schedule (development-only)

Active only when the `KLOTHO_FAULT_INJECTION` define is set. The external surface (`FaultInjectionRuntime.AttachToSession` / `FaultInjectionLoader.TryLoadAndApply` / `FaultInjection` static collections) is macro-agnostic — callable without a game-side `#if KLOTHO_FAULT_INJECTION` guard. Undefined builds return null / false / empty stubs; library-internal reader bodies retain their macro guards so release cost stays at zero. The controller owns only the config-load hook + the prefab-serialized hotkey driver — the per-frame RTT schedule and disconnect schedule loops live inside `FaultInjectionRuntime` (Runtime.Unity). The 4 controller-side `#if` guards (+ 2 in `BrawlerSimulationCallbacks`) shown below in the historical snippets are removed in the current controller.

### E-10-1. Bootstrap hook — `ApplyFaultInjection()`

Called from `Start()` right after Flow setup (before the cold-start auto-reconnect branch — placement ensures cold-start paths still get the schedule populated). Loads `Assets/StreamingAssets/faultinjectionconfig.json` via `FaultInjectionLoader.TryLoadAndApply` into the static `FaultInjection` state. Missing file is silently ignored — fault injection stays off.

```csharp
private void Start()
{
    // ... (data preload, transport init, Flow setup with LifecycleObserver = this)
    // session creation is handled via IKlothoSessionObserver.OnSessionCreated(session, kind) — no += wiring

#if KLOTHO_FAULT_INJECTION
    // Populate static fault-injection schedule before any path can short-return.
    // OnSessionCreated.AttachToSession reads this state — must be loaded regardless
    // of which entry path runs (host / guest / cold-start reconnect).
    ApplyFaultInjection();
#endif

    // ... (cold-start auto-reconnect probe, UI setup)
}

#if KLOTHO_FAULT_INJECTION
private void ApplyFaultInjection()
{
    var path = Path.Combine(Application.streamingAssetsPath, "faultinjectionconfig.json");
    FaultInjectionLoader.TryLoadAndApply(path, _logger);
}
#endif
```

Schema fields (see `FaultInjectionLoader.cs`): `EmulatedRttMs`, `EmulatedRttSchedule[(atSec, rttMs)]`, `ServerGcPauseMs`, `ServerGcPauseAtTick`, `DropSpawnCommandPlayerIds`, `SuppressBootstrapAckPlayerIds`, `ForceTickOffsetDelta`.

### E-10-2. Session attach — `FaultInjectionRuntime.AttachToSession`

The per-frame RTT schedule loop is owned by `FaultInjectionRuntime`. The controller invokes it once from `OnSessionCreated` (§E-3) — `FaultInjectionRuntime` subscribes to `Engine.OnGameStart` / `OnMatchEnded` for anchor management and hooks `KlothoSessionDriver.PreSessionUpdate` for the per-tick schedule check.

```csharp
public void OnSessionCreated(KlothoSession session, SessionEntryKind kind)
{
    // ... (driver attach, etc.)

#if KLOTHO_FAULT_INJECTION
    // Spectator/replay have their own transport (spectatorTransport / none) — skip FI to avoid
    // a no-op or spurious-disconnect on the idle main _transport.
    if (kind is SessionEntryKind.Host or SessionEntryKind.Guest)
    {
        string roleLabel = IsHost ? "host" : "guest";
        FaultInjectionRuntime.AttachToSession(
            session, _transport, _logger,
            roleLabel,
            ct => { _ = ReconnectAsync(ct); },
            _sessionDriver);
        _faultInjectionHotkey?.Attach(session, _logger);
    }
#endif

    // ... (simCallbacks.SetNetworkService / InitializeViewSync)
}
```

`FaultInjectionRuntime` internally:
- Subscribes to `Engine.OnGameStart` — anchors the RTT schedule clock on first match-start. Per-client drift equals each client's `GameStartMessage` receive jitter (typically a few ms to tens of ms). Acceptable for measurement; not deterministic.
- Drives `FaultInjection.EmulatedRttSchedule` once `Phase == Playing` via the driver hook.
- Drives `FaultInjection.DisconnectSchedule` (C-1 option: `_transport.DisconnectPeer(peerId)`); duration elapsed → `ReconnectAsync(CancellationToken.None)` via the injected reconnect callback.
- Calls `RttSpikeMetricsCollector.OnMatchStart(role, localId)` / `OnSpike(atSec, rttMs)` / `EmitSummary(logger)` at match-end.

### E-10-3. F12 chain-stall hotkey — `FaultInjectionHotkeyDriver`

The F12 chain-stall toggle moves to a separate `FaultInjectionHotkeyDriver` MonoBehaviour (`[SerializeField] _faultInjectionHotkey`, prefab-serialized). `OnSessionCreated` attaches it via `_faultInjectionHotkey?.Attach(session, _logger)`; `OnSessionDriverStopping` detaches via `_faultInjectionHotkey?.Detach()`. Compile-on regardless of `KLOTHO_FAULT_INJECTION` so prefab serialization stays stable; the `Attach` call is the only site gated by the define. Input System dependency stays in Runtime.Unity (not pulled into Runtime.Core).

### E-10-4. Metrics emit

`RttSpikeMetricsCollector.EmitSummary` writes a one-line `[Metrics][RttSpike]` log at match end with spike list, chain-break counts windowed around each spike, rollback depth mean/p95, and chain-resume latency per spike. Used by the RTT spike measurement scripts. `FaultInjectionRuntime` invokes it on `Engine.OnMatchEnded`.
