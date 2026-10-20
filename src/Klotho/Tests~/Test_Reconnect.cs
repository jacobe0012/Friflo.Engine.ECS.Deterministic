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
/// Drops a client out of a running server-driven match and brings it back on its old player slot,
/// checking it resumes with the world as it now stands.
/// </summary>
/// <remarks>
/// Reconnect is late join with a claim attached: the returning client presents credentials naming
/// the session and the player id it held, and the server hands back that slot rather than a new
/// one. What it receives is the world at the current tick, not the one it left — the match ran on
/// without it — so this exercises the same full-state transfer as late join, from a different
/// entry point and against a server that has been holding a slot open.
/// <para>
/// The interesting failure is not "the reconnect is refused" but "the reconnect is accepted and the
/// client resumes subtly wrong", so the checks below are on state rather than on connection status.
/// </para>
/// </remarks>
public static class Test_Reconnect
{
    /// <summary>Type id for <see cref="NudgeCommand"/>. Every peer must agree on it.</summary>
    private const int NudgeCommandTypeId = 104;

    /// <summary>Wall-clock ms per simulation tick. Short, to keep the test quick.</summary>
    private const int TickIntervalMs = 10;

    /// <summary>Players in the match.</summary>
    private const int PlayerCount = 2;

    /// <summary>Frames the match runs before the client drops.</summary>
    private const int FramesBeforeDrop = 50;

    /// <summary>Frames the match runs while the client is away, so it comes back to a changed world.</summary>
    private const int FramesWhileAway = 40;

    /// <summary>Frames the match runs after the reconnect.</summary>
    private const int FramesAfterReturn = 140;

    /// <summary>Build identifier stored with the credentials and checked on reload.</summary>
    private const string AppVersion = "test-build";

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

    /// <summary>Game side of the simulation. Only the server's world seeding ever runs.</summary>
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

    private sealed class DeviceIdProvider : IDeviceIdProvider
    {
        public string GetDeviceId() => "original-device";
    }

    [Test]
    public static void ReconnectCredentials_ConfiguredBeforeHandshakeBindTheOriginalDevice()
    {
        using var match = Match.Start(new DeviceIdProvider());
        for (int frame = 0; frame < FramesBeforeDrop; frame++) match.Pump();
        var credentials = match.DropClient(1, match.Clients[1].LocalPlayerId);
        Assert.AreEqual("original-device", credentials.DeviceId);
        for (int frame = 0; frame < FramesWhileAway; frame++) match.Pump();
        credentials.DeviceId = "different-device";

        var returned = match.TryReconnect(credentials, out var failure);

        Assert.IsNull(returned, "the initial handshake omitted the device binding, accepting a different device");
        Assert.IsInstanceOf<ReconnectFailedException>(failure);
        Assert.AreEqual(ReconnectRejectReason.DeviceMismatch, ((ReconnectFailedException)failure).Reason);
    }

    [Test]
    public static void Reconnect_UsesAuthoritativeConfigBeforeRestoringWorld()
    {
        using var match = Match.Start();
        for (int frame = 0; frame < FramesBeforeDrop; frame++) match.Pump();
        int playerId = match.Clients[1].LocalPlayerId;
        var credentials = match.DropClient(1, playerId);
        for (int frame = 0; frame < FramesWhileAway; frame++) match.Pump();
        var local = NewSimulationConfig();
        local.TickIntervalMs = 20;
        local.InputDelayTicks = 4;
        local.MaxRollbackTicks = 8;

        var returned = match.Reconnect(credentials, local);

        Assert.AreEqual(TickIntervalMs, returned.Engine.TickInterval);
        Assert.AreEqual(2, returned.Engine.InputDelay);
        Assert.AreEqual(NewSimulationConfig().GetSnapshotCapacity(), returned.Simulation.RollbackCapacity);
        match.PumpUntil(() => returned.IsCaughtUp, "the returning client never caught up");
        Assert.AreEqual(0, returned.ResyncCount);
    }

    /// <summary>
    /// A client that drops out and comes back resumes its own player slot with the current world.
    /// </summary>
    [Test]
    public static void ReturningClient_ResumesItsSlotWithTheCurrentWorld()
    {
        using var match = Match.Start();

        int originalPlayerId = match.Clients[1].LocalPlayerId;
        Assert.GreaterOrEqual(originalPlayerId, 0, "the client never got a player id - it was not really in the match");

        for (int frame = 0; frame < FramesBeforeDrop; frame++) {
            match.Pump();
        }
        int tickAtDrop = match.Server.Simulation.CurrentTick;
        Assert.Greater(tickAtDrop, 0, "the match never advanced before the drop");

        var saved = match.DropClient(1, originalPlayerId);

        // The server has to notice, or the reconnect below would be answered from a slot that was
        // never released and the test would pass without exercising the path.
        match.PumpUntil(
            () => FindPlayer(match.Server, originalPlayerId).ConnectionState == PlayerConnectionState.Disconnected,
            "the server never noticed the client had gone");

        // The match must move on without it, so the returning client comes back to a world that has
        // changed rather than the one it left.
        for (int frame = 0; frame < FramesWhileAway; frame++) {
            match.Pump();
        }
        int tickWhileAway = match.Server.Simulation.CurrentTick;
        Assert.Greater(tickWhileAway, tickAtDrop, "the match stalled while the client was away - it would return to the same world");

        var returned = match.Reconnect(saved);

        Assert.AreEqual(originalPlayerId, returned.LocalPlayerId,
            "the returning client was given a different player id - it did not resume its own slot");
        Assert.IsTrue(returned.HasWorld, "the returning client received no world");

        match.PumpUntil(() => returned.IsCaughtUp, "the returning client never caught up with the match");

        // It came back to the world as it now stands, including the parts that cannot travel as raw
        // component bytes.
        AssertWorldMatches(match.Server.Store, returned.Store, "returning client");

        // And it keeps pace from here like any other client.
        var serverHashes   = new Dictionary<int, long>();
        var returnedHashes = new Dictionary<int, long>();
        returned.OnTickVerified += tick => {
            if (!returnedHashes.ContainsKey(tick)) {
                returnedHashes[tick] = returned.Simulation.GetStateHash();
            }
        };

        for (int frame = 0; frame < FramesAfterReturn; frame++) {
            match.Pump();
            int executed = match.Server.Simulation.CurrentTick - 1;
            if (executed >= 0 && !serverHashes.ContainsKey(executed)) {
                serverHashes[executed] = match.Server.Simulation.GetStateHash();
            }
        }

        int comparisons = 0;
        foreach (var (tick, hash) in returnedHashes) {
            if (!serverHashes.TryGetValue(tick, out long serverHash)) {
                continue;
            }
            Assert.AreEqual(serverHash, hash, $"the returning client desynced from the server at verified tick {tick}");
            comparisons++;
        }
        Assert.Greater(comparisons, 10,
            $"only {comparisons} verified ticks were compared - too few to conclude the returning client keeps pace");
        Assert.AreEqual(0, returned.ResyncCount, "the returning client desynced and was rebuilt from server state");
        Assert.AreEqual(0, match.Clients[0].ResyncCount, "the client that stayed was resynced by the other one's reconnect");
    }

    /// <summary>
    /// A reconnect claiming a session the server is not running is refused.
    /// </summary>
    /// <remarks>
    /// The session magic is what ties credentials to one match. Without this check, credentials
    /// left over from an earlier match would be accepted into the current one, and the returning
    /// client would be handed a player slot in a world it has no history with. The refusal has to
    /// come from the server rather than from a local expiry check, which is why the credentials
    /// here are otherwise valid and current.
    /// </remarks>
    [Test]
    public static void ReconnectWithTheWrongSession_IsRefused()
    {
        using var match = Match.Start();

        int originalPlayerId = match.Clients[1].LocalPlayerId;
        for (int frame = 0; frame < FramesBeforeDrop; frame++) {
            match.Pump();
        }

        var saved = match.DropClient(1, originalPlayerId);
        match.PumpUntil(
            () => FindPlayer(match.Server, originalPlayerId).ConnectionState == PlayerConnectionState.Disconnected,
            "the server never noticed the client had gone");

        // Everything else about these credentials is current - only the session they name is wrong.
        saved.SessionMagic += 1;

        Exception failure = null;
        var result = match.TryReconnect(saved, out failure);

        Assert.IsNull(result, "the server accepted a reconnect for a session it is not running");
        Assert.IsNotNull(failure, "the reconnect neither succeeded nor reported a failure");
    }

    #endregion

    #region helpers

    /// <summary><paramref name="playerId"/>'s entry on the server's roster.</summary>
    private static IPlayerInfo FindPlayer(FrifloKlothoServer server, int playerId)
    {
        var players = server.NetworkService.Players;
        for (int i = 0; i < players.Count; i++) {
            if (players[i].PlayerId == playerId) {
                return players[i];
            }
        }
        Assert.Fail($"player {playerId} is not on the server's roster");
        return null;
    }

    /// <summary>
    /// Assert <paramref name="actual"/> holds the same world as <paramref name="expected"/>.
    /// </summary>
    /// <remarks>
    /// Compared entity by entity rather than by whole-store hash: a client predicts ahead of the
    /// server and re-simulates the moment a verified tick lands, so the two are never on a common
    /// tick outside that event. What is checked is the structure a full state has to carry, which
    /// does not change between ticks.
    /// </remarks>
    private static void AssertWorldMatches(EntityStore expected, EntityStore actual, string who)
    {
        Assert.AreEqual(expected.Count, actual.Count, $"{who}: entity count differs from the server's");

        for (int playerId = 1; playerId <= PlayerCount; playerId++) {
            var onServer = FindAvatar(expected, playerId);
            var onClient = FindAvatar(actual,   playerId);
            Assert.IsFalse(onClient.IsNull, $"{who}: player {playerId}'s avatar never arrived");

            Assert.AreEqual(onServer.ChildCount, onClient.ChildCount, $"{who}: the avatar's children did not arrive");
            for (int i = 0; i < onServer.ChildCount; i++) {
                Assert.AreEqual(onServer.ChildEntities[i].Id, onClient.ChildEntities[i].Id,
                    $"{who}: child {i} of player {playerId}'s avatar differs from the server's");
            }

            var serverLoadout = onServer.GetScript<Loadout>();
            var clientLoadout = onClient.GetScript<Loadout>();
            Assert.IsNotNull(clientLoadout, $"{who}: the avatar's script never arrived");
            Assert.AreEqual(serverLoadout.charges, clientLoadout.charges, $"{who}: the script's field value was lost");
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

    private static SessionConfig NewSessionConfig() => new() {
        MaxPlayers          = PlayerCount,
        MinPlayers          = PlayerCount,
        CountdownDurationMs = 1,
        // Generous: the drop and return happen within a few hundred milliseconds here, and a tight
        // window would make the test fail on a slow machine for reasons unrelated to what it checks.
        ReconnectTimeoutMs  = 60000,
    };

    /// <summary>A credentials store that keeps the value in memory.</summary>
    /// <remarks>
    /// Production stores persist to disk so credentials survive the process dying, which is the
    /// case reconnect exists for. Here the process does not die, so memory is enough.
    /// </remarks>
    private sealed class MemoryCredentialsStore : IReconnectCredentialsStore
    {
        private PersistedReconnectCredentials saved;

        /// <inheritdoc />
        public void Save(PersistedReconnectCredentials creds) => saved = creds;

        /// <inheritdoc />
        public PersistedReconnectCredentials Load() => saved;

        /// <inheritdoc />
        public void Clear() => saved = null;

        /// <inheritdoc />
        public bool IsValid(PersistedReconnectCredentials creds, long nowUnixMs, string currentAppVersion)
        {
            if (creds == null || creds.AppVersion != currentAppVersion) {
                return false;
            }
            return nowUnixMs - creds.SavedAtUnixMs <= creds.ReconnectTimeoutMs;
        }
    }

    /// <summary>A running match whose clients can be dropped and brought back.</summary>
    private sealed class Match : IDisposable
    {
        internal FrifloKlothoServer     Server  { get; private init; }
        internal FrifloKlothoClient[]   Clients { get; private set; }
        /// <summary>Each client's persisted reconnect credentials, written by the client itself.</summary>
        internal MemoryCredentialsStore[] Stores { get; private init; }

        private LoopbackHub       hub;
        private LoopbackTransport serverLink;
        private readonly List<FrifloKlothoClient> live  = new();
        private readonly List<LoopbackTransport>  links = new();

        internal static Match Start(IDeviceIdProvider deviceIdProvider = null)
        {
            var hub        = new LoopbackHub();
            var serverLink = hub.CreateServer();

            var server = FrifloKlothoServer.Create(
                new EntityStore(), serverLink, NewSimulationConfig(), NewSessionConfig(),
                new Callbacks(), RegisterCommands);

            var match = new Match {
                Server  = server,
                Clients = new FrifloKlothoClient[PlayerCount],
                Stores  = new MemoryCredentialsStore[PlayerCount],
            };
            match.hub        = hub;
            match.serverLink = serverLink;

            for (int i = 0; i < PlayerCount; i++) {
                var link = hub.CreateClient();
                var client = FrifloKlothoClient.Connect(
                    new EntityStore(), link, NewSimulationConfig(), NewSessionConfig(),
                    new Callbacks(), RegisterCommands);
                // Before the match starts, deliberately: the credentials are written when it does,
                // so a store attached later would still be empty when the client drops.
                match.Stores[i] = new MemoryCredentialsStore();
                client.SetReconnectCredentialsStore(match.Stores[i], AppVersion, deviceIdProvider);
                hub.Connect(link);
                match.links.Add(link);
                match.live.Add(client);
                match.Clients[i] = client;
            }

            match.PumpUntil(
                () => {
                    foreach (var client in match.live) {
                        if (client.Phase != SessionPhase.Synchronized) return false;
                    }
                    return true;
                },
                "clients did not finish the handshake");

            foreach (var client in match.live) {
                client.SetReady();
            }

            match.PumpUntil(
                () => {
                    if (match.Server.State != KlothoState.Running) return false;
                    foreach (var client in match.live) {
                        if (client.State != KlothoState.Running || !client.HasWorld) return false;
                    }
                    return true;
                },
                "the match did not start");

            return match;
        }

        /// <summary>
        /// Drop the client at <paramref name="index"/> and return what it would need to come back.
        /// </summary>
        /// <remarks>
        /// Stopped keeping its credentials, and its transport disconnected: from the server's side
        /// this is a peer that vanished, which is what puts its player into the disconnected pool
        /// rather than removing it from the roster. An ordinary stop would be a deliberate leave
        /// and the slot would not be held.
        /// </remarks>
        internal PersistedReconnectCredentials DropClient(int index, int playerId)
        {
            var client = Clients[index];
            var link   = links[index];

            // What the client itself persisted at game start, not a reconstruction: the session
            // magic is the server's and is never exposed, so fabricating credentials here would be
            // testing against a value the real flow does not have.
            var credentials = Stores[index].Load();
            Assert.IsNotNull(credentials, "the client never persisted its reconnect credentials");
            Assert.AreEqual(playerId, credentials.LocalPlayerId,
                "the persisted credentials name a different player than the client holds");

            client.Stop(keepReconnectCredentials: true);
            link.Disconnect();
            live.Remove(client);
            return credentials;
        }

        /// <summary>Bring a dropped client back, failing the test if the server refuses.</summary>
        internal FrifloKlothoClient Reconnect(PersistedReconnectCredentials credentials, SimulationConfig localConfig = null)
        {
            var client = TryReconnect(credentials, out var failure, localConfig);
            if (client == null) {
                Assert.Fail($"the reconnect was refused: {failure?.Message}");
            }
            return client;
        }

        /// <summary>
        /// Attempt a reconnect, returning null and the reason if the server refuses.
        /// </summary>
        /// <remarks>
        /// The same two-phase shape as a late join, for the same reason: the server's reply carries
        /// the world and the roster, and the engine has to be constructed against them. Only the
        /// handshake differs — this one presents credentials naming a slot the server is holding.
        /// </remarks>
        internal FrifloKlothoClient TryReconnect(PersistedReconnectCredentials credentials, out Exception failure,
            SimulationConfig localConfig = null)
        {
            var link = hub.CreateClient();

            ConnectionResult result   = null;
            Exception        captured = null;
            var connection = KlothoConnection.Reconnect(
                link, credentials,
                onCompleted: r => result   = r,
                onFailed:    e => captured = e);
            // Only once the handshake's handlers are attached.
            hub.Connect(link);
            links.Add(link);

            const int MaxHandshakeFrames = 2000;
            for (int frame = 0; frame < MaxHandshakeFrames && !connection.IsCompleted; frame++) {
                connection.Update();
                Pump();
                System.Threading.Thread.Sleep(1);
            }

            failure = captured;
            if (result == null) {
                return null;
            }
            Assert.AreEqual(JoinKind.Reconnect, result.Kind,
                "the server treated this as a fresh join - the reconnect path was not exercised");

            var client = FrifloKlothoClient.JoinRunningMatch(
                result, new EntityStore(), localConfig ?? NewSimulationConfig(), NewSessionConfig(),
                new Callbacks(), RegisterCommands);
            live.Add(client);
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
            for (int i = 0; i < live.Count; i++) {
                live[i].Update(TickIntervalMs / 1000f);
            }
        }

        /// <summary>
        /// Pump until <paramref name="condition"/> holds, advancing real time because the handshake,
        /// the countdown and the reconnect window are all wall-clock driven.
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
            Assert.Fail($"{failureMessage} (server: phase={Server.Phase} state={Server.State} " +
                        $"players={Server.PlayerCount} tick={Server.Simulation.CurrentTick})");
        }

        public void Dispose()
        {
            foreach (var client in live) {
                client.Stop();
            }
            Server.Stop();
        }
    }

    #endregion
}
