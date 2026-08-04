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
/// Runs a Friflo-backed simulation as the authority of a server-driven match: it owns the only
/// real state, ticks it once per interval, and broadcasts each verified tick to its clients.
/// </summary>
/// <remarks>
/// This replaces the room construction in Klotho's <c>RoomManager</c>, which builds an
/// <c>EcsSimulation</c> directly and so cannot host a Friflo store. Everything below it is reusable
/// as-is: <c>KlothoEngine.Initialize</c> takes an <see cref="ISimulation"/>, and the server tick
/// loop never mentions the concrete type. Assembling the same objects here keeps Klotho's sources
/// untouched, so upgrading it does not mean re-applying a patch.
/// <para>
/// Not reused, and deliberately: <c>RoomManager</c> / <c>Room</c> / <c>ServerLoop</c> are the
/// multi-room scheduler — per-room peer routing, a thread-pool update barrier, straggler
/// tracking. A single room needs none of it, and <c>Room</c> additionally demands an
/// <c>ISimulationCallbacks</c>, whose <c>RegisterSystems</c> is typed against
/// <c>EcsSimulation</c>. This class serves one room.
/// </para>
/// <para>
/// The engine is initialized <b>without</b> <c>ISimulationCallbacks</c> for that same reason. The
/// server tick loop (<c>KlothoEngine.UpdateServerTick</c>) never reads that interface — it collects
/// input from its clients rather than polling a local player — so the only thing lost is
/// <c>OnInitializeWorld</c>, and the world is seeded here instead. See
/// <see cref="FrifloKlothoSession"/> for the peer-to-peer counterpart.
/// </para>
/// </remarks>
public sealed class FrifloKlothoServer : IDisposable
{
    private readonly KlothoEngine               engine;
    private readonly ServerNetworkService       networkService;
    private readonly CommandFactory             commandFactory;
    private readonly FrifloSimulation           simulation;
    private readonly IFrifloSimulationCallbacks callbacks;
    private readonly IKLogger                   logger;

    private bool worldSeeded;
    private bool stopped;

    /// <summary>The lockstep engine driving the authoritative simulation.</summary>
    public KlothoEngine Engine => engine;

    /// <summary>The network service: roster, phase, per-client input collection.</summary>
    public IServerDrivenNetworkService NetworkService => networkService;

    /// <summary>The simulation and its Friflo store.</summary>
    public FrifloSimulation Simulation => simulation;

    /// <summary>The simulation store. Do not mutate it outside a tick.</summary>
    public EntityStore Store => simulation.Store;

    /// <summary>Engine lifecycle state.</summary>
    public KlothoState State => engine.State;

    /// <summary>Handshake / lobby / countdown progress.</summary>
    public SessionPhase Phase => networkService.Phase;

    /// <summary>Number of players that have joined.</summary>
    public int PlayerCount => networkService.PlayerCount;

    /// <summary>
    /// Start a server hosting one match.
    /// </summary>
    /// <param name="store">The simulation store. Must be empty; the world is seeded at match start.</param>
    /// <param name="transport">Transport the clients connect to.</param>
    /// <param name="simConfig">
    /// Tick rate, input delay, rollback window. Must be <see cref="NetworkMode.ServerDriven"/>; the
    /// server is the authority, so an invalid config throws here rather than being tolerated.
    /// Clients receive these values over the wire and use them in place of any local ones.
    /// </param>
    /// <param name="sessionConfig">Player counts, countdown, timeouts.</param>
    /// <param name="callbacks">Game systems, world setup. Its <c>OnPollInput</c> is never called: a server has no local player.</param>
    /// <param name="registerCommands">
    /// Registers the game's command types on the factory. Every peer must register the same type
    /// ids, or a received command deserializes as the wrong type.
    /// </param>
    /// <param name="roomName">Room name to advertise.</param>
    /// <param name="logger">Optional logger.</param>
    public static FrifloKlothoServer Create(
        EntityStore                store,
        INetworkTransport          transport,
        ISimulationConfig          simConfig,
        ISessionConfig             sessionConfig,
        IFrifloSimulationCallbacks callbacks,
        Action<CommandFactory>     registerCommands = null,
        string                     roomName         = "room",
        IKLogger                   logger           = null)
    {
        if (store     == null) throw new ArgumentNullException(nameof(store));
        if (transport == null) throw new ArgumentNullException(nameof(transport));
        if (simConfig == null) throw new ArgumentNullException(nameof(simConfig));
        if (simConfig.Mode != NetworkMode.ServerDriven) {
            throw new ArgumentException(
                $"a server-driven server needs Mode = {NetworkMode.ServerDriven}, not {simConfig.Mode}. " +
                $"for peer-to-peer play use {nameof(FrifloKlothoSession)}", nameof(simConfig));
        }
        var server = new FrifloKlothoServer(store, simConfig, sessionConfig, callbacks, registerCommands, logger);
        server.networkService.Initialize(transport, server.commandFactory, logger);
        server.InitializeEngine();
        server.networkService.CreateRoom(roomName, sessionConfig.MaxPlayers);
        return server;
    }

    private FrifloKlothoServer(
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

        // The ring must outlast the engine's rollback window, or a legitimate rollback request
        // lands on a tick whose snapshot has already been overwritten. Klotho sizes its own ring
        // the same way (see ISimulationConfig.GetSnapshotCapacity).
        simulation = new FrifloSimulation(
            store,
            systems,
            applyCommands:    ApplyCommands,
            rollbackCapacity: simConfig.GetSnapshotCapacity());

        networkService = new ServerNetworkService();
        engine         = new KlothoEngine(simConfig, sessionConfig);
    }

    /// <summary>Wire the engine to the simulation and the network.</summary>
    private void InitializeEngine()
    {
        // Subscribe to the SERVICE's game-start event, before engine.Initialize attaches the
        // engine's own handler to it. That handler is what runs Start(), so going first puts the
        // world seeding where Klotho puts OnInitializeWorld: before the tick-0 snapshot, and before
        // the state that snapshot holds is broadcast to the clients as the authoritative world.
        // The engine's own OnGameStart is the wrong hook - it fires after Start() has returned, by
        // which point the empty world has already gone out over the wire.
        networkService.OnGameStart += HandleGameStart;

        // Deliberately the overload without ISimulationCallbacks - see the note on the class.
        engine.Initialize(simulation, networkService, logger);
        engine.SetCommandFactory(commandFactory);
        networkService.SubscribeEngine(engine);
    }

    /// <summary>
    /// Seed the world once the roster and the shared random seed are settled.
    /// </summary>
    /// <remarks>
    /// Raised by the network service when every player is ready (and the countdown, if any, has
    /// elapsed), before the engine's own handler runs <c>Start()</c> — see the subscription order
    /// in <see cref="InitializeEngine"/>. Waiting until here rather than seeding at construction is
    /// what makes <c>RandomSeed</c> the value the match agreed on. The guard turns a repeat into a
    /// no-op rather than a second copy of the world.
    /// </remarks>
    private void HandleGameStart()
    {
        if (worldSeeded) {
            return;
        }
        worldSeeded = true;
        callbacks.OnInitializeWorld(simulation.Store, networkService.RandomSeed);
    }

    /// <summary>Hand a tick's commands to the game, in the deterministic order the engine fixed.</summary>
    private void ApplyCommands(EntityStore store, List<ICommand> commands, int tick)
    {
        if (callbacks is IFrifloCommandApplier applier) {
            applier.ApplyCommands(store, commands, tick);
        }
    }

    /// <summary>
    /// Advance the server one frame: pump the transport and the engine.
    /// </summary>
    /// <param name="deltaTime">Elapsed wall-clock seconds since the previous call.</param>
    /// <remarks>
    /// The engine decides how many simulation ticks that buys; it is not one tick per call. Ticks
    /// only begin once every client has acknowledged the initial state — until then the engine sits
    /// in <c>BootstrapPending</c>, so a client cannot miss the opening ticks.
    /// </remarks>
    public void Update(float deltaTime)
    {
        if (stopped) {
            return;
        }
        engine.Update(deltaTime);
    }

    /// <summary>Shut the match down and tear the engine down. Idempotent.</summary>
    public void Stop()
    {
        if (stopped) {
            return;
        }
        stopped = true;
        networkService.OnGameStart -= HandleGameStart;
        engine.Stop();
        networkService.LeaveRoom();
    }

    /// <inheritdoc />
    public void Dispose() => Stop();
}
