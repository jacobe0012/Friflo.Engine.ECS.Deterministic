using System;
using System.Collections.Generic;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using Klotho.Friflo;
using NUnit.Framework;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Input;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Serialization;

// ReSharper disable once CheckNamespace
namespace Klotho.Friflo.Tests;

/// <summary>Completing a handshake must not create a receive gap before the session is constructed.</summary>
public static class Test_ConnectionHandoff
{
    private const int AddCommandId = 211;

    private struct HandoffState : IComponent
    {
        public int value;
    }

    private sealed class AddCommand : CommandBase
    {
        public int amount;
        public override int CommandTypeId => AddCommandId;
        public override int GetSerializedSize() => base.GetSerializedSize() + 4;
        protected override void SerializeData(ref SpanWriter writer) => writer.WriteInt32(amount);
        protected override void DeserializeData(ref SpanReader reader) => amount = reader.ReadInt32();
    }

    private sealed class IncrementSystem : QuerySystem<HandoffState>
    {
        protected override void OnUpdate() => Query.ForEachEntity((ref HandoffState state, Entity _) => state.value++);
    }

    private sealed class Callbacks : IFrifloSimulationCallbacks, IFrifloCommandApplier
    {
        public void RegisterSystems(SystemRoot systems, EntityStore store) => systems.Add(new IncrementSystem());
        public void OnInitializeWorld(EntityStore store, int randomSeed) => Assert.Fail("an SD joiner must restore its world");
        public void OnPollInput(int playerId, int tick, IFrifloCommandSink sink) { }
        public void ApplyCommands(EntityStore store, List<ICommand> commands, int tick) => Apply(store, commands);
    }

    /// <summary>The same PollEvents delivers FullState and the next verified inputs before adoption.</summary>
    [TestCase(JoinKind.Reconnect, false)]
    [TestCase(JoinKind.Reconnect, true)]
    [TestCase(JoinKind.LateJoin, false)]
    [TestCase(JoinKind.LateJoin, true)]
    public static void RunningMatch_PreservesVerifiedInputsAcrossHandoff(JoinKind kind, bool createInline)
    {
        var store = new EntityStore();
        store.CreateEntity(new HandoffState());
        var systems = new SystemRoot(store) { new IncrementSystem() };
        var source = new FrifloSimulation(store, systems, (s, commands, _) => Apply(s, commands));
        for (int tick = 0; tick < 20; tick++) source.Tick(new List<ICommand>());
        var (snapshot, snapshotHash) = source.SerializeFullStateWithHash();
        var transport = new ScriptedTransport();
        var serializer = new MessageSerializer();
        var config = NewConfig();
        var configMessage = new SimulationConfigMessage();
        configMessage.CopyFrom(config);
        transport.Queue(serializer.Serialize(configMessage));
        var roster = new RosterEntry { PlayerId = 1, ConnectionState = (byte)PlayerConnectionState.Connected };
        if (kind == JoinKind.Reconnect) {
            var accept = new ReconnectAcceptMessage {
                PlayerId = 1, CurrentTick = 20,
                SharedEpoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                PlayerCount = 1, MaxPlayers = 1, MinPlayers = 1,
            };
            accept.Roster.Add(roster);
            transport.Queue(serializer.Serialize(accept));
        } else {
            var accept = new LateJoinAcceptMessage {
                PlayerId = 1, CurrentTick = 20, Magic = 123,
                SharedEpoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                PlayerCount = 1, MaxPlayers = 1, MinPlayers = 1,
            };
            accept.Roster.Add(roster);
            transport.Queue(serializer.Serialize(accept));
        }
        transport.Queue(serializer.Serialize(new FullStateResponseMessage {
            Tick = 20, StateData = snapshot, StateHash = snapshotHash,
        }));
        var commandFactory = new CommandFactory();
        RegisterCommands(commandFactory);
        var expected = new Dictionary<int, long>();
        for (int tick = 20; tick < 22; tick++) {
            var commands = new List<ICommand> { new AddCommand { PlayerId = 1, Tick = tick, amount = tick + 1 } };
            source.Tick(commands);
            long hash = source.GetStateHash();
            expected[tick] = hash;
            var bytes = new byte[commandFactory.GetSerializedCommandsSize(commands)];
            int length = commandFactory.SerializeCommandsTo(bytes);
            transport.Queue(serializer.Serialize(new VerifiedStateMessage {
                Tick = tick + 1, StateHash = hash, ConfirmedInputsData = bytes, ConfirmedInputsDataLength = length,
            }));
        }

        ConnectionResult result = null;
        Exception failure = null;
        var credentials = new PersistedReconnectCredentials {
            LocalPlayerId = 1, SessionMagic = 123, RemoteAddress = "scripted", RemotePort = 1,
        };
        FrifloKlothoClient client = null;
        FrifloKlothoClient CreateClient(ConnectionResult completed) =>
            FrifloKlothoClient.JoinRunningMatch(completed, new EntityStore(), config,
                new SessionConfig { MaxPlayers = 1, MinPlayers = 1 }, new Callbacks(), RegisterCommands);
        Action<ConnectionResult> completed = r => {
            result = r;
            if (createInline) client = CreateClient(r);
        };
        var connection = kind == JoinKind.Reconnect
            ? KlothoConnection.Reconnect(transport, credentials, completed, error => failure = error)
            : KlothoConnection.Connect(transport, "scripted", 1, completed, error => failure = error);
        try {
            connection.Update();
            Assert.IsNull(failure);
            Assert.IsNotNull(result);
            Assert.AreEqual(5, transport.Delivered, "fixture must deliver both verified packets in the completing poll");
            // Both documented two-phase use and immediate callback adoption must preserve packets.
            connection.Dispose();
            if (client == null) client = CreateClient(result);
            var observed = new Dictionary<int, long>();
            client.Engine.OnTickExecutedWithState += (tick, state) => {
                if (state == FrameState.Verified) {
                    Assert.IsFalse(observed.ContainsKey(tick), "buffered verified input was delivered twice");
                    observed.Add(tick, client.Simulation.GetStateHash());
                }
            };

            client.Update(0);

            Assert.AreEqual(2, observed.Count,
                "verified inputs following FullState were lost between handshake completion and session adoption");
            foreach (var pair in expected) {
                Assert.IsTrue(observed.TryGetValue(pair.Key, out long actual), "missing verified execution tick " + pair.Key);
                Assert.AreEqual(pair.Value, actual, "buffer reuse or wrong replay order changed tick " + pair.Key);
            }
            Assert.AreEqual(0, client.ResyncCount, "a corrective FullState must not conceal lost input");
        }
        finally {
            connection.Dispose();
            client?.Stop();
            transport.Disconnect();
        }
    }

    /// <summary>Adoption must not replay before listeners are ready or let a later raw poll overtake pending data.</summary>
    [TestCase(NetworkMode.ServerDriven)]
    [TestCase(NetworkMode.P2P)]
    public static void NormalJoin_ReplaysOnceInReceiveOrderAfterExternalPoll(NetworkMode mode)
    {
        var transport = new ScriptedTransport();
        using var result = CompleteNormal(transport, mode);
        var service = NewService(mode);
        var transitions = new List<PlayerConnectionState>();
        try {
            Initialize(service, result);
            service.OnPlayerDisconnected += player => transitions.Add(player.ConnectionState);
            service.OnPlayerReconnected += player => transitions.Add(player.ConnectionState);
            Assert.AreEqual(PlayerConnectionState.Connected, service.Players[0].ConnectionState,
                "pending receives were replayed before the service's first update");
            Assert.AreSame(transport, result.Transport, "handoff must retain the caller's transport identity");
            // The result no longer owns the pending receives once a service has adopted them.
            result.Dispose();
            result.Dispose();
            transport.Queue(new MessageSerializer().Serialize(new PlayerStateNotificationMessage {
                PlayerId = 1, State = (byte)PlayerStateChange.Reconnected,
            }));
            transport.PollEvents();
            Assert.IsEmpty(transitions, "external polling must continue to queue until the first service update");

            service.Update();
            service.Update();

            CollectionAssert.AreEqual(new[] { PlayerConnectionState.Disconnected, PlayerConnectionState.Connected }, transitions,
                "pending and newly polled receives must be delivered in order, exactly once");
            Assert.AreEqual(PlayerConnectionState.Connected, service.Players[0].ConnectionState);
        }
        finally {
            LeaveIfInitialized(service);
            transport.Disconnect();
        }
    }

    [TestCase(NetworkMode.ServerDriven)]
    [TestCase(NetworkMode.P2P)]
    public static void DisposedUnadoptedResult_ReleasesPendingReceivesWithoutDisconnectingTransport(NetworkMode mode)
    {
        var transport = new ScriptedTransport();
        var result = CompleteNormal(transport, mode);
        var service = NewService(mode);
        try {
            result.Dispose();
            result.Dispose();
            Assert.IsTrue(transport.IsConnected, "abandoning a result must not close the caller-owned transport");
            transport.Queue(new byte[4 * 1024 * 1024 + 1]);
            Assert.DoesNotThrow(transport.PollEvents);
            Assert.Throws<ObjectDisposedException>(() => Initialize(service, result));
        }
        finally {
            LeaveIfInitialized(service);
            transport.Disconnect();
        }
    }

    [TestCase(NetworkMode.ServerDriven)]
    [TestCase(NetworkMode.P2P)]
    public static void ConnectionResult_CannotBeAdoptedTwice(NetworkMode mode)
    {
        var transport = new ScriptedTransport();
        using var result = CompleteNormal(transport, mode);
        var first = NewService(mode);
        var second = NewService(mode);
        try {
            Initialize(first, result);
            Assert.Throws<InvalidOperationException>(() => Initialize(second, result));
            first.Update();
            Assert.AreEqual(PlayerConnectionState.Disconnected, first.Players[0].ConnectionState,
                "a rejected second adoption must not steal the first service's pending data");
        }
        finally {
            LeaveIfInitialized(first);
            LeaveIfInitialized(second);
            transport.Disconnect();
        }
    }

    [TestCase(NetworkMode.ServerDriven, false)]
    [TestCase(NetworkMode.ServerDriven, true)]
    [TestCase(NetworkMode.P2P, false)]
    [TestCase(NetworkMode.P2P, true)]
    public static void PendingMessageLimit_FailsBeforeAdoption(NetworkMode mode, bool exceedBytes)
    {
        var transport = new ScriptedTransport();
        using var result = CompleteNormal(transport, mode);
        var service = NewService(mode);
        try {
            ExceedPendingLimit(transport, exceedBytes);
            Assert.Throws<InvalidOperationException>(() => Initialize(service, result),
                "exceeding a handoff limit must fail explicitly rather than adopt a truncated stream");
        }
        finally {
            LeaveIfInitialized(service);
            transport.Disconnect();
        }
    }

    [TestCase(NetworkMode.ServerDriven, false)]
    [TestCase(NetworkMode.ServerDriven, true)]
    [TestCase(NetworkMode.P2P, false)]
    [TestCase(NetworkMode.P2P, true)]
    public static void PendingMessageLimit_FailsClosedAfterAdoption(NetworkMode mode, bool exceedBytes)
    {
        var transport = new ScriptedTransport();
        using var result = CompleteNormal(transport, mode);
        var service = NewService(mode);
        try {
            Initialize(service, result);
            ExceedPendingLimit(transport, exceedBytes);
            Assert.Throws<InvalidOperationException>(service.Update);
            Assert.Throws<InvalidOperationException>(service.Update,
                "a second Update must not silently resume after dropping pending input");
        }
        finally {
            LeaveIfInitialized(service);
            transport.Disconnect();
        }
    }

    [TestCase(NetworkMode.ServerDriven, false)]
    [TestCase(NetworkMode.ServerDriven, true)]
    [TestCase(NetworkMode.P2P, false)]
    [TestCase(NetworkMode.P2P, true)]
    public static void DisconnectDuringHandoff_IsNotAcceptedAsACompleteConnection(NetworkMode mode, bool adoptFirst)
    {
        var transport = new ScriptedTransport();
        using var result = CompleteNormal(transport, mode);
        var service = NewService(mode);
        try {
            if (adoptFirst) Initialize(service, result);
            transport.Disconnect();
            if (adoptFirst) {
                Assert.Throws<InvalidOperationException>(service.Update);
                Assert.Throws<InvalidOperationException>(service.Update);
            } else {
                Assert.Throws<InvalidOperationException>(() => Initialize(service, result));
            }
        }
        finally {
            LeaveIfInitialized(service);
            transport.Disconnect();
        }
    }

    [TestCase(NetworkMode.ServerDriven)]
    [TestCase(NetworkMode.P2P)]
    public static void LeaveBeforeFirstUpdate_DiscardsPendingDelivery(NetworkMode mode)
    {
        var transport = new ScriptedTransport();
        using var result = CompleteNormal(transport, mode);
        var service = NewService(mode);
        int transitions = 0;
        try {
            Initialize(service, result);
            service.OnPlayerDisconnected += _ => transitions++;
            LeaveIfInitialized(service);
            transport.Queue(new MessageSerializer().Serialize(new PlayerStateNotificationMessage {
                PlayerId = 1, State = (byte)PlayerStateChange.Disconnected,
            }));
            transport.PollEvents();
            Assert.AreEqual(0, transitions, "leaving the service must detach both pending and live receives");
        }
        finally {
            LeaveIfInitialized(service);
            transport.Disconnect();
        }
    }

    [TestCase(NetworkMode.ServerDriven)]
    [TestCase(NetworkMode.P2P)]
    public static void InitializationCallbackFailure_LeavesResultAbleToReleasePendingReceives(NetworkMode mode)
    {
        var transport = new ScriptedTransport();
        using var result = CompleteNormal(transport, mode);
        var service = NewService(mode);
        service.OnPhaseChanged += phase => {
            if (phase == SessionPhase.Synchronized) throw new InvalidOperationException("application callback failed");
        };
        try {
            Assert.Throws<InvalidOperationException>(() => Initialize(service, result));

            result.Dispose();

            Assert.AreEqual(0, transport.DataReceivers,
                "failed service initialization took pending-data ownership before its callbacks could throw");
            Assert.IsTrue(transport.IsConnected, "cleanup must not disconnect the caller-owned transport");
        }
        finally {
            LeaveIfInitialized(service);
            transport.Disconnect();
        }
    }

    [TestCase(NetworkMode.ServerDriven, false)]
    [TestCase(NetworkMode.ServerDriven, true)]
    [TestCase(NetworkMode.P2P, false)]
    [TestCase(NetworkMode.P2P, true)]
    public static void CompletionCallbackFailure_ReleasesOnlyUnadoptedReceives(NetworkMode mode, bool adoptBeforeThrow)
    {
        var transport = new ScriptedTransport();
        var service = NewService(mode);
        var expected = new InvalidOperationException("application completion callback failed");
        ConnectionResult result = null;
        try {
            var error = Assert.Throws<InvalidOperationException>(() => CompleteNormal(transport, mode, completed => {
                result = completed;
                if (adoptBeforeThrow) Initialize(service, completed);
                transport.PollEvents();
                throw expected;
            }));
            Assert.AreSame(expected, error);
            Assert.IsTrue(transport.IsConnected, "a callback exception must not close caller-owned transport");
            if (adoptBeforeThrow) {
                service.Update();
                Assert.AreEqual(PlayerConnectionState.Disconnected, service.Players[0].ConnectionState,
                    "callback failure disposed receives that had already transferred to the service");
            } else {
                Assert.AreEqual(0, transport.DataReceivers, "callback failure leaked an unclaimed handoff listener");
                Assert.Throws<ObjectDisposedException>(() => Initialize(service, result));
            }
        }
        finally {
            result?.Dispose();
            LeaveIfInitialized(service);
            transport.Disconnect();
        }
    }

    private static ConnectionResult CompleteNormal(ScriptedTransport transport, NetworkMode mode,
        Action<ConnectionResult> afterCompletion = null)
    {
        var serializer = new MessageSerializer();
        var sync = new SyncCompleteMessage {
            Magic = 123, PlayerId = 1, SharedEpoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        sync.Roster.Add(new RosterEntry { PlayerId = 1, ConnectionState = (byte)PlayerConnectionState.Connected });
        transport.Queue(serializer.Serialize(sync));
        var config = NewConfig();
        config.Mode = mode;
        var configMessage = new SimulationConfigMessage();
        configMessage.CopyFrom(config);
        transport.Queue(serializer.Serialize(configMessage));
        transport.Queue(serializer.Serialize(new PlayerStateNotificationMessage {
            PlayerId = 1, State = (byte)PlayerStateChange.Disconnected,
        }));
        ConnectionResult result = null;
        Exception failure = null;
        var connection = KlothoConnection.Connect(transport, "scripted", 1, r => {
            result = r;
            afterCompletion?.Invoke(r);
        }, error => failure = error);
        try {
            connection.Update();
            Assert.IsNull(failure);
            Assert.IsNotNull(result, "the scripted normal handshake did not complete");
            return result;
        }
        finally {
            connection.Dispose();
        }
    }

    private static IKlothoNetworkService NewService(NetworkMode mode) =>
        mode == NetworkMode.ServerDriven ? new ServerDrivenClientService() : new KlothoNetworkService();

    private static void Initialize(IKlothoNetworkService service, ConnectionResult result)
    {
        if (service is ServerDrivenClientService sd) sd.InitializeFromConnection(result, new CommandFactory(), null);
        else ((KlothoNetworkService)service).InitializeFromConnection(result, new CommandFactory(), null);
    }

    private static void LeaveIfInitialized(IKlothoNetworkService service)
    {
        if (service.Phase != SessionPhase.None) service.LeaveRoom();
    }

    private static void ExceedPendingLimit(ScriptedTransport transport, bool exceedBytes)
    {
        if (exceedBytes) {
            transport.Queue(new byte[4 * 1024 * 1024 + 1]);
        } else {
            var packet = new MessageSerializer().Serialize(new PlayerStateNotificationMessage {
                PlayerId = 1, State = (byte)PlayerStateChange.Reconnected,
            });
            for (int i = 0; i < 1025; i++) transport.Queue(packet);
        }
        transport.PollEvents();
    }

    private static void Apply(EntityStore store, List<ICommand> commands)
    {
        foreach (var command in commands) {
            if (command is AddCommand add) {
                store.Query<HandoffState>().ForEachEntity((ref HandoffState state, Entity _) => state.value += add.amount);
            }
        }
    }

    private static void RegisterCommands(CommandFactory factory) => factory.RegisterCommand<AddCommand>(AddCommandId);

    private static SimulationConfig NewConfig() => new() {
        Mode = NetworkMode.ServerDriven, TickIntervalMs = 20, InputDelayTicks = 2,
        MaxRollbackTicks = 30, CatchupMaxTicksPerFrame = 200, ReactiveMax = 10,
    };

    /// <summary>Public transport seam: ordered packets share and overwrite one callback buffer.</summary>
    private sealed class ScriptedTransport : INetworkTransport
    {
        private readonly Queue<byte[]> incoming = new();
        private byte[] callbackBuffer = new byte[16384];
        internal int Delivered;
        internal int DataReceivers => OnDataReceived?.GetInvocationList().Length ?? 0;
        internal void Queue(byte[] packet) => incoming.Enqueue(packet);
        public bool IsConnected { get; private set; }
        public int LocalPeerId => 1;
        public string RemoteAddress => "scripted";
        public int RemotePort => 1;
        public int LastDisconnectPayload => -1;
        public bool Connect(string address, int port) { IsConnected = true; OnConnected?.Invoke(); return true; }
        public bool Listen(string address, int port, int maxConnections) => throw new NotSupportedException();
        public void Disconnect() { if (!IsConnected) return; IsConnected = false; OnDisconnected?.Invoke(DisconnectReason.LocalDisconnect); }
        public void DisconnectPeer(int peerId) { }
        public void DisconnectPeer(int peerId, byte[] data) { }
        public IEnumerable<int> GetConnectedPeerIds() => Array.Empty<int>();
        public void Send(int peerId, byte[] data, DeliveryMethod method) { }
        public void Send(int peerId, byte[] data, int length, DeliveryMethod method) { }
        public void Broadcast(byte[] data, DeliveryMethod method) { }
        public void Broadcast(byte[] data, int length, DeliveryMethod method) { }
        public void FlushSendQueue() { }
        public void PollEvents()
        {
            while (incoming.Count > 0) {
                var packet = incoming.Dequeue();
                if (packet.Length > callbackBuffer.Length) Array.Resize(ref callbackBuffer, packet.Length);
                Array.Copy(packet, callbackBuffer, packet.Length);
                Delivered++;
                OnDataReceived?.Invoke(0, callbackBuffer, packet.Length);
                Array.Fill(callbackBuffer, (byte)0xCD);
            }
        }
        public event Action<int, byte[], int> OnDataReceived;
        public event Action OnConnected;
        public event Action<DisconnectReason> OnDisconnected;
#pragma warning disable CS0067
        public event Action<int> OnPeerConnected;
        public event Action<int> OnPeerDisconnected;
#pragma warning restore CS0067
    }
}
