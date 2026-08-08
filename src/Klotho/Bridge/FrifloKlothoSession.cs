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
/// Wires a Friflo-backed simulation into Klotho's networked engine: a peer that can host or join a
/// P2P match and run it under lockstep, prediction and rollback.
/// </summary>
/// <remarks>
/// This replaces <c>KlothoSession.Create</c>, which constructs an <c>EcsSimulation</c> directly and
/// so cannot host a Friflo store. Everything below it is reusable as-is: <c>KlothoEngine.Initialize</c>
/// takes an <see cref="ISimulation"/>, and the rollback and prediction paths never mention the
/// concrete type. Assembling the same objects here keeps Klotho's sources untouched, so upgrading it
/// does not mean re-applying a patch.
/// <para>
/// The engine is initialized <b>without</b> <c>ISimulationCallbacks</c>. That interface types
/// <c>RegisterSystems</c> against <c>EcsSimulation</c>, and with it null the engine falls back to
/// its <c>OnPreTick</c> event for input (<c>KlothoEngine</c>, the else-branch after the poll-input
/// call), which is free of ECS types. Input is forwarded from there to
/// <see cref="IFrifloSimulationCallbacks.OnPollInput"/>. Two consequences: the engine builds no
/// <c>CommandSender</c>, so commands go through <c>InputCommand</c>; and it never calls
/// <c>OnInitializeWorld</c>, so the world is seeded here instead, before the engine starts.
/// </para>
/// <para>
/// Only P2P (<see cref="NetworkMode.P2P"/>) is wired. Server-driven play needs the dedicated-server
/// path, which constructs its simulation inside <c>RoomManager</c> and would need the same treatment.
/// </para>
/// </remarks>
public sealed class FrifloKlothoSession : IDisposable
{
    private readonly KlothoEngine                 engine;
    private readonly KlothoNetworkService         networkService;
    private readonly CommandFactory               commandFactory;
    private readonly FrifloSimulation             simulation;
    private readonly IFrifloSimulationCallbacks   callbacks;
    private readonly CommandSink                  sink;
    private readonly IKLogger                     logger;

    private bool worldSeeded;
    private bool stopped;

    /// <summary>The lockstep engine driving this peer.</summary>
    public KlothoEngine Engine => engine;

    /// <summary>The network service: roster, phase, ready state.</summary>
    public IKlothoNetworkService NetworkService => networkService;

    /// <summary>The simulation and its Friflo store.</summary>
    public FrifloSimulation Simulation => simulation;

    /// <summary>The simulation store. Do not mutate it outside a tick.</summary>
    public EntityStore Store => simulation.Store;

    /// <summary>This peer's player id, or -1 before one is assigned.</summary>
    public int LocalPlayerId => engine.LocalPlayerId;

    /// <summary>Engine lifecycle state.</summary>
    public KlothoState State => engine.State;

    /// <summary>Handshake / lobby / countdown progress.</summary>
    public SessionPhase Phase => networkService.Phase;

    /// <summary>True once every player in the room has readied up.</summary>
    public bool AllPlayersReady => networkService.AllPlayersReady;

    /// <summary>How many rollback requests the engine could not serve.</summary>
    /// <remarks>
    /// Counted here rather than read off <c>KlothoEngine.Stats</c>, which is compiled only under
    /// <c>#if DEBUG</c> — reaching for it puts the whole assembly out of reach of a Release build,
    /// which is the configuration a server actually ships. The engine raises
    /// <c>OnRollbackFailed</c> in every configuration, so the same fact is available without the
    /// dependency. <see cref="FrifloSimulation.RollbackCount"/> is the companion for the
    /// succeeded case, and exists for the same reason.
    /// </remarks>
    public int FailedRollbackCount { get; private set; }

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
            // Stamps the player id and schedules the command InputDelayTicks ahead.
            engine.InputCommand(command);
        }
    }

    private FrifloKlothoSession(
        EntityStore                 store,
        ISimulationConfig           simConfig,
        ISessionConfig              sessionConfig,
        IFrifloSimulationCallbacks  callbacks,
        Action<CommandFactory>      registerCommands,
        IKLogger                    logger)
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

        networkService = new KlothoNetworkService();
        engine         = new KlothoEngine(simConfig, sessionConfig);
        sink           = new CommandSink(engine);
    }

    /// <summary>
    /// Create a peer that hosts the match.
    /// </summary>
    /// <param name="store">The simulation store. Must be empty; the world is seeded at start.</param>
    /// <param name="transport">Transport to listen on.</param>
    /// <param name="simConfig">Tick rate, input delay, rollback window. Must match every peer's.</param>
    /// <param name="sessionConfig">Player counts, countdown, timeouts.</param>
    /// <param name="callbacks">Game systems, world setup and input.</param>
    /// <param name="registerCommands">
    /// Registers the game's command types on the factory. Every peer must register the same type ids,
    /// or a received command deserializes as the wrong type.
    /// </param>
    /// <param name="roomName">Room name to advertise.</param>
    /// <param name="logger">Optional logger.</param>
    public static FrifloKlothoSession CreateHost(
        EntityStore                 store,
        INetworkTransport           transport,
        ISimulationConfig           simConfig,
        ISessionConfig              sessionConfig,
        IFrifloSimulationCallbacks  callbacks,
        Action<CommandFactory>      registerCommands = null,
        string                      roomName         = "room",
        IKLogger                    logger           = null)
    {
        if (store     == null) throw new ArgumentNullException(nameof(store));
        if (transport == null) throw new ArgumentNullException(nameof(transport));

        var session = new FrifloKlothoSession(store, simConfig, sessionConfig, callbacks, registerCommands, logger);
        session.networkService.Initialize(transport, session.commandFactory, logger);
        session.InitializeEngine();
        session.networkService.CreateRoom(roomName, sessionConfig.MaxPlayers);
        return session;
    }

    /// <summary>
    /// Create a peer that joins an existing match, using the result of a completed handshake.
    /// </summary>
    /// <param name="connection">
    /// A completed <c>KlothoConnection.Connect</c> result. It carries the transport and the host's
    /// authoritative <c>SimulationConfig</c>, which is used in place of any local one.
    /// </param>
    /// <param name="sessionConfig">
    /// Local defaults. The authoritative values arrive with the host's GameStart message.
    /// </param>
    public static FrifloKlothoSession CreateGuest(
        EntityStore                 store,
        ConnectionResult            connection,
        ISessionConfig              sessionConfig,
        IFrifloSimulationCallbacks  callbacks,
        Action<CommandFactory>      registerCommands = null,
        IKLogger                    logger           = null)
    {
        if (store      == null) throw new ArgumentNullException(nameof(store));
        if (connection == null) throw new ArgumentNullException(nameof(connection));

        // Host-authoritative: a guest that used its own config would tick at a different rate or
        // with a different input delay and desync immediately.
        var session = new FrifloKlothoSession(
            store, connection.SimulationConfig, sessionConfig, callbacks, registerCommands, logger);
        // InitializeFromConnection adopts the completed handshake and leaves the service in
        // SessionPhase.Synchronized. JoinRoom must NOT follow it: that call is the start of the
        // handshake this guest already finished, and it would push the phase back to Lobby — where
        // SetReady is refused and the match never starts.
        session.networkService.InitializeFromConnection(connection, session.commandFactory, logger);
        session.InitializeEngine();
        return session;
    }

    /// <summary>
    /// Wire the engine to the simulation and the network, and take over input via <c>OnPreTick</c>.
    /// </summary>
    private void InitializeEngine()
    {
        // Subscribe to the SERVICE's game-start event, before engine.Initialize attaches the engine's
        // own handler to it. That handler is what runs Start(), so going first puts the world seeding
        // where Klotho puts OnInitializeWorld: before the tick-0 snapshot, and before the first tick
        // executes. The engine's own OnGameStart is the wrong hook - it fires after Start() has
        // returned, by which point tick 0 has been snapshotted and the world would be missing from it.
        //
        // Ordering here happens not to change the outcome of a rollback to tick 0, because
        // ExecuteTick re-snapshots the current tick on entry and so overwrites tick 0's snapshot
        // with the seeded world before the first tick runs. That is a coincidence of the engine's
        // internals, not a guarantee to lean on: seeding still has to precede the first tick, or the
        // first executed tick would run against an empty world.
        networkService.OnGameStart += HandleGameStart;

        // Deliberately the overload without ISimulationCallbacks - see the note on the class.
        engine.Initialize(simulation, networkService, logger);
        engine.SetCommandFactory(commandFactory);
        engine.OnPreTick += HandlePreTick;
        engine.OnRollbackFailed += HandleRollbackFailed;
        networkService.SubscribeEngine(engine);
    }

    /// <summary>Note a rollback the engine could not serve. See <see cref="FailedRollbackCount"/>.</summary>
    private void HandleRollbackFailed(int targetTick, string reason) => FailedRollbackCount++;

    /// <summary>
    /// Seed the world once the roster and the shared random seed are settled.
    /// </summary>
    /// <remarks>
    /// Raised by the network service when the countdown ends, before the engine's own handler runs
    /// <c>Start()</c> and snapshots tick 0 — see the subscription order in
    /// <see cref="InitializeEngine"/>. Waiting until here rather than seeding at construction is
    /// what makes <c>RandomSeed</c> the value every peer agreed on, so all of them build the same
    /// tick-0 state. The guard turns a repeat (a corrective reset replaying start) into a no-op
    /// rather than a second copy of the world.
    /// </remarks>
    private void HandleGameStart()
    {
        if (worldSeeded) {
            return;
        }
        worldSeeded = true;
        callbacks.OnInitializeWorld(simulation.Store, networkService.RandomSeed);
    }

    /// <summary>Ask the game for this tick's input.</summary>
    private void HandlePreTick(int tick) => callbacks.OnPollInput(engine.LocalPlayerId, tick, sink);

    /// <summary>Hand a tick's commands to the game, in the deterministic order the engine fixed.</summary>
    private void ApplyCommands(EntityStore store, List<ICommand> commands, int tick)
    {
        if (callbacks is IFrifloCommandApplier applier) {
            applier.ApplyCommands(store, commands, tick);
        }
    }

    /// <summary>Signal that this peer is ready. The match starts once every player has.</summary>
    public void SetReady(bool ready = true) => networkService.SetReady(ready);

    /// <summary>
    /// Advance one frame: pump the transport and the engine.
    /// </summary>
    /// <param name="deltaTime">Elapsed wall-clock seconds since the previous call.</param>
    /// <remarks>
    /// The engine decides how many simulation ticks that buys; it is not one tick per call.
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
        engine.OnRollbackFailed    -= HandleRollbackFailed;
        networkService.OnGameStart -= HandleGameStart;
        engine.Stop();
        networkService.LeaveRoom(keepReconnectCredentials);
    }

    /// <inheritdoc />
    public void Dispose() => Stop();
}

/// <summary>
/// Applies a tick's commands to the store. Implemented alongside
/// <see cref="IFrifloSimulationCallbacks"/> by games whose commands mutate simulation state.
/// </summary>
/// <remarks>
/// Separate from the main callback interface because it is optional: a simulation driven purely by
/// its systems has no commands to apply. It runs before the systems, once per execution of a tick —
/// including every re-execution during a rollback, so it must depend only on the store and the
/// commands it is given.
/// </remarks>
public interface IFrifloCommandApplier
{
    /// <summary>Apply <paramref name="commands"/> to <paramref name="store"/> for <paramref name="tick"/>.</summary>
    void ApplyCommands(EntityStore store, List<ICommand> commands, int tick);
}
