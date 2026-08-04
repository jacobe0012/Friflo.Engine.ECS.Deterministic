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
/// Joins a client to a server-driven match that is already running, and checks it ends up holding
/// the same world as everyone else.
/// </summary>
/// <remarks>
/// Late join is the flow that made the full-state defects worth fixing rather than merely worth
/// noting: a joiner's world arrives entirely as a full state, and unlike the initial bootstrap it
/// arrives at a tick well past zero, into a match that keeps advancing while the joiner catches up.
/// A transfer that dropped scripts or corrupted the child pool would leave it permanently short of
/// the world, and every existing test would still pass — they seed their clients at tick 0, where
/// far less of the state exists.
/// <para>
/// The flows here are separate from the round of work that fixed the transfer. Fixing it was a
/// precondition for them, not a demonstration that they work.
/// </para>
/// </remarks>
public static class Test_LateJoin
{
    /// <summary>Type id for <see cref="NudgeCommand"/>. Every peer must agree on it.</summary>
    private const int NudgeCommandTypeId = 103;

    /// <summary>Wall-clock ms per simulation tick. Short, to keep the test quick.</summary>
    private const int TickIntervalMs = 10;

    /// <summary>Players the match starts with.</summary>
    private const int StartingPlayers = 2;

    /// <summary>Room capacity: the starting players plus the ones that arrive later.</summary>
    private const int MaxPlayers = 4;

    /// <summary>Frames the match runs before anyone joins late, so the joiner arrives at a non-trivial tick.</summary>
    private const int FramesBeforeJoin = 60;

    /// <summary>Frames the match runs after the join, so the joiner catches up and then keeps pace.</summary>
    private const int FramesAfterJoin = 160;

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

    /// <summary>An avatar's equipment, parented to it. Exists to put a hierarchy in the world.</summary>
    private struct Held : IComponent
    {
        public int slot;
    }

    /// <summary>
    /// State attached to an avatar as a <see cref="Script"/> rather than a component.
    /// </summary>
    /// <remarks>
    /// Scripts live outside the archetypes and travel in their own section of a full state, as does
    /// the hierarchy. A joiner receives all of it at once, mid-match, which is the case that would
    /// expose a section that was never written.
    /// </remarks>
    private sealed class Loadout : Script
    {
        public int    charges;
        public Entity held;
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

    /// <summary>Game side of the simulation. Only the server's world seeding ever runs.</summary>
    private sealed class Callbacks : IFrifloSimulationCallbacks, IFrifloCommandApplier
    {
        /// <inheritdoc />
        public void RegisterSystems(SystemRoot systems, EntityStore store) => systems.Add(new DriftSystem());

        /// <inheritdoc />
        public void OnInitializeWorld(EntityStore store, int randomSeed)
        {
            // Server only. Each avatar carries a component, child entities and a script, so the
            // world a joiner must receive spans all three of the ways state is stored.
            //
            // TWO children, not one, and that is load-bearing: an IdArray of count 1 keeps the id
            // inline in its `start` field and never touches the pooled allocator, so a single-child
            // world round-trips correctly even if the child pool is never rebuilt. Removing the
            // rebuild and re-running this test is how that was found - it passed. At two children
            // `start` becomes a pool offset, which is the case that breaks.
            for (int playerId = 1; playerId <= StartingPlayers; playerId++) {
                var avatar = store.CreateEntity(new Avatar { playerId = playerId, x = FP64.Zero, y = FP64.Zero });
                var held   = store.CreateEntity(new Held { slot = playerId });
                var spare  = store.CreateEntity(new Held { slot = playerId + 100 });
                avatar.AddChild(held);
                avatar.AddChild(spare);
                avatar.AddScript(new Loadout { charges = playerId * 3, held = held });
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
                    continue;   // EmptyCommand, or a framework system command
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
    /// A client joining a running match receives the world as it stands and converges on it.
    /// </summary>
    /// <remarks>
    /// The joiner's state is checked against the server's rather than against a hash at a shared
    /// tick: it arrives at whatever tick the server had reached and then catches up, so there is no
    /// moment before that at which the two are comparable. Once it is caught up, verified ticks are
    /// compared as everywhere else.
    /// </remarks>
    [Test]
    public static void ClientJoiningMidMatch_ReceivesTheWorldAndConverges()
    {
        using var match = Match.Start();

        for (int frame = 0; frame < FramesBeforeJoin; frame++) {
            match.Pump();
        }
        int tickAtJoin = match.Server.Simulation.CurrentTick;
        Assert.Greater(tickAtJoin, 0, "the match never advanced before the join - the joiner would arrive at tick 0");

        var joiner = match.JoinLate();

        // The world arrives with the handshake, so it is already here; what remains is replaying
        // the ticks that ran while it was joining.
        Assert.IsTrue(joiner.HasWorld, "the late joiner did not receive a world from its handshake");
        match.PumpUntil(() => joiner.IsCaughtUp, "the late joiner never caught up with the match");

        // Its world came from the server mid-match, so it must hold the same entities - including
        // the parts that cannot travel as raw component bytes.
        AssertWorldMatches(match.Server.Store, joiner.Store, "late joiner");

        // Now that it is in, it must stay in agreement like any other client.
        var serverHashes = new Dictionary<int, long>();
        var joinerHashes = new Dictionary<int, long>();
        joiner.OnTickVerified += tick => {
            if (!joinerHashes.ContainsKey(tick)) {
                joinerHashes[tick] = joiner.Simulation.GetStateHash();
            }
        };

        for (int frame = 0; frame < FramesAfterJoin; frame++) {
            match.Pump();
            int executed = match.Server.Simulation.CurrentTick - 1;
            if (executed >= 0 && !serverHashes.ContainsKey(executed)) {
                serverHashes[executed] = match.Server.Simulation.GetStateHash();
            }
        }

        int comparisons = 0;
        foreach (var (tick, joinerHash) in joinerHashes) {
            if (!serverHashes.TryGetValue(tick, out long serverHash)) {
                continue;
            }
            Assert.AreEqual(serverHash, joinerHash, $"the late joiner desynced from the server at verified tick {tick}");
            comparisons++;
        }

        Assert.Greater(comparisons, 10,
            $"only {comparisons} verified ticks were compared - too few to conclude the joiner keeps pace");
        // A joiner that was quietly rebuilt from server state whenever it drifted would compare
        // equal at every tick above while never actually simulating correctly.
        Assert.AreEqual(0, joiner.ResyncCount, "the late joiner desynced and was rebuilt from server state");
        // The players that were already in must not have been disturbed by the arrival.
        foreach (var client in match.StartingClients) {
            Assert.AreEqual(0, client.ResyncCount, "an existing client was resynced when someone joined late");
        }
    }

    /// <summary>
    /// Two clients joining a running match at different ticks both converge.
    /// </summary>
    /// <remarks>
    /// A second joiner arrives when the roster and the world are both larger than the first one
    /// found them, and while the first is still catching up. It also takes the match past the two
    /// players every other test uses, so a per-player assumption that happened to hold for a pair
    /// has a chance to fail.
    /// </remarks>
    [Test]
    public static void TwoClientsJoiningAtDifferentTicks_BothConverge()
    {
        using var match = Match.Start();

        for (int frame = 0; frame < FramesBeforeJoin; frame++) {
            match.Pump();
        }
        var first = match.JoinLate();

        // Deliberately only a few frames later: the second joins while the first is still catching
        // up, so the server is servicing two joiners in different states at once.
        for (int frame = 0; frame < 10; frame++) {
            match.Pump();
        }
        var second = match.JoinLate();

        match.PumpUntil(
            () => first.IsCaughtUp && second.IsCaughtUp,
            "the late joiners never caught up with the match");

        for (int frame = 0; frame < FramesAfterJoin; frame++) {
            match.Pump();
        }

        Assert.AreEqual(StartingPlayers + 2, match.Server.PlayerCount, "the server's roster does not hold both joiners");
        AssertWorldMatches(match.Server.Store, first.Store,  "first late joiner");
        AssertWorldMatches(match.Server.Store, second.Store, "second late joiner");
        Assert.AreEqual(0, first.ResyncCount,  "the first late joiner was rebuilt from server state");
        Assert.AreEqual(0, second.ResyncCount, "the second late joiner was rebuilt from server state");
        foreach (var client in match.StartingClients) {
            Assert.AreEqual(0, client.ResyncCount, "an existing client was resynced when someone joined late");
        }
    }

    #endregion

    #region helpers

    /// <summary>
    /// Assert <paramref name="actual"/> holds the same world as <paramref name="expected"/>.
    /// </summary>
    /// <remarks>
    /// Compared entity by entity rather than by whole-store hash: the two are never on a common
    /// tick — a client predicts ahead of the server and re-simulates the moment a verified tick
    /// lands — so a hash comparison outside the verified-tick event would fail on healthy peers.
    /// What is checked is the structure a full state has to carry, which does not change between
    /// ticks: children, scripts, and the entity references inside them.
    /// </remarks>
    private static void AssertWorldMatches(EntityStore expected, EntityStore actual, string who)
    {
        Assert.AreEqual(expected.Count, actual.Count, $"{who}: entity count differs from the server's");

        for (int playerId = 1; playerId <= StartingPlayers; playerId++) {
            var onServer = FindAvatar(expected, playerId);
            var onClient = FindAvatar(actual,   playerId);
            Assert.IsFalse(onClient.IsNull, $"{who}: player {playerId}'s avatar never arrived");

            // Reading the children is what fails when the child pool was not rebuilt: with more
            // than one child their ids live in the pool, so a stale handle either reads back the
            // wrong ids or throws.
            Assert.AreEqual(onServer.ChildCount, onClient.ChildCount, $"{who}: the avatar's children did not arrive");
            for (int i = 0; i < onServer.ChildCount; i++) {
                Assert.AreEqual(onServer.ChildEntities[i].Id, onClient.ChildEntities[i].Id,
                    $"{who}: child {i} of player {playerId}'s avatar differs from the server's");
            }

            var serverLoadout = onServer.GetScript<Loadout>();
            var clientLoadout = onClient.GetScript<Loadout>();
            Assert.IsNotNull(clientLoadout, $"{who}: the avatar's script never arrived");
            Assert.AreEqual(serverLoadout.charges, clientLoadout.charges, $"{who}: the script's field value was lost");
            // An Entity field carries only (Id, Revision) and is re-bound on arrival, so it must
            // point into the receiving store rather than at the server's.
            Assert.AreEqual(serverLoadout.held.Id, clientLoadout.held.Id, $"{who}: the script's entity reference was lost");
            Assert.AreSame(actual, clientLoadout.held.Store, $"{who}: the script still points at the server's store");
        }
    }

    /// <summary>The avatar belonging to <paramref name="playerId"/>, or a null entity if absent.</summary>
    private static Entity FindAvatar(EntityStore store, int playerId)
    {
        var found = default(Entity);
        store.Query<Avatar>().ForEachEntity((ref Avatar avatar, Entity entity) => {
            if (avatar.playerId == playerId) {
                found = entity;
            }
        });
        return found;
    }

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

    /// <summary>
    /// Session config for the match.
    /// </summary>
    /// <remarks>
    /// <c>MaxPlayers</c> exceeds <c>MinPlayers</c> so there is room to join into; if they were
    /// equal the server would reject the joiner as room-full and the test would pass by never
    /// exercising the path.
    /// </remarks>
    private static SessionConfig NewSessionConfig() => new() {
        MaxPlayers          = MaxPlayers,
        MinPlayers          = StartingPlayers,
        AllowLateJoin       = true,
        CountdownDurationMs = 1,
    };

    /// <summary>A running match that clients can be added to after it has started.</summary>
    private sealed class Match : IDisposable
    {
        internal FrifloKlothoServer         Server          { get; private init; }
        /// <summary>The clients the match started with.</summary>
        internal FrifloKlothoClient[]       StartingClients { get; private init; }

        private LoopbackHub                 hub;
        private LoopbackTransport           serverLink;
        private readonly List<FrifloKlothoClient> clients = new();
        private readonly List<LoopbackTransport>  links   = new();

        internal static Match Start()
        {
            var hub        = new LoopbackHub();
            var serverLink = hub.CreateServer();

            var server = FrifloKlothoServer.Create(
                new EntityStore(), serverLink, NewSimulationConfig(), NewSessionConfig(),
                new Callbacks(), RegisterCommands);

            var match = new Match {
                Server = server, StartingClients = new FrifloKlothoClient[StartingPlayers],
            };
            match.hub        = hub;
            match.serverLink = serverLink;

            for (int i = 0; i < StartingPlayers; i++) {
                match.StartingClients[i] = match.AddClient();
            }

            match.PumpUntil(
                () => {
                    foreach (var client in match.StartingClients) {
                        if (client.Phase != SessionPhase.Synchronized) return false;
                    }
                    return true;
                },
                "clients did not finish the handshake");

            foreach (var client in match.StartingClients) {
                client.SetReady();
            }

            match.PumpUntil(
                () => {
                    if (match.Server.State != KlothoState.Running) return false;
                    foreach (var client in match.StartingClients) {
                        if (client.State != KlothoState.Running || !client.HasWorld) return false;
                    }
                    return true;
                },
                "the match did not start");

            return match;
        }

        /// <summary>Connect one more client into a match that has not started yet.</summary>
        internal FrifloKlothoClient AddClient()
        {
            var link = hub.CreateClient();
            var client = FrifloKlothoClient.Connect(
                new EntityStore(), link, NewSimulationConfig(), NewSessionConfig(),
                new Callbacks(), RegisterCommands);
            // Only now that the client's handlers are attached.
            hub.Connect(link);
            links.Add(link);
            clients.Add(client);
            return client;
        }

        /// <summary>
        /// Join one more client into the running match, running the handshake to completion first.
        /// </summary>
        /// <remarks>
        /// The two-phase shape is forced by the protocol, not chosen: the server's reply carries
        /// the roster, the session seed and the world as of the current tick, and the engine has to
        /// be built against those rather than started empty and corrected. So the handshake runs on
        /// a bare transport with no session behind it, and only its result produces a client.
        /// <para>
        /// The server routes this peer to the late-join handshake on its own, because it connected
        /// after the match started — the client sends the same <c>PlayerJoinMessage</c> either way.
        /// </para>
        /// </remarks>
        internal FrifloKlothoClient JoinLate()
        {
            var link = hub.CreateClient();

            ConnectionResult result = null;
            Exception        failure = null;
            var connection = KlothoConnection.Connect(
                link, "loopback", 0,
                onCompleted: r => result  = r,
                onFailed:    e => failure = e);
            // The handshake's own events have to reach it, so link the transport only once it is
            // subscribed - Connect above attaches the handlers.
            hub.Connect(link);
            links.Add(link);

            // Pump the whole match while the handshake runs: it is a request/response sequence with
            // the server, and the server only answers from inside its own update.
            const int MaxHandshakeFrames = 2000;
            for (int frame = 0; frame < MaxHandshakeFrames && !connection.IsCompleted; frame++) {
                connection.Update();
                Pump();
                System.Threading.Thread.Sleep(1);
            }
            if (failure != null) {
                Assert.Fail($"the late-join handshake failed: {failure.Message}");
            }
            Assert.IsNotNull(result, "the late-join handshake never completed");
            Assert.AreEqual(JoinKind.LateJoin, result.Kind,
                "the server treated this as a normal join - the match was not running yet, so nothing late-join was tested");

            var client = FrifloKlothoClient.JoinRunningMatch(
                result, new EntityStore(), NewSimulationConfig(), NewSessionConfig(),
                new Callbacks(), RegisterCommands);
            clients.Add(client);
            return client;
        }

        /// <summary>Advance every peer one frame, delivering whatever each has queued for the others.</summary>
        internal void Pump()
        {
            serverLink.PollEvents();
            for (int i = 0; i < links.Count; i++) {
                links[i].PollEvents();
            }
            Server.Update(TickIntervalMs / 1000f);
            for (int i = 0; i < clients.Count; i++) {
                clients[i].Update(TickIntervalMs / 1000f);
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
            foreach (var client in clients) {
                states.Add($"phase={client.Phase} state={client.State} hasWorld={client.HasWorld}");
            }
            Assert.Fail($"{failureMessage} (server: phase={Server.Phase} state={Server.State} " +
                        $"players={Server.PlayerCount} tick={Server.Simulation.CurrentTick}, " +
                        $"clients: {string.Join("; ", states)})");
        }

        public void Dispose()
        {
            foreach (var client in clients) {
                client.Stop();
            }
            Server.Stop();
        }
    }

    #endregion
}
