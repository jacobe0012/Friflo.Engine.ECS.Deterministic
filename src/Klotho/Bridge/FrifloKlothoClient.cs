using System;
using System.Collections.Generic;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Network;

// ReSharper disable once CheckNamespace
namespace Klotho.Friflo;

/// <summary>
/// Runs a Friflo-backed simulation as a client of a server-driven match: it predicts ahead of the
/// server, and rolls back and re-simulates whenever the server's verified state says it guessed
/// wrong.
/// </summary>
/// <remarks>
/// The client-side counterpart of <see cref="FrifloKlothoServer"/>, and it replaces the same piece
/// of Klotho: <c>KlothoSession.Create</c> constructs an <c>EcsSimulation</c> directly and so cannot
/// host a Friflo store. As on the server, the engine is initialized <b>without</b>
/// <c>ISimulationCallbacks</c> — that interface types <c>RegisterSystems</c> against
/// <c>EcsSimulation</c> — and input is taken from the engine's <c>OnPreTick</c> event instead,
/// which the SD client loop falls back to when no callbacks are set.
/// <para>
/// <b>The client never seeds its own world.</b> Unlike a peer-to-peer guest, it receives the
/// authoritative tick-0 state from the server as a full state and restores it
/// (<c>KlothoEngine.Start</c> skips world initialization on an SD client for exactly this reason).
/// So <see cref="IFrifloSimulationCallbacks.OnInitializeWorld"/> is not called here, and
/// <see cref="Store"/> is empty until <see cref="HasWorld"/> turns true.
/// </para>
/// </remarks>
public sealed class FrifloKlothoClient : IDisposable
{
    private readonly KlothoEngine               engine;
    private readonly ServerDrivenClientService  networkService;
    private readonly CommandFactory             commandFactory;
    private readonly FrifloSimulation           simulation;
    private readonly IFrifloSimulationCallbacks callbacks;
    private readonly CommandSink                sink;
    private readonly IKLogger                   logger;

    private bool stopped;

    /// <summary>The lockstep engine driving prediction and rollback.</summary>
    public KlothoEngine Engine => engine;

    /// <summary>The network service: roster, phase, ready state.</summary>
    public IKlothoNetworkService NetworkService => networkService;

    /// <summary>The simulation and its Friflo store.</summary>
    public FrifloSimulation Simulation => simulation;

    /// <summary>
    /// The simulation store. Empty until the server's initial state arrives — see
    /// <see cref="HasWorld"/>. Do not mutate it outside a tick.
    /// </summary>
    public EntityStore Store => simulation.Store;

    /// <summary>This peer's player id, or -1 before one is assigned.</summary>
    public int LocalPlayerId => engine.LocalPlayerId;

    /// <summary>Engine lifecycle state.</summary>
    public KlothoState State => engine.State;

    /// <summary>Handshake / lobby / countdown progress.</summary>
    public SessionPhase Phase => networkService.Phase;

    /// <summary>
    /// True once the server's authoritative world has been received and applied.
    /// </summary>
    /// <remarks>
    /// Before this, the store is empty and the engine is holding its ticks: the client has no state
    /// to predict from. Reading the store or its hash earlier compares an empty world against the
    /// server's seeded one.
    /// </remarks>
    public bool HasWorld { get; private set; }

    /// <summary>The last tick the server confirmed, or -1 before the first one.</summary>
    /// <remarks>
    /// The tick to compare against the server, rather than <c>CurrentTick</c>: the client runs
    /// ahead of the server on prediction, so its live state may still carry a guess the server has
    /// not confirmed — and may yet contradict.
    /// </remarks>
    public int LastVerifiedTick => engine.LastVerifiedTick - 1;

    /// <summary>
    /// How many times this client has had to rebuild its state from a server full state.
    /// </summary>
    /// <remarks>
    /// Zero on a healthy client. Above zero means its re-simulation of a verified tick produced a
    /// different hash than the server's — a determinism failure the engine recovered from by
    /// discarding local state and taking the server's. Recovery makes the desync invisible in a
    /// state comparison afterwards, so this counter is how it stays observable.
    /// </remarks>
    public int ResyncCount { get; private set; }

    /// <summary>Routes <see cref="IFrifloCommandSink"/> sends to the engine's input path.</summary>
    private sealed class CommandSink : IFrifloCommandSink
    {
        private readonly KlothoEngine engine;
        internal CommandSink(KlothoEngine engine) => this.engine = engine;

        /// <inheritdoc />
        public void Send(ICommand command)
        {
            if (command == null) {
                throw new ArgumentNullException(nameof(command));
            }
            // Stamps the player id, schedules the command InputDelayTicks ahead, keeps it in the
            // local buffer for prediction, and sends it to the server.
            engine.InputCommand(command);
        }
    }

    /// <summary>
    /// Create a client and begin connecting to a server-driven match.
    /// </summary>
    /// <param name="store">The simulation store. Must be empty; the world arrives from the server.</param>
    /// <param name="transport">Transport to reach the server on.</param>
    /// <param name="simConfig">
    /// Local defaults, superseded field by field once the server's authoritative configuration
    /// arrives. Must be <see cref="NetworkMode.ServerDriven"/>.
    /// </param>
    /// <param name="sessionConfig">Local defaults; the authoritative values arrive with the server's GameStart.</param>
    /// <param name="callbacks">
    /// Game systems and input. <c>OnInitializeWorld</c> is <b>not</b> called — see the note on the
    /// class — so any state it would have created must be seeded by the server instead.
    /// </param>
    /// <param name="registerCommands">
    /// Registers the game's command types on the factory. Every peer must register the same type
    /// ids, or a received command deserializes as the wrong type.
    /// </param>
    /// <param name="address">Server address.</param>
    /// <param name="port">Server port.</param>
    /// <param name="logger">Optional logger.</param>
    public static FrifloKlothoClient Connect(
        EntityStore                store,
        INetworkTransport          transport,
        ISimulationConfig          simConfig,
        ISessionConfig             sessionConfig,
        IFrifloSimulationCallbacks callbacks,
        Action<CommandFactory>     registerCommands = null,
        string                     address          = "loopback",
        int                        port             = 0,
        IKLogger                   logger           = null)
    {
        if (store     == null) throw new ArgumentNullException(nameof(store));
        if (transport == null) throw new ArgumentNullException(nameof(transport));
        if (simConfig == null) throw new ArgumentNullException(nameof(simConfig));
        if (simConfig.Mode != NetworkMode.ServerDriven) {
            throw new ArgumentException(
                $"a server-driven client needs Mode = {NetworkMode.ServerDriven}, not {simConfig.Mode}. " +
                $"for peer-to-peer play use {nameof(FrifloKlothoSession)}", nameof(simConfig));
        }

        var client = new FrifloKlothoClient(store, simConfig, sessionConfig, callbacks, registerCommands, logger);
        client.networkService.Initialize(transport, client.commandFactory, logger);
        client.InitializeEngine();
        // No room id: a single-room server has no RoomRouter in front of it, so no
        // RoomHandshakeMessage prefix is expected. JoinRoom(null) leaves _roomId at -1, which is
        // what suppresses that prefix in SendFirstMessage.
        client.networkService.JoinRoom(null);
        client.networkService.Connect(address, port);
        return client;
    }

    private FrifloKlothoClient(
        EntityStore                store,
        ISimulationConfig          simConfig,
        ISessionConfig             sessionConfig,
        IFrifloSimulationCallbacks callbacks,
        Action<CommandFactory>     registerCommands,
        IKLogger                   logger)
    {
        this.callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
        this.logger    = logger;

        commandFactory = new CommandFactory();
        registerCommands?.Invoke(commandFactory);

        var systems = new SystemRoot(store);
        callbacks.RegisterSystems(systems, store);

        simulation = new FrifloSimulation(
            store,
            systems,
            applyCommands:    ApplyCommands,
            rollbackCapacity: simConfig.GetSnapshotCapacity());

        networkService = new ServerDrivenClientService();
        engine         = new KlothoEngine(simConfig, sessionConfig);
        sink           = new CommandSink(engine);
    }

    /// <summary>Wire the engine to the simulation and the network, and take over input via <c>OnPreTick</c>.</summary>
    private void InitializeEngine()
    {
        // Deliberately the overload without ISimulationCallbacks - see the note on the class.
        engine.Initialize(simulation, networkService, logger);
        engine.SetCommandFactory(commandFactory);
        engine.OnPreTick += HandlePreTick;
        networkService.SubscribeEngine(engine);
        engine.OnResyncCompleted += HandleStateApplied;
        engine.OnFrameVerified   += HandleFrameVerified;
        networkService.OnGameStart += HandleGameStart;
    }

    /// <summary>
    /// Note that the server has replaced this client's state wholesale.
    /// </summary>
    /// <remarks>
    /// The initial full state does not come through here — it takes the dedicated bootstrap path,
    /// so <see cref="HasWorld"/> is settled at game start instead. Reaching this handler therefore
    /// means a mid-match recovery: the client's own state was wrong and has been discarded.
    /// </remarks>
    private void HandleStateApplied(int tick)
    {
        HasWorld = true;
        ResyncCount++;
    }

    /// <summary>
    /// The match has started. The server's initial state has been applied by now.
    /// </summary>
    /// <remarks>
    /// The client acknowledges that state from inside its apply handler
    /// (<c>HandleInitialFullStateReceived</c> → <c>SendBootstrapReady</c>), and the server only
    /// leaves <c>BootstrapPending</c> once every client has, so no tick can have run against an
    /// empty world by the time this is observable.
    /// </remarks>
    private void HandleGameStart() => HasWorld = true;

    /// <summary>
    /// Raised as a tick becomes verified, while the store still holds exactly that tick's state.
    /// </summary>
    /// <remarks>
    /// The only point at which a client's verified state can be observed. The client predicts
    /// several ticks ahead of the server, and re-simulates straight back up to its prediction lead
    /// once a verified tick lands, so between frames the store is always sitting on a predicted
    /// tick — never on the confirmed one. Comparing state with the server therefore has to happen
    /// here rather than after <see cref="Update"/> returns.
    /// </remarks>
    public event Action<int> OnTickVerified;

    /// <summary>Ask the game for this tick's input.</summary>
    private void HandlePreTick(int tick) => callbacks.OnPollInput(engine.LocalPlayerId, tick, sink);

    /// <summary>Forward the engine's verified-frame signal.</summary>
    private void HandleFrameVerified(int tick) => OnTickVerified?.Invoke(tick);

    /// <summary>Hand a tick's commands to the game, in the deterministic order the engine fixed.</summary>
    private void ApplyCommands(EntityStore store, List<ICommand> commands, int tick)
    {
        if (callbacks is IFrifloCommandApplier applier) {
            applier.ApplyCommands(store, commands, tick);
        }
    }

    /// <summary>Signal that this client is ready. The server starts the match once every player has.</summary>
    public void SetReady(bool ready = true) => networkService.SetReady(ready);

    /// <summary>
    /// Advance one frame: pump the transport and the engine.
    /// </summary>
    /// <param name="deltaTime">Elapsed wall-clock seconds since the previous call.</param>
    /// <remarks>
    /// The engine decides how many simulation ticks that buys; it is not one tick per call. It also
    /// throttles itself when it gets too far ahead of the server's confirmed state.
    /// </remarks>
    public void Update(float deltaTime)
    {
        if (stopped) {
            return;
        }
        engine.Update(deltaTime);
    }

    /// <summary>Leave the match and tear the engine down. Idempotent.</summary>
    public void Stop(bool keepReconnectCredentials = false)
    {
        if (stopped) {
            return;
        }
        stopped = true;
        engine.OnPreTick           -= HandlePreTick;
        engine.OnResyncCompleted   -= HandleStateApplied;
        engine.OnFrameVerified     -= HandleFrameVerified;
        networkService.OnGameStart -= HandleGameStart;
        engine.Stop();
        networkService.LeaveRoom(keepReconnectCredentials);
    }

    /// <inheritdoc />
    public void Dispose() => Stop();
}
