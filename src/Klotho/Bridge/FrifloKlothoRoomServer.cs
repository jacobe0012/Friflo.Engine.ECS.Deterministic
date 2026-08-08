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
/// Hosts several independent Friflo-backed matches on one socket, routing each client to the room
/// it asked for.
/// </summary>
/// <remarks>
/// <b>What this reuses, and what it cannot.</b> Klotho's multi-room stack is four pieces:
/// <c>RoomRouter</c> (peer to room), <c>RoomScopedTransport</c> (one socket presented to a room as
/// if it were its own), <c>Room</c> (a match plus its inbound queue), and <c>RoomManager</c> /
/// <c>ServerLoop</c> (the room factory and the thread-pool update schedule). The first three are
/// typed against <see cref="ISimulation"/> and are used here unchanged. The last two are not
/// usable: <c>RoomManager.CreateRoomAt</c> constructs an <c>EcsSimulation</c> directly, with no
/// seam to substitute one, and <c>ServerLoop</c> takes the concrete <c>RoomManager</c>. So the room
/// factory and the update loop are the parts reimplemented here — the same shape, over a Friflo
/// store.
/// <para>
/// <c>Room</c> also demands an <c>ISimulationCallbacks</c>, whose <c>RegisterSystems</c> is typed
/// against <c>EcsSimulation</c>. It only ever stores that reference — nothing in Klotho reads
/// <c>Room.Callbacks</c> — so a stub satisfies the constructor without any of its members being
/// reachable. Handing it a real implementation is what is impossible; handing it one that is never
/// called is merely inelegant.
/// </para>
/// <para>
/// <b>Rooms are updated one after another, not on the thread pool.</b> <c>ServerLoop</c> dispatches
/// its rooms concurrently with a completion barrier, which is sound for it because each room owns a
/// separate <c>EcsSimulation</c>. That reasoning carries over — each room here owns a separate
/// <see cref="EntityStore"/>, and Friflo stores are independent — but it stops being true the
/// moment any game code reaches across rooms, and the determinism contract this bridge is built
/// around asks for exactly one thing in return for everything it guarantees: that a tick is
/// reproducible. Sequential updates cost throughput on a busy server and remove a whole class of
/// question from the answer. <see cref="UpdateRooms"/> is the seam if that trade is ever worth
/// revisiting.
/// </para>
/// </remarks>
public sealed class FrifloKlothoRoomServer : IDisposable
{
    private readonly INetworkTransport transport;
    private readonly RoomRouter        router;
    private readonly RoomSetup         setup;
    private readonly IKLogger          logger;

    /// <summary>Rooms by id. A slot is null until <see cref="CreateRoom"/> fills it.</summary>
    private readonly Room[] rooms;

    /// <summary>Each room's simulation, by room id, so callers can inspect a room's store.</summary>
    private readonly FrifloSimulation[] simulations;

    /// <summary>Reused across updates rather than allocated per frame.</summary>
    private readonly List<Room> readyRooms = new();

    private bool stopped;

    /// <summary>Largest number of rooms this server will host.</summary>
    public int MaxRooms => rooms.Length;

    /// <summary>Rooms currently hosting a match.</summary>
    /// <remarks>
    /// Counts rooms that still occupy a slot, which is what capacity is about — a room stays
    /// counted while it drains and stops being counted when its slot is free again.
    /// </remarks>
    public int ActiveRoomCount { get; private set; }

    /// <summary>Matches this server has hosted to completion, over its whole life.</summary>
    /// <remarks>
    /// Exists so slot reuse is observable. Without it, a server whose rooms end and reopen looks
    /// exactly like one that never ends a room at all.
    /// </remarks>
    public int CompletedRoomCount { get; private set; }

    /// <summary>
    /// How a room is built: its configuration, its systems, and its world.
    /// </summary>
    /// <remarks>
    /// A factory per room rather than shared instances: each room is a separate match with its own
    /// store, so sharing a systems object or a callbacks instance between them would let one
    /// room's state leak into another's.
    /// </remarks>
    public sealed class RoomSetup
    {
        /// <summary>Builds the simulation configuration for a room. Must be <see cref="NetworkMode.ServerDriven"/>.</summary>
        public Func<int, ISimulationConfig> SimulationConfig { get; init; }

        /// <summary>Builds the session configuration for a room.</summary>
        public Func<int, ISessionConfig> SessionConfig { get; init; }

        /// <summary>Builds the game callbacks for a room.</summary>
        public Func<int, IFrifloSimulationCallbacks> Callbacks { get; init; }

        /// <summary>Registers the game's command types. Every peer must register the same ids.</summary>
        public Action<CommandFactory> RegisterCommands { get; init; }
    }

    /// <summary>
    /// Stands in for the callbacks interface <c>Room</c> requires but never invokes.
    /// </summary>
    /// <remarks>
    /// <c>Room</c> stores its <c>ISimulationCallbacks</c> and nothing in Klotho reads it back, so
    /// none of these members is reachable. They throw rather than returning quietly: if a future
    /// version of Klotho does start calling one, the failure should name the reason rather than
    /// looking like a game that forgot to seed its world.
    /// </remarks>
    private sealed class UnusedCallbacks : ISimulationCallbacks
    {
        private static Exception NotReachable([System.Runtime.CompilerServices.CallerMemberName] string member = null) =>
            new NotSupportedException(
                $"{member} was called on the placeholder callbacks of a Friflo-backed room. Room stores " +
                "ISimulationCallbacks without ever invoking it, which is what lets a Friflo simulation " +
                "use Room at all - its RegisterSystems is typed against EcsSimulation and cannot be " +
                "implemented here. Something now calls it, so the room needs a different arrangement.");

        /// <inheritdoc />
        public void RegisterSystems(xpTURN.Klotho.ECS.EcsSimulation simulation) => throw NotReachable();
        /// <inheritdoc />
        public void OnInitializeWorld(IKlothoEngine engine) => throw NotReachable();
        /// <inheritdoc />
        public void OnPollInput(int playerId, int tick, ICommandSender sender) => throw NotReachable();
        /// <inheritdoc />
        public void OnPlayerJoinedWorld(IKlothoEngine engine, xpTURN.Klotho.ECS.Frame frame, int playerId) => throw NotReachable();
    }

    /// <summary>
    /// Start a server able to host <paramref name="maxRooms"/> matches.
    /// </summary>
    /// <param name="transport">The shared socket every room's clients connect to.</param>
    /// <param name="maxRooms">Room capacity.</param>
    /// <param name="setup">How each room is built.</param>
    /// <param name="logger">Optional logger.</param>
    /// <remarks>
    /// No room exists yet. A client addresses one by id in its room handshake, and the router
    /// resolves it — so call <see cref="CreateRoom"/> for each room before its clients connect.
    /// </remarks>
    public static FrifloKlothoRoomServer Create(
        INetworkTransport transport,
        int               maxRooms,
        RoomSetup         setup,
        IKLogger          logger = null)
    {
        if (transport == null) throw new ArgumentNullException(nameof(transport));
        if (setup     == null) throw new ArgumentNullException(nameof(setup));
        if (maxRooms  <= 0) {
            throw new ArgumentOutOfRangeException(nameof(maxRooms), maxRooms, "a server needs room for at least one match");
        }
        if (setup.SimulationConfig == null) throw new ArgumentException("SimulationConfig factory is required", nameof(setup));
        if (setup.SessionConfig    == null) throw new ArgumentException("SessionConfig factory is required",    nameof(setup));
        if (setup.Callbacks        == null) throw new ArgumentException("Callbacks factory is required",        nameof(setup));

        return new FrifloKlothoRoomServer(transport, maxRooms, setup, logger);
    }

    private FrifloKlothoRoomServer(INetworkTransport transport, int maxRooms, RoomSetup setup, IKLogger logger)
    {
        this.transport = transport;
        this.setup     = setup;
        this.logger    = logger;

        rooms       = new Room[maxRooms];
        simulations = new FrifloSimulation[maxRooms];
        router      = new RoomRouter(transport, logger);
        // Deliberately no SetRoomManager: that hook exists so the router can create a room on
        // demand for a client that asked for one that does not exist yet, and it takes the concrete
        // RoomManager. Rooms here are created up front, so a request for an unknown room is
        // refused rather than silently conjuring one.
    }

    /// <summary>
    /// Open a room and start its match lobby.
    /// </summary>
    /// <param name="roomId">Which slot to fill. Clients name this id in their room handshake.</param>
    /// <returns>The room's simulation, whose store the match will run in.</returns>
    public FrifloSimulation CreateRoom(int roomId)
    {
        if ((uint)roomId >= (uint)rooms.Length) {
            throw new ArgumentOutOfRangeException(nameof(roomId), roomId, $"this server hosts rooms 0..{rooms.Length - 1}");
        }
        if (rooms[roomId] != null) {
            throw new InvalidOperationException($"room {roomId} is already open");
        }

        var simConfig     = setup.SimulationConfig(roomId);
        var sessionConfig = setup.SessionConfig(roomId);
        var callbacks     = setup.Callbacks(roomId);
        if (simConfig.Mode != NetworkMode.ServerDriven) {
            throw new ArgumentException(
                $"room {roomId} was configured as {simConfig.Mode}; a room server is the authority for its " +
                $"matches, so every room must be {NetworkMode.ServerDriven}");
        }

        var store   = new EntityStore();
        var systems = new SystemRoot(store);
        callbacks.RegisterSystems(systems, store);

        var commandFactory = new CommandFactory();
        setup.RegisterCommands?.Invoke(commandFactory);

        var simulation = new FrifloSimulation(
            store,
            systems,
            applyCommands:    (s, commands, tick) => ApplyCommands(callbacks, s, commands, tick),
            rollbackCapacity: simConfig.GetSnapshotCapacity());

        // The room's own view of the shared socket: it addresses its peers directly and never sees
        // another room's.
        var roomTransport  = new RoomScopedTransport(transport);
        var networkService = new ServerNetworkService();
        networkService.Initialize(roomTransport, commandFactory, logger);

        // Subscribe BEFORE engine.Initialize, which attaches the engine's own handler to this same
        // event - and that handler is the one that runs Start(), snapshots tick 0 and broadcasts it
        // to the clients as the authoritative world. Registering afterwards seeds the world one
        // handler too late: the clients receive the empty world, and every one of them is then
        // resynced. That is exactly what happened here first.
        bool seeded = false;
        networkService.OnGameStart += () => {
            if (seeded) {
                return;
            }
            seeded = true;
            callbacks.OnInitializeWorld(store, networkService.RandomSeed);
        };

        var engine = new KlothoEngine(simConfig, sessionConfig);
        // Without ISimulationCallbacks, as everywhere else in this bridge: its RegisterSystems is
        // typed against EcsSimulation. That is also what makes the seeding above necessary - the
        // OnInitializeWorld the engine would otherwise call lives on that interface.
        engine.Initialize(simulation, networkService, logger);
        engine.SetCommandFactory(commandFactory);
        networkService.SubscribeEngine(engine);

        networkService.CreateRoom($"room-{roomId}", sessionConfig.MaxPlayers);
        // Without this the room accepts a client that named any id, since the service would not
        // know which room it is.
        networkService.SetRoomId(roomId);
        networkService.MaxSpectatorsPerRoom = sessionConfig.MaxSpectators;

        var room = new Room(
            roomId, simConfig, sessionConfig, simulation, commandFactory,
            roomTransport, networkService, engine, new UnusedCallbacks(), logger)
        {
            CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            State       = RoomState.Active,
        };

        rooms[roomId]       = room;
        simulations[roomId] = simulation;
        ActiveRoomCount++;
        router.RegisterRoom(room);
        return simulation;
    }

    /// <summary>The simulation of room <paramref name="roomId"/>, or null if that room is not open.</summary>
    public FrifloSimulation GetSimulation(int roomId) =>
        (uint)roomId < (uint)simulations.Length ? simulations[roomId] : null;

    /// <summary>The room with that id, or null if it is not open.</summary>
    public Room GetRoom(int roomId) =>
        (uint)roomId < (uint)rooms.Length ? rooms[roomId] : null;

    /// <summary>Hand a tick's commands to the room's game code.</summary>
    private static void ApplyCommands(
        IFrifloSimulationCallbacks callbacks, EntityStore store, List<ICommand> commands, int tick)
    {
        if (callbacks is IFrifloCommandApplier applier) {
            applier.ApplyCommands(store, commands, tick);
        }
    }

    /// <summary>
    /// Advance the server one frame: take delivery for every room, then update each.
    /// </summary>
    /// <param name="deltaTime">Elapsed wall-clock seconds since the previous call.</param>
    /// <remarks>
    /// The stages are separate for the reason <c>ServerLoop</c> separates them: the socket is
    /// shared, so one poll fans out to every room's inbound queue, and a room drains its own queue
    /// when it updates. Polling per room would have each room consuming packets addressed to the
    /// others.
    /// <para>
    /// Reclaiming finished rooms runs <i>before</i> the poll, matching <c>ServerLoop</c>'s stage 0.
    /// The ordering matters in one direction: a room released after this frame's packets were
    /// already fanned out would leave those packets queued on a room nobody updates again, so the
    /// releases happen while the queues are empty.
    /// </para>
    /// </remarks>
    public void Update(float deltaTime)
    {
        if (stopped) {
            return;
        }
        ReclaimFinishedRooms();
        transport.PollEvents();
        router.CleanupUnroutedPeers();
        UpdateRooms(deltaTime);
        transport.FlushSendQueue();
    }

    /// <summary>
    /// Move rooms whose match has ended through to release, freeing their slots.
    /// </summary>
    /// <remarks>
    /// <c>Room.Update</c> promotes a room to <see cref="RoomState.Draining"/> on its own once its
    /// peers are gone or its end grace expires, but nothing in Klotho then acts on that for a
    /// server built this way: the two stages that finish the job live on <c>RoomManager</c>
    /// (<c>TransitionDrainingRooms</c> and <c>CleanupDisposingRooms</c>), which this server
    /// replaces. Without them a drained room is simply never visited again — it is skipped by an
    /// update loop that only walks <see cref="RoomState.Active"/> rooms, while its slot, store,
    /// engine and network service stay held for the life of the process. Nothing fails; the
    /// server just quietly hosts fewer matches the longer it runs.
    /// <para>
    /// The two states stay distinct rather than collapsing into one step, because they are separate
    /// moments: <see cref="RoomState.Draining"/> stops the engine, and only the frame after does
    /// the room release its peers and its slot. Merging them would tear down the room inside the
    /// same frame its last packets are still being answered.
    /// </para>
    /// </remarks>
    private void ReclaimFinishedRooms()
    {
        for (int i = 0; i < rooms.Length; i++) {
            var room = rooms[i];
            if (room == null) {
                continue;
            }
            if (room.State == RoomState.Draining) {
                // Stop the engine first: it must not tick again once the room is being taken down.
                room.Engine.Stop();
                room.State = RoomState.Disposing;
                continue;
            }
            if (room.State == RoomState.Disposing) {
                ReleaseRoom(i, room);
            }
        }
    }

    /// <summary>Release a room's slot and everything the room holds.</summary>
    /// <remarks>
    /// The peer mappings are cleared through <c>RemoveRoomPeers</c> as well as the room
    /// registration. They are two different tables: unregistering the room stops packets being
    /// routed to it, while the peer entries are keyed by peer and would otherwise survive every
    /// later room release, since no room they name exists any more to clear them.
    /// </remarks>
    private void ReleaseRoom(int roomId, Room room)
    {
        router.UnregisterRoom(roomId);
        router.RemoveRoomPeers(roomId);
        room.Dispose();

        rooms[roomId]       = null;
        simulations[roomId] = null;
        ActiveRoomCount--;
        CompletedRoomCount++;
        logger?.KInformation($"[FrifloKlothoRoomServer] room {roomId} released (active={ActiveRoomCount})");
    }

    /// <summary>
    /// Update every active room.
    /// </summary>
    /// <remarks>
    /// Sequential, and the override point if that is ever to change — see the note on the class for
    /// why it is not the thread-pool dispatch <c>ServerLoop</c> uses.
    /// <para>
    /// Draining rooms are updated too, as <c>ServerLoop.GetReadyRooms</c> does. A draining room
    /// still has an inbound queue to consume and departures to answer, and skipping it would also
    /// leave the buffers in that queue unreturned.
    /// </para>
    /// </remarks>
    private void UpdateRooms(float deltaTime)
    {
        readyRooms.Clear();
        for (int i = 0; i < rooms.Length; i++) {
            var room = rooms[i];
            if (room != null && (room.State == RoomState.Active || room.State == RoomState.Draining)) {
                readyRooms.Add(room);
            }
        }
        for (int i = 0; i < readyRooms.Count; i++) {
            readyRooms[i].Update(deltaTime);
        }
    }

    /// <summary>Shut every room down. Idempotent.</summary>
    public void Stop()
    {
        if (stopped) {
            return;
        }
        stopped = true;
        for (int i = 0; i < rooms.Length; i++) {
            var room = rooms[i];
            if (room == null) {
                continue;
            }
            room.Engine.Stop();
            // Through the same release path as a room that ended on its own, so shutdown cannot
            // drift from the per-room teardown and forget one of the tables.
            ReleaseRoom(i, room);
        }
        ActiveRoomCount = 0;
        router.Dispose();
    }

    /// <inheritdoc />
    public void Dispose() => Stop();
}
