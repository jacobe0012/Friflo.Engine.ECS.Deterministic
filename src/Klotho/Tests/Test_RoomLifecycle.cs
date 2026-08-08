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
/// Checks that a finished match gives its room slot back, so a long-running server keeps its
/// capacity instead of losing it one match at a time.
/// </summary>
/// <remarks>
/// <see cref="Test_MultiRoom"/> proves two rooms stay separate games, but it never ends a match:
/// its rooms are created, played and then torn down wholesale with the server. That leaves the
/// entire second half of a room's life untested — and a server hosting matches back to back spends
/// most of its time there.
/// <para>
/// The distinction that matters is between a room that has <i>stopped being updated</i> and one
/// that has been <i>released</i>. <c>Room.Update</c> moves a room to <see cref="RoomState.Draining"/>
/// by itself once its peers are gone, and an update loop that only visits
/// <see cref="RoomState.Active"/> rooms will then quietly skip it forever. Nothing throws, no
/// packet is misrouted, and the room's <see cref="EntityStore"/>, engine and network service stay
/// reachable for as long as the process runs. Capacity decays silently, which is why these tests
/// assert on slot reuse rather than on any error surface.
/// </para>
/// </remarks>
public static class Test_RoomLifecycle
{
    /// <summary>Type id for <see cref="NudgeCommand"/>. Every peer must agree on it.</summary>
    private const int NudgeCommandTypeId = 107;

    /// <summary>Wall-clock ms per simulation tick. Short, to keep the test quick.</summary>
    private const int TickIntervalMs = 10;

    /// <summary>Players in each room.</summary>
    private const int PlayersPerRoom = 2;

    #region command

    /// <summary>Nudges the sending player's avatar.</summary>
    private sealed class NudgeCommand : CommandBase
    {
        public int dx;

        public override int  CommandTypeId     => NudgeCommandTypeId;
        public override bool IsContinuousInput => true;

        public override int GetSerializedSize() => base.GetSerializedSize() + 4;

        protected override void SerializeData(ref SpanWriter writer) => writer.WriteInt32(dx);
        protected override void DeserializeData(ref SpanReader reader) => dx = reader.ReadInt32();
    }

    #endregion

    #region simulation

    private struct Avatar : IComponent
    {
        public int  playerId;
        public FP64 x;
    }

    private sealed class DriftSystem : QuerySystem<Avatar>
    {
        protected override void OnUpdate() =>
            Query.ForEachEntity((ref Avatar avatar, Entity _) => avatar.x += FP64.FromInt(1));
    }

    private sealed class Callbacks : IFrifloSimulationCallbacks, IFrifloCommandApplier
    {
        /// <inheritdoc />
        public void RegisterSystems(SystemRoot systems, EntityStore store) => systems.Add(new DriftSystem());

        /// <inheritdoc />
        public void OnInitializeWorld(EntityStore store, int randomSeed)
        {
            for (int playerId = 1; playerId <= PlayersPerRoom; playerId++) {
                store.CreateEntity(new Avatar { playerId = playerId, x = FP64.FromInt(0) });
            }
        }

        /// <inheritdoc />
        public void OnPollInput(int playerId, int tick, IFrifloCommandSink sink) =>
            sink.Send(new NudgeCommand { dx = (tick + playerId) % 3 });

        /// <inheritdoc />
        public void ApplyCommands(EntityStore store, List<ICommand> commands, int tick) { }
    }

    #endregion

    #region tests

    /// <summary>
    /// A room whose players have all left is released, and its slot can host the next match.
    /// </summary>
    /// <remarks>
    /// The whole point of a room slot is that it comes back. Asserting on
    /// <see cref="FrifloKlothoRoomServer.ActiveRoomCount"/> alone would not be enough — a slot can
    /// be counted as free while the room object behind it is still held — so this also opens a new
    /// match in the same slot and plays it, which is what a server actually needs to do next.
    /// </remarks>
    [Test]
    public static void AnEmptiedRoomIsReleasedAndItsSlotReused()
    {
        using var harness = RoomHarness.Start(maxRooms: 1);

        harness.OpenRoom(0);
        harness.PlayUntilRunning(0);
        Assert.AreEqual(1, harness.Server.ActiveRoomCount, "the room did not open");

        // Every client leaves, which is what ends a match on a dedicated server: the room has
        // nobody left to simulate for.
        harness.DisconnectClients(0);

        // Draining is not instant - the room notices on its own next update, and the release is a
        // stage of the update loop after that.
        harness.PumpUntil(() => harness.Server.ActiveRoomCount == 0,
                          "the emptied room was never released - its slot is gone for the life of the server");

        Assert.IsNull(harness.Server.GetRoom(0), "the room slot still holds its Room after release");
        Assert.IsNull(harness.Server.GetSimulation(0), "the room slot still holds its simulation after release");

        // The real test of a released slot: it can host the next match.
        harness.OpenRoom(0);
        harness.PlayUntilRunning(0);
        Assert.AreEqual(1, harness.Server.ActiveRoomCount, "the slot could not host a second match");

        var store = harness.Server.GetSimulation(0).Store;
        Assert.AreEqual(PlayersPerRoom, store.Count,
            "the second match's world is not a fresh one - the slot kept state from the match before it");
    }

    /// <summary>
    /// Slots are reused across many matches, so a server's capacity does not decay with uptime.
    /// </summary>
    /// <remarks>
    /// One reuse can pass on a fluke - a single stale entry does not exhaust a table. Running
    /// several matches through the same slots is what makes a per-match leak show up as a trend,
    /// and asserts the invariant a long-running server depends on: capacity after N matches is the
    /// capacity it started with.
    /// </remarks>
    [Test]
    public static void CapacitySurvivesManyMatches()
    {
        const int Rooms   = 2;
        const int Matches = 4;

        using var harness = RoomHarness.Start(maxRooms: Rooms);

        for (int match = 0; match < Matches; match++) {
            for (int room = 0; room < Rooms; room++) {
                harness.OpenRoom(room);
            }
            for (int room = 0; room < Rooms; room++) {
                harness.PlayUntilRunning(room);
            }
            Assert.AreEqual(Rooms, harness.Server.ActiveRoomCount,
                $"match {match}: not every room opened - earlier matches did not give their slots back");

            for (int room = 0; room < Rooms; room++) {
                harness.DisconnectClients(room);
            }
            harness.PumpUntil(() => harness.Server.ActiveRoomCount == 0,
                              $"match {match}: the rooms were never released");
        }

        // Slot accounting alone would also be satisfied by a server that reuses slots while
        // holding on to every room it ever built, so check the rooms themselves are gone.
        for (int room = 0; room < Rooms; room++) {
            Assert.IsNull(harness.Server.GetRoom(room),
                $"room {room} is still held after its last match - the objects accumulate with uptime");
        }
        Assert.AreEqual(Rooms * Matches, harness.Server.CompletedRoomCount,
            "not every match was seen through to release");
    }

    /// <summary>
    /// A peer id reissued to a new connection is routed by that connection's own handshake.
    /// </summary>
    /// <remarks>
    /// Peer routing is keyed by peer id, and a real transport reuses ids: LiteNetLib gives a freed
    /// slot to the next peer to arrive. The router consults that table <i>before</i> treating a
    /// message as a first message, so a peer inheriting an id with a stale entry would have its
    /// room handshake swallowed and be delivered to a room it never named. Every other test here
    /// takes a fresh id per connection, which is the in-memory hub's default and which means none
    /// of them ever reaches this case.
    /// <para>
    /// <b>This does not guard <c>RemoveRoomPeers</c>,</b> and it was written believing it did.
    /// Re-injecting proved otherwise: with that call removed the test still passes, because
    /// <c>RoomRouter.OnPeerDisconnected</c> already removes a peer's entry as it goes, so a client
    /// that leaves normally — as these do — never leaves one behind. The residue
    /// <c>RemoveRoomPeers</c> exists for is the peer that is <i>still connected</i> when its room is
    /// released, which is the forced-shutdown path rather than the end of an ordinary match. That
    /// path is not covered here; see the plan's known-gaps list.
    /// </para>
    /// </remarks>
    [Test]
    public static void AReusedPeerIdIsRoutedByItsOwnHandshake()
    {
        using var harness = RoomHarness.Start(maxRooms: 1);

        harness.OpenRoom(0);
        harness.PlayUntilRunning(0);

        var retiredPeerIds = harness.PeerIds(0);
        harness.DisconnectClients(0);
        harness.PumpUntil(() => harness.Server.ActiveRoomCount == 0, "the first match's room was never released");

        // The next match's clients arrive on the same peer ids the last match's clients had.
        harness.OpenRoom(0, reusePeerIds: retiredPeerIds);
        harness.PlayUntilRunning(0);

        Assert.AreEqual(1, harness.Server.ActiveRoomCount,
            "the second match never started - its clients' handshakes went to the peer entries of the first");
    }

    /// <summary>
    /// A peer that connects and never completes the room handshake is eventually dropped.
    /// </summary>
    /// <remarks>
    /// A connection is not yet assigned to a room until its first message names one, so it is held
    /// aside until then. Nothing in the per-room lifecycle can reclaim such a peer — it belongs to
    /// no room — so without a sweep of its own, a peer that connects and then says nothing occupies
    /// that table permanently. That is reachable by anyone who can open a socket.
    /// <para>
    /// The router's own tables are private and Klotho is not modified here, so the assertion is on
    /// what the sweep does rather than on what it holds: an expired peer is disconnected, which the
    /// peer itself can see.
    /// </para>
    /// </remarks>
    [Test]
    public static void APeerThatNeverNamesARoomIsSweptUp()
    {
        using var harness = RoomHarness.Start(maxRooms: 1);

        harness.OpenRoom(0);

        // Connect without sending the room handshake that would assign it to a room.
        var silent = harness.ConnectSilentPeer();
        Assert.IsTrue(silent.IsConnected, "the silent peer never connected - the test would prove nothing");

        harness.PumpUntil(() => !silent.IsConnected,
                          "the silent peer was never swept - anyone who can open a socket can grow this table");
    }

    #endregion

    #region harness

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
        // A departed player's seat is held open for a reconnect, and the room does not count as
        // empty until that window closes - so at the 60s default no room could drain inside a
        // test. Shortened rather than worked around: the wait is real server behaviour, and a
        // test that bypassed it would not be exercising the path a real match takes.
        ReconnectTimeoutMs  = 50,
    };

    /// <summary>A room server with clients that can be brought up and taken down per room.</summary>
    private sealed class RoomHarness : IDisposable
    {
        internal FrifloKlothoRoomServer Server { get; private init; }

        private LoopbackHub hub;
        private readonly Dictionary<int, List<FrifloKlothoClient>>  clients = new();
        private readonly Dictionary<int, List<LoopbackTransport>>   links   = new();
        private readonly List<LoopbackTransport>                    allLinks = new();

        internal static RoomHarness Start(int maxRooms)
        {
            var hub        = new LoopbackHub();
            var serverLink = hub.CreateServer();

            var server = FrifloKlothoRoomServer.Create(
                serverLink, maxRooms,
                new FrifloKlothoRoomServer.RoomSetup {
                    SimulationConfig = _ => NewSimulationConfig(),
                    SessionConfig    = _ => NewSessionConfig(),
                    Callbacks        = _ => new Callbacks(),
                    RegisterCommands = RegisterCommands,
                });

            return new RoomHarness { Server = server, hub = hub };
        }

        /// <summary>The peer ids currently serving a room's seats.</summary>
        internal List<int> PeerIds(int roomId)
        {
            var ids = new List<int>();
            foreach (var link in links[roomId]) {
                ids.Add(link.LocalPeerId);
            }
            return ids;
        }

        /// <summary>Open a room and connect a full complement of clients to it.</summary>
        /// <param name="roomId">The slot to open.</param>
        /// <param name="reusePeerIds">
        /// Peer ids for the new clients to take over, as a real transport reissues them. Null gives
        /// each client a fresh id.
        /// </param>
        internal void OpenRoom(int roomId, IReadOnlyList<int> reusePeerIds = null)
        {
            Server.CreateRoom(roomId);

            var roomClients = new List<FrifloKlothoClient>();
            var roomLinks   = new List<LoopbackTransport>();
            for (int seat = 0; seat < PlayersPerRoom; seat++) {
                var link   = reusePeerIds != null
                    ? hub.ReuseClient(reusePeerIds[seat])
                    : hub.CreateClient();
                var client = FrifloKlothoClient.Connect(
                    new EntityStore(), link, NewSimulationConfig(), NewSessionConfig(),
                    new Callbacks(), RegisterCommands, roomId: roomId);
                hub.Connect(link);          // only now that the client's handlers are attached
                roomClients.Add(client);
                roomLinks.Add(link);
                allLinks.Add(link);
            }
            clients[roomId] = roomClients;
            links[roomId]   = roomLinks;
        }

        /// <summary>Connect a peer that never sends the room handshake, and return its link.</summary>
        internal LoopbackTransport ConnectSilentPeer()
        {
            var link = hub.CreateClient();
            hub.Connect(link);
            allLinks.Add(link);
            Pump();
            return link;
        }

        /// <summary>Run the room's match until every peer in it is playing.</summary>
        internal void PlayUntilRunning(int roomId)
        {
            PumpUntil(() => {
                foreach (var client in clients[roomId]) {
                    if (client.Phase != SessionPhase.Synchronized) return false;
                }
                return true;
            }, $"room {roomId}: clients did not finish the handshake");

            foreach (var client in clients[roomId]) {
                client.SetReady();
            }

            PumpUntil(() => {
                if (Server.GetRoom(roomId)?.Engine.State != KlothoState.Running) return false;
                if (Server.GetSimulation(roomId).Store.Count == 0) return false;
                foreach (var client in clients[roomId]) {
                    if (client.State != KlothoState.Running || !client.HasWorld) return false;
                }
                return true;
            }, $"room {roomId}: the match did not start");
        }

        /// <summary>Take every client of a room off the wire, as a finished match would.</summary>
        internal void DisconnectClients(int roomId)
        {
            foreach (var client in clients[roomId]) {
                client.Stop();
            }
            foreach (var link in links[roomId]) {
                link.Disconnect();
                allLinks.Remove(link);
            }
            clients.Remove(roomId);
            links.Remove(roomId);
        }

        internal void Pump()
        {
            Server.Update(TickIntervalMs / 1000f);
            for (int i = 0; i < allLinks.Count; i++) {
                allLinks[i].PollEvents();
            }
            foreach (var room in clients.Values) {
                foreach (var client in room) {
                    client.Update(TickIntervalMs / 1000f);
                }
            }
        }

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
            Assert.Fail($"{failureMessage} (activeRooms={Server.ActiveRoomCount})");
        }

        public void Dispose()
        {
            foreach (var room in clients.Values) {
                foreach (var client in room) {
                    client?.Stop();
                }
            }
            Server.Stop();
        }
    }

    #endregion
}
