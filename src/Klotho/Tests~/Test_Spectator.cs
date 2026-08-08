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
/// Attaches a spectator to a running server-driven match and checks it replays the same game the
/// players are in.
/// </summary>
/// <remarks>
/// A spectator is the one participant whose correctness nobody else can observe. It holds no player
/// slot, sends nothing, and cannot affect the outcome, so a spectator that fell behind or replayed
/// a subtly different game would look completely healthy from the server's side and from every
/// player's. That makes it worth checking against the server's own state directly.
/// <para>
/// It also exercises the full-state transfer from a third entry point, with a difference that
/// matters: a spectator receives the world and then advances only on confirmed input, never
/// predicting. If the confirmed command stream or the world it starts from were wrong, it would
/// drift with nothing to correct it — a player would be pulled back by the next verified tick.
/// </para>
/// </remarks>
public static class Test_Spectator
{
    /// <summary>Type id for <see cref="NudgeCommand"/>. Every peer must agree on it.</summary>
    private const int NudgeCommandTypeId = 105;

    /// <summary>Wall-clock ms per simulation tick. Short, to keep the test quick.</summary>
    private const int TickIntervalMs = 10;

    /// <summary>Players in the match.</summary>
    private const int PlayerCount = 2;

    /// <summary>Frames the match runs before the spectator attaches.</summary>
    private const int FramesBeforeWatching = 40;

    /// <summary>Frames the match runs with the spectator attached.</summary>
    private const int FramesWatching = 200;

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

    /// <summary>State attached to an avatar as a <see cref="Script"/> rather than a component.</summary>
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

    /// <summary>Applies a tick's commands. Shared by the players and the spectator.</summary>
    /// <remarks>
    /// The spectator runs the same command application as everyone else: it replays the confirmed
    /// input stream rather than receiving state per tick, so anything the players' code does to the
    /// world has to happen on the spectator too.
    /// </remarks>
    private static void Apply(EntityStore store, List<ICommand> commands)
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

    /// <summary>Game side for the server and its players.</summary>
    private sealed class Callbacks : IFrifloSimulationCallbacks, IFrifloCommandApplier
    {
        /// <inheritdoc />
        public void RegisterSystems(SystemRoot systems, EntityStore store) => systems.Add(new DriftSystem());

        /// <inheritdoc />
        public void OnInitializeWorld(EntityStore store, int randomSeed)
        {
            // Server only. Two children per avatar rather than one: an IdArray of count 1 keeps its
            // id inline and never touches the pooled allocator, so a one-child world would survive
            // a child pool that was never rebuilt. See the note in Test_LateJoin.
            for (int playerId = 1; playerId <= PlayerCount; playerId++) {
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
        public void ApplyCommands(EntityStore store, List<ICommand> commands, int tick) => Apply(store, commands);
    }

    /// <summary>
    /// Game side for the spectator: the same systems and command handling, minus what a spectator
    /// has no use for.
    /// </summary>
    private sealed class SpectatorCallbacks : IFrifloSpectatorCallbacks, IFrifloCommandApplier
    {
        /// <inheritdoc />
        public void RegisterSystems(SystemRoot systems, EntityStore store) => systems.Add(new DriftSystem());

        /// <inheritdoc />
        public void ApplyCommands(EntityStore store, List<ICommand> commands, int tick) => Apply(store, commands);
    }

    #endregion

    #region test

    /// <summary>
    /// A spectator receives the world and replays the same ticks the server executed.
    /// </summary>
    /// <remarks>
    /// Compared by state hash at matching ticks: a spectator replays only confirmed ticks and never
    /// predicts, so a tick it has executed is one the server has executed too, and the two must
    /// agree exactly. That is a stronger comparison than the one available for a playing client,
    /// whose live state legitimately holds unconfirmed guesses.
    /// </remarks>
    [Test]
    public static void Spectator_ReplaysTheSameMatch()
    {
        using var match = Match.Start();

        for (int frame = 0; frame < FramesBeforeWatching; frame++) {
            match.Pump();
        }
        Assert.Greater(match.Server.Simulation.CurrentTick, 0,
            "the match never advanced before the spectator attached - it would have nothing to catch up on");

        var spectator = match.AddSpectator();

        match.PumpUntil(
            () => spectator.HasWorld && spectator.State == SpectatorState.Watching,
            "the spectator never received the world");

        // The server's state at each tick it executes, and the spectator's at each tick it replays.
        // Sampled from the events rather than between frames: either side may advance several ticks
        // in one frame, and only the last would still be visible afterwards.
        var serverHashes    = new Dictionary<int, long>();
        var spectatorHashes = new Dictionary<int, long>();
        spectator.OnTickReplayed += tick => {
            if (!spectatorHashes.ContainsKey(tick)) {
                spectatorHashes[tick] = spectator.Simulation.GetStateHash();
            }
        };

        for (int frame = 0; frame < FramesWatching; frame++) {
            match.Pump();
            int executed = match.Server.Simulation.CurrentTick - 1;
            if (executed >= 0 && !serverHashes.ContainsKey(executed)) {
                serverHashes[executed] = match.Server.Simulation.GetStateHash();
            }
        }

        int comparisons = 0;
        foreach (var (tick, spectatorHash) in spectatorHashes) {
            if (!serverHashes.TryGetValue(tick, out long serverHash)) {
                continue;   // a tick the server executed before the spectator started recording
            }
            Assert.AreEqual(serverHash, spectatorHash,
                $"the spectator replayed tick {tick} differently from the server");
            comparisons++;
        }

        Assert.Greater(comparisons, 10,
            $"only {comparisons} ticks were compared - too few to conclude the spectator is following the match");
        // The world must have arrived exactly once. More would mean the spectator kept falling far
        // enough behind to be rebuilt from server state, which makes the hashes above agree again
        // and would hide a spectator that could not keep up on its own.
        Assert.AreEqual(1, spectator.FullStateCount,
            "the spectator was re-sent the world - it could not keep up from confirmed input alone");
        // And it must be replaying the live match rather than sitting on the state it was handed.
        Assert.Greater(spectator.Simulation.CurrentTick, 0, "the spectator never replayed a tick");
    }

    /// <summary>
    /// The world a spectator receives carries the parts that cannot travel as raw component bytes.
    /// </summary>
    /// <remarks>
    /// Checked separately from the hashes above because a spectator that never received the
    /// hierarchy or the scripts could still hash-match on a world where nothing touches them. Read
    /// per entity, since the spectator trails the server and the two are rarely on the same tick.
    /// </remarks>
    [Test]
    public static void Spectator_ReceivesTheWholeWorld()
    {
        using var match = Match.Start();

        for (int frame = 0; frame < FramesBeforeWatching; frame++) {
            match.Pump();
        }
        var spectator = match.AddSpectator();
        match.PumpUntil(
            () => spectator.HasWorld && spectator.State == SpectatorState.Watching,
            "the spectator never received the world");

        Assert.AreEqual(match.Server.Store.Count, spectator.Store.Count,
            "the spectator's world holds a different number of entities than the server's");

        for (int playerId = 1; playerId <= PlayerCount; playerId++) {
            var onServer    = FindAvatar(match.Server.Store, playerId);
            var onSpectator = FindAvatar(spectator.Store,    playerId);
            Assert.IsFalse(onSpectator.IsNull, $"player {playerId}'s avatar never reached the spectator");

            // With more than one child the ids live in the pooled allocator, so a stale handle
            // either reads back the wrong ids or throws.
            Assert.AreEqual(onServer.ChildCount, onSpectator.ChildCount, "the avatar's children did not arrive");
            for (int i = 0; i < onServer.ChildCount; i++) {
                Assert.AreEqual(onServer.ChildEntities[i].Id, onSpectator.ChildEntities[i].Id,
                    $"child {i} of player {playerId}'s avatar differs from the server's");
            }

            var serverLoadout    = onServer.GetScript<Loadout>();
            var spectatorLoadout = onSpectator.GetScript<Loadout>();
            Assert.IsNotNull(spectatorLoadout, "the avatar's script never reached the spectator");
            Assert.AreEqual(serverLoadout.charges, spectatorLoadout.charges, "the script's field value was lost");
            // An Entity field carries only (Id, Revision) and is re-bound on arrival, so it must
            // point into the spectator's own store.
            Assert.AreEqual(serverLoadout.held.Id, spectatorLoadout.held.Id, "the script's entity reference was lost");
            Assert.AreSame(spectator.Store, spectatorLoadout.held.Store, "the script still points at the server's store");
        }

        // A spectator takes no player slot: the match is still the size it was.
        Assert.AreEqual(PlayerCount, match.Server.PlayerCount, "the spectator was counted as a player");
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
    /// <c>MaxSpectators</c> defaults to 0, which rejects every spectator outright — the test would
    /// then pass its connection assertions by never getting one.
    /// </remarks>
    private static SessionConfig NewSessionConfig() => new() {
        MaxPlayers          = PlayerCount,
        MinPlayers          = PlayerCount,
        MaxSpectators       = 2,
        CountdownDurationMs = 1,
    };

    /// <summary>A running match that spectators can attach to.</summary>
    private sealed class Match : IDisposable
    {
        internal FrifloKlothoServer   Server  { get; private init; }
        internal FrifloKlothoClient[] Clients { get; private init; }

        private LoopbackHub       hub;
        private LoopbackTransport serverLink;
        private readonly List<LoopbackTransport>      links      = new();
        private readonly List<FrifloKlothoSpectator>  spectators = new();

        internal static Match Start()
        {
            var hub        = new LoopbackHub();
            var serverLink = hub.CreateServer();

            var server = FrifloKlothoServer.Create(
                new EntityStore(), serverLink, NewSimulationConfig(), NewSessionConfig(),
                new Callbacks(), RegisterCommands);

            var match = new Match { Server = server, Clients = new FrifloKlothoClient[PlayerCount] };
            match.hub        = hub;
            match.serverLink = serverLink;

            for (int i = 0; i < PlayerCount; i++) {
                var link = hub.CreateClient();
                match.Clients[i] = FrifloKlothoClient.Connect(
                    new EntityStore(), link, NewSimulationConfig(), NewSessionConfig(),
                    new Callbacks(), RegisterCommands);
                hub.Connect(link);
                match.links.Add(link);
            }

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

        /// <summary>
        /// Attach a spectator to the running match.
        /// </summary>
        /// <remarks>
        /// No handshake to run first, unlike a late join: a spectator's accept message carries the
        /// server's configuration, and the bridge builds its engine when that arrives. So the
        /// construction is a single call, and the world lands a few frames later.
        /// </remarks>
        internal FrifloKlothoSpectator AddSpectator()
        {
            var link = hub.CreateClient();
            var spectator = FrifloKlothoSpectator.Watch(
                new EntityStore(), link, new SpectatorCallbacks(), RegisterCommands);
            // Only now that the spectator's handlers are attached.
            hub.Connect(link);
            links.Add(link);
            spectators.Add(spectator);
            return spectator;
        }

        /// <summary>Advance every peer one frame, delivering whatever each has queued for the others.</summary>
        internal void Pump()
        {
            serverLink.PollEvents();
            for (int i = 0; i < links.Count; i++) {
                links[i].PollEvents();
            }
            Server.Update(TickIntervalMs / 1000f);
            foreach (var client in Clients) {
                client.Update(TickIntervalMs / 1000f);
            }
            for (int i = 0; i < spectators.Count; i++) {
                spectators[i].Update(TickIntervalMs / 1000f);
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
            for (int i = 0; i < spectators.Count; i++) {
                states.Add($"state={spectators[i].State} hasWorld={spectators[i].HasWorld}");
            }
            Assert.Fail($"{failureMessage} (server: phase={Server.Phase} state={Server.State} " +
                        $"players={Server.PlayerCount} tick={Server.Simulation.CurrentTick}, " +
                        $"spectators: {string.Join("; ", states)})");
        }

        public void Dispose()
        {
            for (int i = 0; i < spectators.Count; i++) {
                spectators[i].Stop();
            }
            foreach (var client in Clients) {
                client.Stop();
            }
            Server.Stop();
        }
    }

    #endregion
}
