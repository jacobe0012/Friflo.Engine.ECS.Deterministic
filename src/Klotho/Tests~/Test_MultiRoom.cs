using System;
using System.Collections.Generic;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using Klotho.Friflo;
using NUnit.Framework;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Serialization;

// ReSharper disable once CheckNamespace
namespace Klotho.Friflo.Tests;

/// <summary>
/// Runs several independent Friflo-backed matches on one socket and checks each stays its own game.
/// </summary>
/// <remarks>
/// Every other server test hosts exactly one match, so the routing layer between the socket and a
/// room has never run: which peer belongs to which room, which room a packet is delivered to, and
/// whether one room's state can reach another's. Those are the questions a second room introduces,
/// and none of them is visible with one.
/// <para>
/// The rooms are given deliberately different worlds. Two rooms running the same simulation would
/// agree by construction, so a routing bug that fed one room's packets to the other could leave
/// both of them looking correct.
/// </para>
/// </remarks>
public static class Test_MultiRoom
{
    /// <summary>Type id for <see cref="NudgeCommand"/>. Every peer must agree on it.</summary>
    private const int NudgeCommandTypeId = 106;

    /// <summary>Wall-clock ms per simulation tick. Short, to keep the test quick.</summary>
    private const int TickIntervalMs = 10;

    /// <summary>Rooms the server hosts.</summary>
    private const int RoomCount = 2;

    /// <summary>Players in each room.</summary>
    private const int PlayersPerRoom = 2;

    /// <summary>Frames the matches run once every client is in.</summary>
    private const int MatchFrames = 160;

    #region command

    /// <summary>Nudges the sending player's avatar.</summary>
    private sealed class NudgeCommand : CommandBase
    {
        public int dx;
        public int dy;

        public override int  CommandTypeId     => NudgeCommandTypeId;
        public override bool IsContinuousInput => true;

        public override int GetSerializedSize() => base.GetSerializedSize() + 8;

        protected override void SerializeData(ref SpanWriter writer)
        {
            writer.WriteInt32(dx);
            writer.WriteInt32(dy);
        }

        protected override void DeserializeData(ref SpanReader reader)
        {
            dx = reader.ReadInt32();
            dy = reader.ReadInt32();
        }
    }

    #endregion

    #region simulation

    /// <summary>An avatar owned by one player.</summary>
    private struct Avatar : IComponent
    {
        public int  playerId;
        public FP64 x, y;
    }

    /// <summary>Marks which room a world belongs to, so a mixed-up world is visible in its state.</summary>
    private struct RoomTag : IComponent
    {
        public int roomId;
    }

    /// <summary>Applies a constant drift, so state keeps changing even with no input.</summary>
    private sealed class DriftSystem : QuerySystem<Avatar>
    {
        protected override void OnUpdate()
        {
            Query.ForEachEntity((ref Avatar avatar, Entity _) => {
                avatar.x += FP64.FromInt(1);
            });
        }
    }

    /// <summary>
    /// Game side of one room's match.
    /// </summary>
    /// <remarks>
    /// Seeds a different number of avatars per room, and tags the world with its room id. Identical
    /// rooms would agree with each other by construction, which would make a routing bug that
    /// crossed the two invisible.
    /// </remarks>
    private sealed class Callbacks : IFrifloSimulationCallbacks, IFrifloCommandApplier
    {
        private readonly int roomId;

        internal Callbacks(int roomId) => this.roomId = roomId;

        /// <inheritdoc />
        public void RegisterSystems(SystemRoot systems, EntityStore store) => systems.Add(new DriftSystem());

        /// <inheritdoc />
        public void OnInitializeWorld(EntityStore store, int randomSeed)
        {
            store.CreateEntity(new RoomTag { roomId = roomId });
            for (int playerId = 1; playerId <= PlayersPerRoom; playerId++) {
                // Room 1's avatars start offset, so the two rooms' states differ from tick 0 even
                // where they hold the same entities.
                store.CreateEntity(new Avatar {
                    playerId = playerId,
                    x        = FP64.FromInt(roomId * 1000),
                    y        = FP64.FromInt(roomId * 7),
                });
            }
            // And a different entity count per room, so a world that arrived from the wrong room is
            // visible without reading a single component.
            for (int extra = 0; extra < roomId; extra++) {
                store.CreateEntity(new RoomTag { roomId = roomId });
            }
        }

        /// <inheritdoc />
        public void OnPollInput(int playerId, int tick, IFrifloCommandSink sink)
        {
            sink.Send(new NudgeCommand {
                dx = (tick * 7 + playerId * 13) % 5,
                dy = (tick * 3 + playerId * 11) % 4,
            });
        }

        /// <inheritdoc />
        public void ApplyCommands(EntityStore store, List<ICommand> commands, int tick)
        {
            for (int i = 0; i < commands.Count; i++) {
                if (commands[i] is not NudgeCommand nudge) {
                    continue;
                }
                int owner = nudge.PlayerId;
                store.Query<Avatar>().ForEachEntity((ref Avatar avatar, Entity _) => {
                    if (avatar.playerId == owner) {
                        avatar.x += FP64.FromInt(nudge.dx);
                        avatar.y += FP64.FromInt(nudge.dy);
                    }
                });
            }
        }
    }

    #endregion

    #region test

    /// <summary>
    /// Each room's clients agree with their own room's server state, and only with that.
    /// </summary>
    [Test]
    public static void RoomsRunIndependently_AndClientsAgreeWithTheirOwnRoom()
    {
        using var server = MultiRoomMatch.Start();

        // Each room's server-side hashes, and each client's, recorded per room.
        var roomHashes   = new Dictionary<int, long>[RoomCount];
        var clientHashes = new Dictionary<int, long>[RoomCount][];
        for (int room = 0; room < RoomCount; room++) {
            roomHashes[room]   = new Dictionary<int, long>();
            clientHashes[room] = new Dictionary<int, long>[PlayersPerRoom];
            for (int seat = 0; seat < PlayersPerRoom; seat++) {
                clientHashes[room][seat] = new Dictionary<int, long>();
                var record = clientHashes[room][seat];
                var client = server.Clients[room][seat];
                client.OnTickVerified += tick => {
                    if (!record.ContainsKey(tick)) {
                        record[tick] = client.Simulation.GetStateHash();
                    }
                };
            }
        }

        for (int frame = 0; frame < MatchFrames; frame++) {
            server.Pump();
            for (int room = 0; room < RoomCount; room++) {
                var simulation = server.Server.GetSimulation(room);
                int executed   = simulation.CurrentTick - 1;
                if (executed >= 0 && !roomHashes[room].ContainsKey(executed)) {
                    roomHashes[room][executed] = simulation.GetStateHash();
                }
            }
        }

        int comparisons = 0;
        for (int room = 0; room < RoomCount; room++) {
            for (int seat = 0; seat < PlayersPerRoom; seat++) {
                foreach (var (tick, clientHash) in clientHashes[room][seat]) {
                    if (!roomHashes[room].TryGetValue(tick, out long serverHash)) {
                        continue;
                    }
                    Assert.AreEqual(serverHash, clientHash,
                        $"room {room} client {seat} desynced from its room's server at verified tick {tick}");
                    comparisons++;
                }
                Assert.AreEqual(0, server.Clients[room][seat].ResyncCount,
                    $"room {room} client {seat} was rebuilt from server state");
            }
        }

        Assert.Greater(comparisons, 10 * RoomCount * PlayersPerRoom,
            $"only {comparisons} verified ticks were compared - too few to conclude the rooms are running");

        // The rooms must be genuinely separate games. Both advance, and neither holds the other's
        // world: an identical hash at a shared tick would mean the two rooms had converged, which
        // for deliberately different worlds can only be a routing or state-sharing bug.
        for (int room = 0; room < RoomCount; room++) {
            Assert.Greater(server.Server.GetSimulation(room).CurrentTick, 0, $"room {room} never advanced");
        }
        int shared = 0;
        foreach (var (tick, hashA) in roomHashes[0]) {
            if (roomHashes[1].TryGetValue(tick, out long hashB)) {
                Assert.AreNotEqual(hashA, hashB,
                    $"the two rooms hold identical state at tick {tick} - they were seeded differently, so they have been sharing");
                shared++;
            }
        }
        Assert.Greater(shared, 10, "the rooms never ran the same ticks - nothing was actually compared between them");
    }

    /// <summary>
    /// A client is given the world of the room it asked for.
    /// </summary>
    /// <remarks>
    /// Checked on the entity count and the room tag rather than on a hash, because it is the
    /// question a hash comparison cannot answer on its own: a client wired to the wrong room would
    /// agree perfectly with that room, and the per-room comparison above would not notice.
    /// </remarks>
    [Test]
    public static void EachClientLandsInTheRoomItAskedFor()
    {
        using var server = MultiRoomMatch.Start();

        for (int room = 0; room < RoomCount; room++) {
            var roomStore = server.Server.GetSimulation(room).Store;
            for (int seat = 0; seat < PlayersPerRoom; seat++) {
                var client = server.Clients[room][seat];
                Assert.IsTrue(client.HasWorld, $"room {room} client {seat} never received a world");
                Assert.AreEqual(roomStore.Count, client.Store.Count,
                    $"room {room} client {seat} holds a different number of entities than its room");

                // The tag is what names the room, and the rooms seed different counts of it.
                int tagged = 0;
                client.Store.Query<RoomTag>().ForEachEntity((ref RoomTag tag, Entity _) => {
                    Assert.AreEqual(room, tag.roomId,
                        $"room {room} client {seat} received a world belonging to room {tag.roomId}");
                    tagged++;
                });
                Assert.Greater(tagged, 0, $"room {room} client {seat} received a world with no room tag at all");
            }
        }
    }

    #endregion

    #region helpers

    /// <summary>Register the game's command types. Identical on every peer, by construction.</summary>
    private static void RegisterCommands(CommandFactory factory) =>
        factory.RegisterCommand<NudgeCommand>(NudgeCommandTypeId);

    private static SimulationConfig NewSimulationConfig() => new() {
        Mode              = NetworkMode.ServerDriven,
        TickIntervalMs    = TickIntervalMs,
        InputDelayTicks   = 2,
        MaxRollbackTicks  = 30,
        SyncCheckInterval = 10,
    };

    private static SessionConfig NewSessionConfig() => new() {
        MaxPlayers          = PlayersPerRoom,
        MinPlayers          = PlayersPerRoom,
        CountdownDurationMs = 1,
    };

    /// <summary>A multi-room server with every room's match running.</summary>
    private sealed class MultiRoomMatch : IDisposable
    {
        internal FrifloKlothoRoomServer  Server  { get; private init; }
        /// <summary>Clients by room, then by seat within that room.</summary>
        internal FrifloKlothoClient[][]  Clients { get; private init; }

        private LoopbackHub       hub;
        private LoopbackTransport serverLink;
        private readonly List<LoopbackTransport> links = new();

        internal static MultiRoomMatch Start()
        {
            var hub        = new LoopbackHub();
            var serverLink = hub.CreateServer();

            var server = FrifloKlothoRoomServer.Create(
                serverLink, RoomCount,
                new FrifloKlothoRoomServer.RoomSetup {
                    SimulationConfig = _      => NewSimulationConfig(),
                    SessionConfig    = _      => NewSessionConfig(),
                    Callbacks        = roomId => new Callbacks(roomId),
                    RegisterCommands = RegisterCommands,
                });

            // Up front: a client naming a room that does not exist yet is refused, since this
            // server does not create rooms on demand.
            for (int room = 0; room < RoomCount; room++) {
                server.CreateRoom(room);
            }

            var match = new MultiRoomMatch {
                Server = server, Clients = new FrifloKlothoClient[RoomCount][],
            };
            match.hub        = hub;
            match.serverLink = serverLink;

            for (int room = 0; room < RoomCount; room++) {
                match.Clients[room] = new FrifloKlothoClient[PlayersPerRoom];
                for (int seat = 0; seat < PlayersPerRoom; seat++) {
                    var link = hub.CreateClient();
                    match.Clients[room][seat] = FrifloKlothoClient.Connect(
                        new EntityStore(), link, NewSimulationConfig(), NewSessionConfig(),
                        new Callbacks(room), RegisterCommands, roomId: room);
                    // Only now that the client's handlers are attached.
                    hub.Connect(link);
                    match.links.Add(link);
                }
            }

            match.PumpUntil(
                () => {
                    foreach (var room in match.Clients) {
                        foreach (var client in room) {
                            if (client.Phase != SessionPhase.Synchronized) return false;
                        }
                    }
                    return true;
                },
                "clients did not finish the handshake");

            foreach (var room in match.Clients) {
                foreach (var client in room) {
                    client.SetReady();
                }
            }

            // Every room must actually be running its match, not merely have accepted its clients.
            // Waiting on the clients alone is not enough: a client sets HasWorld from its game-start
            // event, which can fire while its room is still in countdown with an empty store.
            match.PumpUntil(
                () => {
                    for (int room = 0; room < RoomCount; room++) {
                        if (match.Server.GetRoom(room).Engine.State != KlothoState.Running) return false;
                        if (match.Server.GetSimulation(room).Store.Count == 0) return false;
                    }
                    foreach (var room in match.Clients) {
                        foreach (var client in room) {
                            if (client.State != KlothoState.Running || !client.HasWorld) return false;
                        }
                    }
                    return true;
                },
                "the matches did not start");

            return match;
        }

        /// <summary>Advance the server and every client one frame.</summary>
        internal void Pump()
        {
            // The server polls the shared socket itself and fans each packet out to the room it
            // belongs to, so unlike the single-room tests there is no per-room link to pump.
            Server.Update(TickIntervalMs / 1000f);
            for (int i = 0; i < links.Count; i++) {
                links[i].PollEvents();
            }
            foreach (var room in Clients) {
                foreach (var client in room) {
                    client.Update(TickIntervalMs / 1000f);
                }
            }
        }

        /// <summary>
        /// Pump until <paramref name="condition"/> holds, advancing real time because the handshake
        /// and the countdown are wall-clock driven.
        /// </summary>
        internal void PumpUntil(Func<bool> condition, string failureMessage)
        {
            const int MaxFrames = 2000;
            for (int frame = 0; frame < MaxFrames; frame++) {
                if (condition()) {
                    return;
                }
                Pump();
                System.Threading.Thread.Sleep(1);
            }
            var states = new List<string>();
            for (int room = 0; room < Clients.Length; room++) {
                for (int seat = 0; seat < Clients[room].Length; seat++) {
                    var client = Clients[room][seat];
                    states.Add($"r{room}s{seat}: phase={client.Phase} state={client.State} hasWorld={client.HasWorld}");
                }
            }
            Assert.Fail($"{failureMessage} (rooms active={Server.ActiveRoomCount}, {string.Join("; ", states)})");
        }

        public void Dispose()
        {
            foreach (var room in Clients) {
                foreach (var client in room) {
                    client?.Stop();
                }
            }
            Server.Stop();
        }
    }

    #endregion
}
