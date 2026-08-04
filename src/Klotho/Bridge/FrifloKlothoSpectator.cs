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
/// Watches a server-driven match over a Friflo store without playing in it: it receives the world
/// and every confirmed tick, and replays them a little behind the live game.
/// </summary>
/// <remarks>
/// A spectator is not a client with input disabled. It holds no player slot, sends no commands, and
/// never predicts — it replays only what the server has already confirmed, which is why it trails
/// the match rather than running ahead of it. Nothing it does can affect the outcome, so a
/// spectator that fell behind or diverged would be invisible from the players' side.
/// <para>
/// <b>The engine cannot exist until the server replies.</b> A spectator is told the tick rate,
/// rollback window and session parameters by the server when it is accepted, and those must be the
/// ones it runs with — a locally guessed tick rate would replay the match at the wrong speed. So
/// construction here starts a connection and nothing else; the store stays empty and
/// <see cref="HasWorld"/> false until the server has answered and its full state has arrived. This
/// mirrors how Klotho's own <c>KlothoSession.CreateSpectator</c> defers its
/// <c>EcsSimulation</c>, and is why the systems come from a factory rather than being registered up
/// front.
/// </para>
/// </remarks>
public sealed class FrifloKlothoSpectator : IDisposable
{
    private readonly EntityStore                store;
    private readonly SpectatorService           spectatorService;
    private readonly CommandFactory             commandFactory;
    private readonly IFrifloSpectatorCallbacks  callbacks;
    private readonly IKLogger                   logger;

    private KlothoEngine     engine;
    private FrifloSimulation simulation;
    private bool             stopped;

    /// <summary>The engine replaying confirmed ticks, or null before the server has replied.</summary>
    public KlothoEngine Engine => engine;

    /// <summary>The simulation and its Friflo store, or null before the server has replied.</summary>
    public FrifloSimulation Simulation => simulation;

    /// <summary>The store being replayed into. Empty until <see cref="HasWorld"/> turns true.</summary>
    public EntityStore Store => store;

    /// <summary>Connection progress: connecting, watching, or stopped.</summary>
    public SpectatorState State => spectatorService.State;

    /// <summary>Players in the match being watched.</summary>
    public IReadOnlyList<IPlayerInfo> Players => spectatorService.Players;

    /// <summary>The most recent tick the server has confirmed to this spectator, or -1.</summary>
    public int LatestReceivedTick => spectatorService.LatestReceivedTick;

    /// <summary>
    /// True once the server's world has arrived and the engine is replaying it.
    /// </summary>
    /// <remarks>
    /// Before this the store is empty: a spectator has no way to construct the world for itself,
    /// having never run world initialization. Reading the store or its hash earlier compares an
    /// empty world against the match's.
    /// </remarks>
    public bool HasWorld { get; private set; }

    /// <summary>How many full states this spectator has been sent.</summary>
    /// <remarks>
    /// At least one: the world arrives that way. More than one means it fell far enough behind, or
    /// diverged far enough, that the server rebuilt it rather than letting it catch up from
    /// confirmed input — the spectator equivalent of a resync, and just as much a sign that
    /// something is wrong.
    /// </remarks>
    public int FullStateCount { get; private set; }

    /// <summary>
    /// Raised as a confirmed tick finishes replaying, while the store still holds that tick's state.
    /// </summary>
    /// <remarks>
    /// The tick argument is the one just executed. Sampling here rather than between frames matters
    /// for the same reason it does on a client: a single <see cref="Update"/> may replay several
    /// ticks, so afterwards the store is on the last of them and the ones in between are gone.
    /// </remarks>
    public event Action<int> OnTickReplayed;

    /// <summary>
    /// Start watching a match.
    /// </summary>
    /// <param name="store">The store to replay into. Must be empty; the world arrives from the server.</param>
    /// <param name="transport">Transport to reach the server on.</param>
    /// <param name="callbacks">
    /// Builds the systems once the server's configuration has arrived. Called at most once.
    /// </param>
    /// <param name="registerCommands">
    /// Registers the game's command types. Must match the players' registrations, or a confirmed
    /// command deserializes as the wrong type and the replay diverges.
    /// </param>
    /// <param name="address">Server address.</param>
    /// <param name="port">Server port.</param>
    /// <param name="roomId">Room to watch, or -1 for a single-room server.</param>
    /// <param name="logger">Optional logger.</param>
    public static FrifloKlothoSpectator Watch(
        EntityStore               store,
        INetworkTransport         transport,
        IFrifloSpectatorCallbacks callbacks,
        Action<CommandFactory>    registerCommands = null,
        string                    address          = "loopback",
        int                       port             = 0,
        int                       roomId           = -1,
        IKLogger                  logger           = null)
    {
        if (store     == null) throw new ArgumentNullException(nameof(store));
        if (transport == null) throw new ArgumentNullException(nameof(transport));

        var spectator = new FrifloKlothoSpectator(store, callbacks, registerCommands, logger);
        spectator.spectatorService.Initialize(transport, spectator.commandFactory, null, logger);
        spectator.spectatorService.OnSimulationConfigReceived += spectator.HandleSimulationConfig;
        spectator.spectatorService.OnSessionConfigReceived    += spectator.HandleSessionConfig;
        spectator.spectatorService.Connect(address, port, roomId);
        return spectator;
    }

    private FrifloKlothoSpectator(
        EntityStore               store,
        IFrifloSpectatorCallbacks callbacks,
        Action<CommandFactory>    registerCommands,
        IKLogger                  logger)
    {
        this.store     = store;
        this.callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
        this.logger    = logger;

        commandFactory = new CommandFactory();
        registerCommands?.Invoke(commandFactory);

        spectatorService = new SpectatorService();
        spectatorService.SetLogger(logger);
    }

    /// <summary>The server's simulation configuration, held until the session configuration also arrives.</summary>
    private ISimulationConfig pendingSimConfig;
    /// <summary>The server's session configuration, held until the simulation configuration also arrives.</summary>
    private ISessionConfig    pendingSessionConfig;

    private void HandleSimulationConfig(ISimulationConfig config)
    {
        pendingSimConfig = config;
        TryBuildEngine();
    }

    private void HandleSessionConfig(ISessionConfig config)
    {
        pendingSessionConfig = config;
        TryBuildEngine();
    }

    /// <summary>
    /// Build the engine once both halves of the server's configuration have arrived.
    /// </summary>
    /// <remarks>
    /// The two configurations arrive in one accept message but are raised as separate events, in an
    /// order this code should not depend on, so whichever lands second does the building. Guarded
    /// against running twice.
    /// </remarks>
    private void TryBuildEngine()
    {
        if (pendingSimConfig == null || pendingSessionConfig == null || engine != null) {
            return;
        }
        var systems = new SystemRoot(store);
        callbacks.RegisterSystems(systems, store);

        simulation = new FrifloSimulation(
            store,
            systems,
            applyCommands:    ApplyCommands,
            rollbackCapacity: pendingSimConfig.GetSnapshotCapacity());

        engine = new KlothoEngine(pendingSimConfig, pendingSessionConfig);
        // The overload without a network service: a spectator has none. It neither sends input nor
        // participates in the protocol - the spectator service drives it directly, below.
        engine.Initialize(simulation, logger);
        engine.SetCommandFactory(commandFactory);
        engine.OnTickExecuted += HandleTickExecuted;

        spectatorService.SetEngine(engine);
        spectatorService.OnSpectatorStarted       += HandleSpectatorStarted;
        spectatorService.OnConfirmedInputReceived += HandleConfirmedInput;
        spectatorService.OnTickConfirmed          += HandleTickConfirmed;
        spectatorService.OnFullStateReceived      += HandleFullState;
    }

    private void HandleSpectatorStarted(SpectatorStartInfo info) => engine.StartSpectator(info);

    /// <summary>Buffer a command the server has confirmed, so the tick that uses it can replay.</summary>
    private void HandleConfirmedInput(int tick, ICommand command) => engine.ReceiveConfirmedCommand(command);

    /// <summary>Note that a tick's input is complete and it may be replayed.</summary>
    private void HandleTickConfirmed(int tick) => engine.ConfirmSpectatorTick(tick);

    /// <summary>
    /// Take the server's world wholesale.
    /// </summary>
    /// <remarks>
    /// How a spectator gets its world in the first place, and how it is repaired if it ever falls
    /// too far behind to catch up from confirmed input alone.
    /// </remarks>
    private void HandleFullState(int tick, byte[] stateData, long stateHash, FullStateKind kind)
    {
        simulation.RestoreFromFullState(stateData);
        engine.ResetToTick(tick);
        HasWorld = true;
        FullStateCount++;
    }

    /// <summary>Forward the engine's executed-tick signal.</summary>
    private void HandleTickExecuted(int tick) => OnTickReplayed?.Invoke(tick);

    /// <summary>Hand a tick's commands to the game, in the deterministic order the server fixed.</summary>
    private void ApplyCommands(EntityStore store, List<ICommand> commands, int tick)
    {
        if (callbacks is IFrifloCommandApplier applier) {
            applier.ApplyCommands(store, commands, tick);
        }
    }

    /// <summary>
    /// Advance one frame: pump the transport and replay whatever the server has confirmed.
    /// </summary>
    /// <param name="deltaTime">Elapsed wall-clock seconds since the previous call.</param>
    /// <remarks>
    /// Before the server has replied this only pumps the transport — there is no engine yet.
    /// </remarks>
    public void Update(float deltaTime)
    {
        if (stopped) {
            return;
        }
        spectatorService.Update();
        engine?.Update(deltaTime);
    }

    /// <summary>Stop watching and tear the engine down. Idempotent.</summary>
    public void Stop()
    {
        if (stopped) {
            return;
        }
        stopped = true;
        spectatorService.OnSimulationConfigReceived -= HandleSimulationConfig;
        spectatorService.OnSessionConfigReceived    -= HandleSessionConfig;
        if (engine != null) {
            spectatorService.OnSpectatorStarted       -= HandleSpectatorStarted;
            spectatorService.OnConfirmedInputReceived -= HandleConfirmedInput;
            spectatorService.OnTickConfirmed          -= HandleTickConfirmed;
            spectatorService.OnFullStateReceived      -= HandleFullState;
            engine.OnTickExecuted                     -= HandleTickExecuted;
            engine.Stop();
        }
        spectatorService.Disconnect();
    }

    /// <inheritdoc />
    public void Dispose() => Stop();
}

/// <summary>
/// The game side of a spectator: the systems that replay the match.
/// </summary>
/// <remarks>
/// Deliberately smaller than <see cref="IFrifloSimulationCallbacks"/>. A spectator never seeds a
/// world — it receives one — and never produces input, so those members would have no
/// implementation to give. Systems must match the players' exactly: a spectator replays their
/// confirmed input, so a difference here shows up as a spectator that quietly disagrees with the
/// match it is displaying.
/// <para>
/// Implement <see cref="IFrifloCommandApplier"/> as well to receive each tick's commands.
/// </para>
/// </remarks>
public interface IFrifloSpectatorCallbacks
{
    /// <summary>
    /// Register the systems, in the order they run each tick.
    /// </summary>
    /// <param name="systems">The system root executed once per replayed tick.</param>
    /// <param name="store">The store the systems will read and write.</param>
    /// <remarks>
    /// Called once the server's configuration has arrived, not at construction — see the note on
    /// <see cref="FrifloKlothoSpectator"/>.
    /// </remarks>
    void RegisterSystems(SystemRoot systems, EntityStore store);
}
