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
/// Runs a server-driven match over a link that loses, reorders and duplicates packets, and checks
/// the peers still agree.
/// </summary>
/// <remarks>
/// The other networked tests run on an ideal link: nothing is lost, nothing arrives out of order.
/// That is the right default — it makes a hash mismatch attributable to the simulation — but it
/// leaves the protocol's recovery machinery dormant. Input that never arrives, input that arrives
/// twice, and input that arrives after the tick it was meant for are all ordinary on a real
/// network, and each one reaches the bridge differently: as an empty-command substitution, as a
/// command the receiver must not apply twice, and as a rejected past-tick arrival the client has to
/// correct for.
/// <para>
/// Only unreliable traffic is degraded, because that is the only traffic a real transport lets
/// through unrepaired — see <see cref="LinkConditions"/>. The randomness is seeded, so a failure
/// here reproduces rather than being a coin flip.
/// </para>
/// </remarks>
public static class Test_LossyNetwork
{
    /// <summary>Type id for <see cref="NudgeCommand"/>. Every peer must agree on it.</summary>
    private const int NudgeCommandTypeId = 102;

    /// <summary>Frames to run once the match is live. Longer than the clean run: loss needs time to bite.</summary>
    private const int MatchFrames = 220;

    /// <summary>Wall-clock ms per simulation tick. Short, to keep the test quick.</summary>
    private const int TickIntervalMs = 10;

    /// <summary>Players in the match. The server starts once both are ready.</summary>
    private const int PlayerCount = 2;

    /// <summary>
    /// Fraction of unreliable payloads dropped once the match is live.
    /// </summary>
    /// <remarks>
    /// High for a real network, deliberately: at a few percent a short test might lose nothing at
    /// all and pass without exercising anything. The assertions below check that loss actually
    /// happened rather than trusting the rate.
    /// </remarks>
    private const double LossRate = 0.30;

    /// <summary>Fraction of unreliable payloads held back to arrive out of order.</summary>
    private const double ReorderRate = 0.15;

    /// <summary>Fraction of unreliable payloads delivered twice.</summary>
    private const double DuplicateRate = 0.10;

    #region command

    /// <summary>
    /// Nudges the sending player's avatar. Sent unreliably by the engine, so this is the traffic
    /// the degraded link actually damages.
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

    /// <summary>Game side of the simulation: one avatar per player, a drift system, one command per tick.</summary>
    private sealed class Callbacks : IFrifloSimulationCallbacks, IFrifloCommandApplier
    {
        /// <inheritdoc />
        public void RegisterSystems(SystemRoot systems, EntityStore store) => systems.Add(new DriftSystem());

        /// <inheritdoc />
        public void OnInitializeWorld(EntityStore store, int randomSeed)
        {
            // Server only; clients receive this world as a full state. Fixed creation order, since
            // entity ids are assigned in sequence and the state hash covers them.
            for (int playerId = 1; playerId <= PlayerCount; playerId++) {
                store.CreateEntity(new Avatar { playerId = playerId, x = FP64.Zero, y = FP64.Zero });
            }
        }

        /// <inheritdoc />
        public void OnPollInput(int playerId, int tick, IFrifloCommandSink sink)
        {
            // A pure function of (playerId, tick), so re-simulating a tick reproduces its command,
            // and deliberately not a pattern the "repeat the last continuous input" predictor can
            // guess - a client predicting its opponent has to be wrong sometimes.
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
                    continue;   // EmptyCommand substituted for input that never arrived, or a system command
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
    /// Server and clients must still agree on every verified tick when the link drops, reorders and
    /// duplicates packets.
    /// </summary>
    /// <remarks>
    /// The server decides what happened — it substitutes an empty command for input that did not
    /// arrive in time — so a client whose input was lost has to accept the server's version and
    /// re-simulate, rather than keeping the move it locally predicted. That reconciliation is the
    /// path this test covers and the ideal-link tests cannot reach.
    /// </remarks>
    [Test]
    public static void PeersAgree_WhenPacketsAreLostReorderedAndDuplicated()
    {
        using var match = Match.Start();

        // Degrade only now that the match is live. The handshake is a request/response sequence
        // with retries; running it over a lossy link would slow the test without testing the
        // simulation, and its own traffic is reliable anyway.
        //
        // Both directions, and each with its own seed: client input travels one way and the
        // server's acks and pings the other, so degrading only one leaves half the unreliable
        // traffic untouched.
        match.ServerLink.Conditions = NewConditions(seed: 1);
        for (int i = 0; i < match.ClientLinks.Length; i++) {
            match.ClientLinks[i].Conditions = NewConditions(seed: 100 + i);
        }

        // Verified ticks only. A client runs ahead of the server on prediction, so at a common
        // CurrentTick it may hold a guess the server has not confirmed - comparing that would
        // report ordinary prediction as a desync. A client is sampled inside its verified-tick
        // event because that is the only moment its store holds the confirmed state.
        var serverHashes = new Dictionary<int, long>();
        var clientHashes = new Dictionary<int, long>[match.Clients.Length];
        for (int i = 0; i < clientHashes.Length; i++) {
            clientHashes[i] = new Dictionary<int, long>();
            var record = clientHashes[i];
            var client = match.Clients[i];
            client.OnTickVerified += tick => {
                // First write wins: a verified tick is final, so overwriting would hide a bug.
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
                Assert.AreEqual(serverHash, clientHash,
                    $"client {i} desynced from the server at verified tick {tick} on a lossy link");
                comparisons++;
            }
        }

        // The link must actually have misbehaved. Rates are probabilistic, and a bug that quietly
        // stopped applying them would otherwise turn this into the ideal-link test wearing a
        // different name.
        int dropped    = match.ServerLink.Conditions.DroppedCount;
        int reordered  = match.ServerLink.Conditions.ReorderedCount;
        int duplicated = match.ServerLink.Conditions.DuplicatedCount;
        for (int i = 0; i < match.ClientLinks.Length; i++) {
            dropped    += match.ClientLinks[i].Conditions.DroppedCount;
            reordered  += match.ClientLinks[i].Conditions.ReorderedCount;
            duplicated += match.ClientLinks[i].Conditions.DuplicatedCount;
        }
        Assert.Greater(dropped,    0, "no packet was ever dropped - the lossy link did nothing");
        Assert.Greater(reordered,  0, "no packet was ever reordered - the lossy link did nothing");
        Assert.Greater(duplicated, 0, "no packet was ever duplicated - the lossy link did nothing");

        // And the match must have survived it. Everything above compares hashes, which proves
        // nothing if the match stalled or the clients were rebuilt from server state each time.
        Assert.Greater(match.Server.Simulation.CurrentTick, 0, "the match never advanced - the test proved nothing");
        Assert.Greater(comparisons, 10 * match.Clients.Length,
            $"only {comparisons} verified ticks were compared - too few to conclude the peers agree");
        for (int i = 0; i < match.Clients.Length; i++) {
            Assert.Greater(match.Clients[i].Simulation.RollbackCount, 0,
                $"client {i} never rolled back - the verified re-simulation loop went untested");
            // Losing input must not cost a client its state. The engine repairs a hash mismatch by
            // replacing the client's world with the server's, after which the hashes agree again -
            // so without this the test would pass on a client that desynced continuously.
            Assert.AreEqual(0, match.Clients[i].ResyncCount,
                $"client {i} desynced and was rebuilt from server state - loss was not handled, it was papered over");
        }
    }

    /// <summary>
    /// Nothing the protocol sends unreliably may exceed what a real UDP packet can carry.
    /// </summary>
    /// <remarks>
    /// An oversized <i>reliable</i> payload is fragmented and reassembled by LiteNetLib, but an
    /// oversized unreliable one throws <c>TooBigPacketException</c> at the send call — there is no
    /// retransmission, so a lost fragment could never be recovered. In-memory transports have no
    /// size limit, so a bridge could send a command far too large for a real socket and only find
    /// out on real UDP. Here the limit is enforced, so exceeding it fails at the send that did it.
    /// <para>
    /// This is about the command payloads the bridge is responsible for. Full states are sent
    /// reliably and may legitimately be far larger than one packet.
    /// </para>
    /// </remarks>
    [Test]
    public static void UnreliableTraffic_FitsInOnePacket()
    {
        using var match = Match.Start();

        // Reliable traffic stays exempt, as on a real socket: this rejects only what would throw.
        var limit = new LinkConditions { MaxUnreliablePayload = LinkConditions.LiteNetLibInitialMtu };
        match.ServerLink.Conditions = limit;
        foreach (var link in match.ClientLinks) {
            link.Conditions = new LinkConditions { MaxUnreliablePayload = LinkConditions.LiteNetLibInitialMtu };
        }

        // Any oversized send throws out of Pump, failing the test at the frame that did it.
        for (int frame = 0; frame < MatchFrames; frame++) {
            match.Pump();
        }

        Assert.Greater(match.Server.Simulation.CurrentTick, 0, "the match never advanced - the test proved nothing");
    }

    #endregion

    #region helpers

    /// <summary>Link conditions for one endpoint, seeded so the run reproduces.</summary>
    private static LinkConditions NewConditions(int seed) => new() {
        LossRate      = LossRate,
        ReorderRate   = ReorderRate,
        DuplicateRate = DuplicateRate,
        Seed          = seed,
    };

    /// <summary>
    /// Record the server's hash for the tick it just executed.
    /// </summary>
    /// <remarks>Every server tick is verified by definition — the server is the authority.</remarks>
    private static void RecordServerVerified(FrifloKlothoServer server, Dictionary<int, long> into)
    {
        int executedTick = server.Simulation.CurrentTick - 1;
        if (executedTick < 0) {
            return;
        }
        if (!into.ContainsKey(executedTick)) {
            into[executedTick] = server.Simulation.GetStateHash();
        }
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
        // Short, so a dropped input is resent within the window that still matters. The default
        // 25ms is more than two ticks at this tick rate.
        InputResendIntervalMs = 10,
    };

    private static SessionConfig NewSessionConfig() => new() {
        MaxPlayers          = PlayerCount,
        MinPlayers          = PlayerCount,
        CountdownDurationMs = 1,
    };

    /// <summary>
    /// A running match: one server, its clients, and the links between them.
    /// </summary>
    /// <remarks>
    /// Construction runs the real bootstrap with no test-only shortcuts, on a clean link — see the
    /// note in the test on why the degradation is applied afterwards.
    /// </remarks>
    private sealed class Match : IDisposable
    {
        internal FrifloKlothoServer   Server      { get; private init; }
        internal FrifloKlothoClient[] Clients     { get; private init; }
        /// <summary>Each client's receiving endpoint.</summary>
        internal LoopbackTransport[]  ClientLinks { get; private init; }
        /// <summary>The server's receiving endpoint, for degrading the client-to-server direction.</summary>
        internal LoopbackTransport    ServerLink  { get; private init; }

        internal static Match Start()
        {
            var hub         = new LoopbackHub();
            var serverLink  = hub.CreateServer();
            var clientLinks = new LoopbackTransport[PlayerCount];
            var clients     = new FrifloKlothoClient[PlayerCount];

            var server = FrifloKlothoServer.Create(
                new EntityStore(), serverLink, NewSimulationConfig(), NewSessionConfig(),
                new Callbacks(), RegisterCommands);

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
                Server = server, Clients = clients, ClientLinks = clientLinks, ServerLink = serverLink,
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
            ServerLink.PollEvents();
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
