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
/// Runs two Friflo-backed peers against each other over Klotho's real networking, and checks they
/// stay identical.
/// </summary>
/// <remarks>
/// The other determinism tests drive <see cref="FrifloSimulation"/> directly, which proves a
/// snapshot round-trips but never involves the engine. Here the whole stack runs: handshake, game
/// start, command serialization, input prediction for a peer whose input has not arrived yet, and
/// the rollback and re-simulation that follow when it does. Those paths are where a bridge that
/// merely satisfies the interface would still desync.
/// <para>
/// Both peers live in one process on a <see cref="LoopbackTransport"/> pair, so delivery is ordered
/// and lossless and a hash mismatch can only come from the simulation. Real UDP would add loss and
/// reordering, and would make the test's outcome depend on timing.
/// </para>
/// </remarks>
public static class Test_NetworkedDeterminism
{
    /// <summary>Type id for <see cref="NudgeCommand"/>. Both peers must agree on it.</summary>
    private const int NudgeCommandTypeId = 100;

    /// <summary>Ticks to run once the match is live.</summary>
    private const int MatchTicks = 120;

    /// <summary>Wall-clock ms per simulation tick. Short, to keep the test quick.</summary>
    private const int TickIntervalMs = 10;

    /// <summary>
    /// Polls a message waits in flight once the match is live. Chosen to exceed the engine's
    /// InputDelayTicks so a peer's input is genuinely late and the rollback path is taken.
    /// </summary>
    private const int LinkLatencyPolls = 12;

    #region command

    /// <summary>
    /// Nudges the sending player's avatar. Serialized, sent and re-executed on rollback like any
    /// real input.
    /// </summary>
    /// <remarks>
    /// Marked as continuous input so the engine repeats the last one while a peer's command for a
    /// tick is still in flight. That is what puts a misprediction — and the rollback correcting it —
    /// on the path this test is here to check.
    /// </remarks>
    private sealed class NudgeCommand : CommandBase
    {
        public int dx;
        public int dy;

        public override int  CommandTypeId      => NudgeCommandTypeId;
        public override bool IsContinuousInput  => true;

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

    /// <summary>
    /// Game side of the networked simulation: two avatars, a drift system, and one command per tick.
    /// </summary>
    private sealed class Callbacks : IFrifloSimulationCallbacks, IFrifloCommandApplier
    {
        /// <summary>Player ids seeded at tick 0. Fixed, so both peers build the same world.</summary>
        private static readonly int[] PlayerIds = { 0, 1 };

        /// <inheritdoc />
        public void RegisterSystems(SystemRoot systems, EntityStore store) => systems.Add(new DriftSystem());

        /// <inheritdoc />
        public void OnInitializeWorld(EntityStore store, int randomSeed)
        {
            // Fixed creation order: entity ids are assigned in sequence, and the state hash covers
            // them, so seeding in a different order on one peer would desync at tick 0.
            foreach (int playerId in PlayerIds) {
                store.CreateEntity(new Avatar { playerId = playerId, x = FP64.Zero, y = FP64.Zero });
            }
        }

        /// <inheritdoc />
        public void OnPollInput(int playerId, int tick, IFrifloCommandSink sink)
        {
            // Derived from the player id as well as the tick, and deliberately NOT a pattern the
            // engine's "repeat the last continuous command" predictor can guess. A peer predicting
            // its opponent therefore gets it wrong, and has to roll back when the real command
            // lands — which is the path worth testing. It stays a pure function of (playerId, tick),
            // so a re-simulation of the same tick still produces the same command.
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
    /// A host and a guest playing the same match must hold the same state at every tick.
    /// </summary>
    [Test]
    public static void TwoNetworkedPeers_StayInSync()
    {
        var (hostTransport, guestTransport) = LoopbackTransport.CreatePair();

        var hostStore  = new EntityStore();
        var guestStore = new EntityStore();

        var simConfig     = NewSimulationConfig();
        var sessionConfig = NewSessionConfig();

        var host = FrifloKlothoSession.CreateHost(
            hostStore, hostTransport, simConfig, sessionConfig, new Callbacks(), RegisterCommands);

        // The guest goes through the real handshake: PlayerJoin, SyncComplete, and the host's
        // authoritative SimulationConfig off the wire. Handing it the host's config object directly
        // would have skipped the part of the stack this test exists to exercise.
        var guest = JoinAsGuest(host, hostTransport, guestTransport, guestStore, sessionConfig);

        try {
            // The guest's SetReady is refused unless its own service has reached Synchronized, which
            // InitializeFromConnection sets — but its engine still has to see the roster first.
            PumpUntil(host, guest, hostTransport, guestTransport,
                      () => host.Phase == SessionPhase.Synchronized && guest.Phase == SessionPhase.Synchronized,
                      "peers did not reach Synchronized");

            host.SetReady();
            guest.SetReady();
            PumpUntil(host, guest, hostTransport, guestTransport,
                      () => host.State == KlothoState.Running && guest.State == KlothoState.Running,
                      "match did not start");

            // Delay the link now that the match is live. Inputs arrive after the tick that needs
            // them, so each peer predicts the other, is corrected, and rolls back — which is the
            // path this test exists to check. Applied only now because the handshake is a
            // request/response sequence that a delay merely slows down.
            hostTransport.LatencyPolls  = LinkLatencyPolls;
            guestTransport.LatencyPolls = LinkLatencyPolls;

            // Both peers were seeded independently; if that disagreed, everything after is noise.
            Assert.AreEqual(host.Simulation.GetStateHash(), guest.Simulation.GetStateHash(),
                "peers disagreed before the first tick - world seeding is not deterministic");

            // Live state is speculative: each peer predicts the other's input and only later learns
            // whether it guessed right. Two peers at the same CurrentTick can therefore legitimately
            // hold different state — one may still be carrying a misprediction the other has already
            // corrected. Comparing that would flag normal prediction as a desync.
            //
            // What must agree is *verified* state: a tick both peers have confirmed and will never
            // revise. Each side's hash for such a tick is recorded as it is reached, and the two
            // records are compared afterwards. This is the same rule the engine applies to its own
            // sync-hash exchange, which sends only for a verified tick with no rollback pending.
            var hostVerified  = new Dictionary<int, long>();
            var guestVerified = new Dictionary<int, long>();

            for (int frame = 0; frame < MatchTicks; frame++) {
                Pump(host, guest, hostTransport, guestTransport);
                RecordVerified(host,  hostVerified);
                RecordVerified(guest, guestVerified);
            }

            int comparisons = 0;
            foreach (var (tick, hostHash) in hostVerified) {
                if (!guestVerified.TryGetValue(tick, out long guestHash)) {
                    continue;   // the other peer has not verified this tick yet
                }
                Assert.AreEqual(hostHash, guestHash, $"desync at verified tick {tick}");
                comparisons++;
            }

            // Everything below guards against a green run that checked nothing. A hash comparison
            // only means something if the match advanced, if the peers actually verified overlapping
            // ticks, and if the rollback path — the whole reason for the delayed link — was taken.
            Assert.Greater(host.Simulation.CurrentTick, 0, "the match never advanced - the test proved nothing");
            Assert.Greater(comparisons, 0, "the peers never verified a common tick - nothing was compared");
            Assert.Greater(host.Engine.Stats.TotalRollbacks, 0,
                "the host never rolled back - prediction was never wrong, so re-simulation went untested");
            Assert.Greater(guest.Engine.Stats.TotalRollbacks, 0,
                "the guest never rolled back - prediction was never wrong, so re-simulation went untested");
            Assert.AreEqual(0, host.Engine.Stats.FailedRollbacks,  "host rollback failed");
            Assert.AreEqual(0, guest.Engine.Stats.FailedRollbacks, "guest rollback failed");
        }
        finally {
            guest.Stop();
            host.Stop();
        }
    }

    /// <summary>
    /// Rolling all the way back to tick 0 must restore the seeded world, not an empty store.
    /// </summary>
    /// <remarks>
    /// Tick 0 is the one snapshot not produced by an ordinary tick: it spans the engine's start
    /// sequence and the world seeding that the bridge performs there. The main test above never
    /// reaches back this far — its rollbacks stay within a few ticks of the present — so the deepest
    /// rollback the ring can serve would otherwise go unexercised.
    /// </remarks>
    [Test]
    public static void RollbackToTickZero_RestoresTheSeededWorld()
    {
        var (hostTransport, guestTransport) = LoopbackTransport.CreatePair();
        var hostStore = new EntityStore();

        var host = FrifloKlothoSession.CreateHost(
            hostStore, hostTransport, NewSimulationConfig(), NewSessionConfig(),
            new Callbacks(), RegisterCommands);
        var guest = JoinAsGuest(host, hostTransport, guestTransport, new EntityStore(), NewSessionConfig());

        try {
            PumpUntil(host, guest, hostTransport, guestTransport,
                      () => host.Phase == SessionPhase.Synchronized && guest.Phase == SessionPhase.Synchronized,
                      "peers did not reach Synchronized");
            host.SetReady();
            guest.SetReady();
            PumpUntil(host, guest, hostTransport, guestTransport,
                      () => host.State == KlothoState.Running && guest.State == KlothoState.Running,
                      "match did not start");

            int seededEntities = host.Store.Count;
            Assert.Greater(seededEntities, 0, "the world was never seeded - the test proved nothing");

            // The state at tick 0 cannot be read off the live store: by the time the engine reports
            // Running it has already executed a tick or two. Roll back to 0 first, and take the
            // reference reading from the restored snapshot itself.
            host.Simulation.Rollback(0);
            Assert.AreEqual(seededEntities, host.Store.Count,
                "rolling back to tick 0 lost the seeded entities - tick 0 was snapshotted without the world");
            long hashAtZero = host.Simulation.GetStateHash();

            // Advance well past tick 0, then return to it: the same snapshot must restore the same
            // state a second time, from a very different starting point.
            for (int frame = 0; frame < 20; frame++) {
                Pump(host, guest, hostTransport, guestTransport);
            }
            Assert.Greater(host.Simulation.CurrentTick, 0, "the match never advanced");
            Assert.AreNotEqual(hashAtZero, host.Simulation.GetStateHash(),
                "the state never changed after tick 0 - the rollback below would prove nothing");

            host.Simulation.Rollback(0);
            Assert.AreEqual(seededEntities, host.Store.Count, "entities were lost rolling back to tick 0");
            Assert.AreEqual(hashAtZero, host.Simulation.GetStateHash(),
                "the restored tick-0 state differs from the state that was snapshotted");
        }
        finally {
            guest.Stop();
            host.Stop();
        }
    }

    #endregion

    #region helpers

    /// <summary>
    /// Run the guest through <see cref="KlothoConnection"/>'s handshake and build its session from
    /// the result.
    /// </summary>
    /// <remarks>
    /// The host has to be pumped alongside the connection: it is the peer that answers the join and
    /// sends the config. Only the guest's transport is polled by <c>KlothoConnection.Update</c>.
    /// </remarks>
    private static FrifloKlothoSession JoinAsGuest(
        FrifloKlothoSession host,
        LoopbackTransport   hostTransport,
        LoopbackTransport   guestTransport,
        EntityStore         guestStore,
        ISessionConfig      sessionConfig)
    {
        ConnectionResult result    = null;
        Exception        failure   = null;

        var connection = KlothoConnection.Connect(
            guestTransport, "loopback", 0,
            onCompleted: r => result  = r,
            onFailed:    e => failure = e);

        // Handlers are attached now, so it is safe to raise the connection events.
        LoopbackTransport.Connect(hostTransport, guestTransport);

        const int MaxFrames = 2000;
        for (int frame = 0; frame < MaxFrames && result == null && failure == null; frame++) {
            connection.Update();        // polls the guest transport
            hostTransport.PollEvents(); // lets the host answer
            host.Update(TickIntervalMs / 1000f);
        }

        if (failure != null) {
            Assert.Fail($"guest handshake failed: {failure.Message}");
        }
        Assert.IsNotNull(result, "guest handshake did not complete");
        Assert.IsNotNull(result.SimulationConfig, "host never delivered its SimulationConfig");

        return FrifloKlothoSession.CreateGuest(
            guestStore, result, sessionConfig, new Callbacks(), RegisterCommands);
    }

    /// <summary>
    /// Record this peer's state hash if what it currently holds is verified — i.e. settled, and not
    /// subject to a later correction.
    /// </summary>
    /// <remarks>
    /// The store holds the state after the most recently executed tick, <c>CurrentTick - 1</c>. That
    /// is safe to record only when it is also the last verified tick, meaning every input for it has
    /// arrived and nothing will roll it back. This mirrors the engine's own rule for sending a sync
    /// hash (<c>CurrentTick == _lastVerifiedTick + 1 &amp;&amp; !_hasPendingRollback</c>); the
    /// rollback term needs no equivalent here because a pending rollback is flushed before
    /// <c>Update</c> returns, and this is only ever called between frames.
    /// <para>First write wins: a verified tick is final, so re-recording it should be impossible —
    /// and if it were not, silently overwriting would hide exactly the bug worth seeing.</para>
    /// </remarks>
    private static void RecordVerified(FrifloKlothoSession peer, Dictionary<int, long> into)
    {
        int executedTick = peer.Simulation.CurrentTick - 1;
        if (executedTick < 0 || executedTick != peer.Engine.LastVerifiedTick) {
            return;
        }
        if (!into.ContainsKey(executedTick)) {
            into[executedTick] = peer.Simulation.GetStateHash();
        }
    }

    /// <summary>Register the game's command types. Identical on both peers, by construction.</summary>
    private static void RegisterCommands(CommandFactory factory) =>
        factory.RegisterCommand<NudgeCommand>(NudgeCommandTypeId);

    private static SimulationConfig NewSimulationConfig() => new() {
        Mode              = NetworkMode.P2P,
        TickIntervalMs    = TickIntervalMs,
        InputDelayTicks   = 2,
        MaxRollbackTicks  = 30,
        SyncCheckInterval = 10,
    };

    private static SessionConfig NewSessionConfig() => new() {
        MaxPlayers          = 2,
        MinPlayers          = 2,
        // Kept small so the countdown, which runs on wall-clock, does not dominate the test.
        CountdownDurationMs = 1,
    };

    /// <summary>Advance both peers one frame, delivering whatever each has queued for the other.</summary>
    private static void Pump(
        FrifloKlothoSession host,  FrifloKlothoSession guest,
        LoopbackTransport   hostT, LoopbackTransport   guestT)
    {
        hostT.PollEvents();
        guestT.PollEvents();
        host.Update(TickIntervalMs / 1000f);
        guest.Update(TickIntervalMs / 1000f);
    }

    /// <summary>
    /// Pump until <paramref name="condition"/> holds, advancing real time because the handshake and
    /// the countdown are wall-clock driven.
    /// </summary>
    private static void PumpUntil(
        FrifloKlothoSession host,  FrifloKlothoSession guest,
        LoopbackTransport   hostT, LoopbackTransport   guestT,
        Func<bool> condition, string failureMessage)
    {
        const int MaxFrames = 2000;
        for (int frame = 0; frame < MaxFrames; frame++) {
            if (condition()) {
                return;
            }
            Pump(host, guest, hostT, guestT);
            System.Threading.Thread.Sleep(1);
        }
        Assert.Fail($"{failureMessage} (host: phase={host.Phase} state={host.State}, " +
                    $"guest: phase={guest.Phase} state={guest.State})");
    }

    #endregion
}
