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
/// Runs a Friflo-backed authoritative server against two Friflo-backed clients over Klotho's real
/// server-driven networking, and checks they stay identical.
/// </summary>
/// <remarks>
/// Server-driven play is a different stack from the peer-to-peer path covered by
/// <see cref="Test_NetworkedDeterminism"/>, not a variation on it: the server owns the only real
/// state and broadcasts each verified tick, clients predict ahead and re-simulate against those
/// broadcasts, and — the part with no P2P equivalent — a client's world does not exist until the
/// server sends it as a full state. That last point is why this test is worth having: a bridge that
/// satisfied <c>ISimulation</c> and passed every P2P test could still hand clients an incomplete
/// world here, and did.
/// <para>
/// All peers live in one process on a <see cref="LoopbackHub"/>, so delivery is ordered and
/// lossless and a hash mismatch can only come from the simulation. Real UDP would add loss and
/// reordering, and would make the test's outcome depend on timing.
/// </para>
/// </remarks>
public static class Test_ServerDrivenDeterminism
{
    /// <summary>Type id for <see cref="NudgeCommand"/>. Every peer must agree on it.</summary>
    private const int NudgeCommandTypeId = 101;

    /// <summary>Frames to run once the match is live.</summary>
    private const int MatchFrames = 160;

    /// <summary>Wall-clock ms per simulation tick. Short, to keep the test quick.</summary>
    private const int TickIntervalMs = 10;

    /// <summary>Players in the match. The server starts once both are ready.</summary>
    private const int PlayerCount = 2;

    /// <summary>
    /// Polls a message waits in flight on a delayed link. Chosen to exceed the engine's
    /// InputDelayTicks so a client's input is genuinely late and the rollback path is taken.
    /// </summary>
    private const int LinkLatencyPolls = 8;

    #region command

    /// <summary>
    /// Nudges the sending player's avatar. Serialized, sent to the server, and re-executed on
    /// rollback like any real input.
    /// </summary>
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

    /// <summary>An avatar owned by one player, moved by that player's commands.</summary>
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
    /// Scripts live outside the archetypes and so travel in their own section of a full state. A
    /// server-driven client's whole world arrives that way, which is why the seeded world here
    /// carries one: a client whose scripts went missing would otherwise still pass every
    /// component-only check.
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

    /// <summary>
    /// Game side of the simulation: one avatar per player, a drift system, one command per tick.
    /// </summary>
    /// <remarks>
    /// The same instance shape runs on the server and on every client. Only the server's
    /// <c>OnInitializeWorld</c> is ever called — clients receive that world as a full state — so
    /// the seeding below defines the match's tick-0 state exactly once.
    /// </remarks>
    private sealed class Callbacks : IFrifloSimulationCallbacks, IFrifloCommandApplier
    {
        /// <inheritdoc />
        public void RegisterSystems(SystemRoot systems, EntityStore store) => systems.Add(new DriftSystem());

        /// <inheritdoc />
        public void OnInitializeWorld(EntityStore store, int randomSeed)
        {
            // Server only. Fixed creation order: entity ids are assigned in sequence and the state
            // hash covers them, so a different order would change the world the clients receive.
            //
            // Each avatar gets a child entity and a script as well as components, so the world the
            // clients must receive spans all three of the ways state is stored. Components alone
            // would leave the parts that cannot travel as raw bytes untested - and those are
            // exactly the parts that were broken.
            for (int playerId = 1; playerId <= PlayerCount; playerId++) {
                var avatar = store.CreateEntity(new Avatar { playerId = playerId, x = FP64.Zero, y = FP64.Zero });
                var held   = store.CreateEntity(new Held { slot = playerId });
                avatar.AddChild(held);
                avatar.AddScript(new Loadout { charges = playerId * 3, held = held });
            }
        }

        /// <inheritdoc />
        public void OnPollInput(int playerId, int tick, IFrifloCommandSink sink)
        {
            // Derived from the player id as well as the tick, and deliberately NOT a pattern the
            // engine's "repeat the last continuous command" predictor can guess. A client
            // predicting its opponent therefore gets it wrong and must roll back when the server's
            // verified state arrives - which is the path worth testing. It stays a pure function of
            // (playerId, tick), so a re-simulation of the same tick produces the same command.
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
    /// A server and its clients must hold the same state at every tick the server has confirmed.
    /// </summary>
    [Test]
    public static void ServerAndClients_StayInSync()
    {
        using var match = Match.Start();

        // Links are delayed now that the match is live. A client's input arrives after the tick
        // that needs it, so the server substitutes an empty command and the client's prediction is
        // later contradicted - which is what drives it through rollback and re-simulation. Applied
        // only now because the handshake is a request/response sequence a delay merely slows down.
        foreach (var link in match.ClientLinks) {
            link.LatencyPolls = LinkLatencyPolls;
        }

        // Verified state is the only fair comparison. A client runs ahead of the server on
        // prediction, so at the same CurrentTick it may hold a guess the server has not confirmed
        // and may yet contradict - comparing that would flag normal prediction as a desync. Each
        // side records the hash of ticks it considers settled, and the records are compared after.
        //
        // A client is sampled from its verified-tick event rather than between frames: it
        // re-simulates back up to its prediction lead as soon as a verified tick lands, so by the
        // time Update returns the store is on a predicted tick again and the verified state is gone.
        var serverHashes = new Dictionary<int, long>();
        var clientHashes = new Dictionary<int, long>[match.Clients.Length];
        for (int i = 0; i < clientHashes.Length; i++) {
            clientHashes[i] = new Dictionary<int, long>();
            var record = clientHashes[i];
            var client = match.Clients[i];
            // First write wins: a verified tick is final, so re-recording it should be impossible -
            // and if it were not, silently overwriting would hide exactly the bug worth seeing.
            client.OnTickVerified += tick => {
                if (!record.ContainsKey(tick)) {
                    record[tick] = client.Simulation.GetStateHash();
                }
            };
        }

        for (int frame = 0; frame < MatchFrames; frame++) {
            match.Pump();
            RecordServerVerified(match.Server, serverHashes);
        }

        int comparisons = 0;
        for (int i = 0; i < match.Clients.Length; i++) {
            foreach (var (tick, clientHash) in clientHashes[i]) {
                if (!serverHashes.TryGetValue(tick, out long serverHash)) {
                    continue;   // the server's record of this tick has been overwritten
                }
                Assert.AreEqual(serverHash, clientHash, $"client {i} desynced from the server at verified tick {tick}");
                comparisons++;
            }
        }

        // Everything below guards against a green run that checked nothing. A hash comparison only
        // means something if the match advanced, if server and client verified overlapping ticks,
        // and if the rollback path - the whole reason for the delayed links - was taken.
        Assert.Greater(match.Server.Simulation.CurrentTick, 0, "the match never advanced - the test proved nothing");
        // A handful of overlapping ticks would be a technically-passing run that barely looked at
        // anything. Ten per client is well under what a healthy match produces (~100 each) and well
        // above what a stalled one would.
        Assert.Greater(comparisons, 10 * match.Clients.Length,
            $"only {comparisons} verified ticks were compared - too few to conclude the peers agree");
        for (int i = 0; i < match.Clients.Length; i++) {
            // Counted on the simulation, not on the engine: a server-driven client rewinds by
            // calling Rollback directly, bypassing the engine's own (debug-only, P2P-only) tally.
            Assert.Greater(match.Clients[i].Simulation.RollbackCount, 0,
                $"client {i} never rolled back - the verified re-simulation loop went untested");
            // The engine recovers from a determinism failure by discarding local state and taking
            // the server's, which would leave the hashes above agreeing again afterwards. Without
            // this the test could pass on a client that desynced repeatedly and was repeatedly
            // rescued.
            Assert.AreEqual(0, match.Clients[i].ResyncCount,
                $"client {i} desynced and was rebuilt from server state");
        }
    }

    /// <summary>
    /// A client's world must come from the server, not from running world setup locally.
    /// </summary>
    /// <remarks>
    /// This is the property that distinguishes server-driven from peer-to-peer play, and the one a
    /// P2P-only test suite cannot check: <c>KlothoEngine.Start</c> skips world initialization on an
    /// SD client precisely so the authoritative state can arrive over the wire instead. A bridge
    /// whose full-state transfer drops part of the world therefore leaves clients permanently
    /// short of it, rather than merely degrading a resync.
    /// </remarks>
    [Test]
    public static void Clients_ReceiveTheWorldFromTheServer()
    {
        using var match = Match.Start();

        int seeded = match.Server.Store.Count;
        Assert.Greater(seeded, 0, "the server never seeded a world - the test proved nothing");

        foreach (var client in match.Clients) {
            Assert.IsTrue(client.HasWorld, "the client never received the server's world");
            Assert.AreEqual(seeded, client.Store.Count, "the client's world differs from the server's");
        }

        // Entity count alone would pass on a world that arrived with the right shape and the wrong
        // contents. The parts checked below are the ones that cannot travel as raw component
        // bytes - a script is a class instance outside the archetypes, and a child list is a handle
        // into a pooled allocator - so they are where an incomplete transfer shows up. They are
        // read per entity rather than via a whole-store hash because the peers cannot be caught at
        // a common tick once the match is running: the client jumps its prediction lead ahead the
        // moment the world lands, and the server ticks on as soon as the clients acknowledge.
        foreach (var client in match.Clients) {
            for (int playerId = 1; playerId <= PlayerCount; playerId++) {
                var onServer = FindAvatar(match.Server.Store, playerId);
                var onClient = FindAvatar(client.Store,        playerId);
                Assert.IsFalse(onClient.IsNull, $"player {playerId}'s avatar never reached the client");

                // Reading a child is the operation that throws when the pool was not rebuilt.
                Assert.AreEqual(onServer.ChildCount, onClient.ChildCount, "the avatar's children did not arrive");
                Assert.AreEqual(onServer.ChildEntities[0].Id, onClient.ChildEntities[0].Id, "the child link changed");

                var serverLoadout = onServer.GetScript<Loadout>();
                var clientLoadout = onClient.GetScript<Loadout>();
                Assert.IsNotNull(clientLoadout, "the avatar's script never reached the client");
                Assert.AreEqual(serverLoadout.charges, clientLoadout.charges, "the script's field value was lost");
                // An Entity field carries only (Id, Revision) and is re-bound on arrival, so it
                // must point into the client's own store.
                Assert.AreEqual(serverLoadout.held.Id, clientLoadout.held.Id, "the script's entity reference was lost");
                Assert.AreSame(client.Store, clientLoadout.held.Store, "the script still points at the server's store");
            }
        }
    }

    #endregion

    #region helpers

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

    /// <summary>
    /// Record the server's hash for the tick it just executed.
    /// </summary>
    /// <remarks>
    /// Every server tick is verified by definition — it is the authority — so the state after
    /// executing <c>CurrentTick - 1</c> is final as soon as it exists.
    /// </remarks>
    private static void RecordServerVerified(FrifloKlothoServer server, Dictionary<int, long> into)
    {
        int executedTick = server.Simulation.CurrentTick - 1;
        if (executedTick < 0) {
            return;
        }
        // First write wins: a verified tick is final, so re-recording it should be impossible - and
        // if it were not, silently overwriting would hide exactly the bug worth seeing.
        if (!into.ContainsKey(executedTick)) {
            into[executedTick] = server.Simulation.GetStateHash();
        }
    }

    /// <summary>Register the game's command types. Identical on every peer, by construction.</summary>
    private static void RegisterCommands(CommandFactory factory) =>
        factory.RegisterCommand<NudgeCommand>(NudgeCommandTypeId);

    private static SimulationConfig NewSimulationConfig() => new() {
        Mode             = NetworkMode.ServerDriven,
        TickIntervalMs   = TickIntervalMs,
        // Server-driven play requires at least 1; the config validator rejects 0 outright, since a
        // command that leaves at the tick it is meant for can never arrive in time.
        InputDelayTicks  = 2,
        MaxRollbackTicks = 30,
        SyncCheckInterval = 10,
    };

    private static SessionConfig NewSessionConfig() => new() {
        MaxPlayers          = PlayerCount,
        MinPlayers          = PlayerCount,
        // Kept small so the countdown, which runs on wall-clock, does not dominate the test.
        CountdownDurationMs = 1,
    };

    /// <summary>
    /// A running match: one server, its clients, and the links between them.
    /// </summary>
    /// <remarks>
    /// Construction runs the whole bootstrap — the real one, with no test-only shortcuts: transport
    /// connect, PlayerJoin, the sync round trips, the server's authoritative configuration, ready
    /// signals, GameStart, the initial full state, and each client's acknowledgement of it. The
    /// server only starts ticking once every client has acknowledged, so a match handed back from
    /// here has every peer holding the same tick-0 world.
    /// </remarks>
    private sealed class Match : IDisposable
    {
        internal FrifloKlothoServer   Server      { get; private init; }
        internal FrifloKlothoClient[] Clients     { get; private init; }
        /// <summary>Each client's receiving endpoint, for injecting latency.</summary>
        internal LoopbackTransport[]  ClientLinks { get; private init; }

        private LoopbackTransport serverLink;

        internal static Match Start()
        {
            var hub          = new LoopbackHub();
            var serverLink   = hub.CreateServer();
            var clientLinks  = new LoopbackTransport[PlayerCount];
            var clients      = new FrifloKlothoClient[PlayerCount];

            var simConfig     = NewSimulationConfig();
            var sessionConfig = NewSessionConfig();

            var server = FrifloKlothoServer.Create(
                new EntityStore(), serverLink, simConfig, sessionConfig, new Callbacks(), RegisterCommands);

            for (int i = 0; i < PlayerCount; i++) {
                clientLinks[i] = hub.CreateClient();
                clients[i] = FrifloKlothoClient.Connect(
                    new EntityStore(), clientLinks[i], NewSimulationConfig(), NewSessionConfig(),
                    new Callbacks(), RegisterCommands);
                // Only now that the client's handlers are attached: connecting earlier would raise
                // the transport events into a half-wired session.
                hub.Connect(clientLinks[i]);
            }

            var match = new Match {
                Server = server, Clients = clients, ClientLinks = clientLinks, serverLink = serverLink,
            };

            match.PumpUntil(
                () => {
                    foreach (var client in match.Clients) {
                        if (client.Phase != SessionPhase.Synchronized) return false;
                    }
                    return true;
                },
                "clients did not finish the handshake");

            foreach (var client in match.Clients) {
                client.SetReady();
            }

            // The server starts the match itself once every player is ready; there is no ready
            // signal from the server side. Running is reached only after the initial full state has
            // been sent and acknowledged by every client.
            match.PumpUntil(
                () => {
                    if (match.Server.State != KlothoState.Running) return false;
                    foreach (var client in match.Clients) {
                        if (client.State != KlothoState.Running || !client.HasWorld) return false;
                    }
                    return true;
                },
                "the match did not start");

            return match;
        }

        /// <summary>Advance every peer one frame, delivering whatever each has queued for the others.</summary>
        internal void Pump()
        {
            serverLink.PollEvents();
            foreach (var link in ClientLinks) {
                link.PollEvents();
            }
            Server.Update(TickIntervalMs / 1000f);
            foreach (var client in Clients) {
                client.Update(TickIntervalMs / 1000f);
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
            foreach (var client in Clients) {
                states.Add($"phase={client.Phase} state={client.State} hasWorld={client.HasWorld}");
            }
            Assert.Fail($"{failureMessage} (server: phase={Server.Phase} state={Server.State} " +
                        $"players={Server.PlayerCount}, clients: {string.Join("; ", states)})");
        }

        public void Dispose()
        {
            foreach (var client in Clients) {
                client.Stop();
            }
            Server.Stop();
        }
    }

    #endregion
}
