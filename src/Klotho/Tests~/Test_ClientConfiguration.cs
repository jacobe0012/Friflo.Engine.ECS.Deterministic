using System;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using NUnit.Framework;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;

// ReSharper disable once CheckNamespace
namespace Klotho.Friflo.Tests;

public static class Test_ClientConfiguration
{
    private sealed class Callbacks : IFrifloSimulationCallbacks
    {
        public void RegisterSystems(SystemRoot systems, EntityStore store) { }
        public void OnInitializeWorld(EntityStore store, int randomSeed) => store.CreateEntity();
        public void OnPollInput(int playerId, int tick, IFrifloCommandSink sink) { }
    }

    [Test]
    public static void NormalConnect_UsesServerConfigBeforeCreatingEngineAndSnapshotRing()
    {
        var hub = new LoopbackHub();
        var serverLink = hub.CreateServer();
        var clientLink = hub.CreateClient();
        var authority = new SimulationConfig {
            Mode = NetworkMode.ServerDriven, TickIntervalMs = 20, InputDelayTicks = 4,
            MaxRollbackTicks = 40, ReactiveMax = 10,
        };
        var session = new SessionConfig { MaxPlayers = 1, MinPlayers = 1, CountdownDurationMs = 1 };
        using var server = FrifloKlothoServer.Create(
            new EntityStore(), serverLink, authority, session, new Callbacks());
        using var client = FrifloKlothoClient.Connect(
            new EntityStore(), clientLink,
            new SimulationConfig { Mode = NetworkMode.ServerDriven, TickIntervalMs = 10, InputDelayTicks = 2, MaxRollbackTicks = 8 },
            new SessionConfig(), new Callbacks());
        hub.Connect(clientLink);

        void Pump()
        {
            serverLink.PollEvents();
            clientLink.PollEvents();
            server.Update(0.02f);
            client.Update(0.02f);
        }
        PumpUntil(Pump, () => client.Phase == SessionPhase.Synchronized);

        Assert.AreEqual(20, client.Engine.TickInterval);
        Assert.AreEqual(4, client.Engine.InputDelay);
        Assert.AreEqual(40, client.Engine.SimulationConfig.MaxRollbackTicks);
        Assert.AreEqual(authority.GetSnapshotCapacity(), client.Simulation.RollbackCapacity,
            "authoritative rollback capacity must be used at construction, not patched after startup");
        client.SetReady();
        PumpUntil(Pump, () => client.HasWorld && client.State == KlothoState.Running);
        for (int frame = 0; frame < 30; frame++) Pump();
        Assert.AreEqual(0, client.ResyncCount);
    }

    [Test]
    public static void PendingConnect_ExposesLifecycleAndCanBeStoppedBeforeHandshake()
    {
        var hub = new LoopbackHub();
        hub.CreateServer();
        var link = hub.CreateClient();
        var store = new EntityStore();
        using var client = FrifloKlothoClient.Connect(
            store, link, new SimulationConfig { Mode = NetworkMode.ServerDriven },
            new SessionConfig(), new Callbacks());
        hub.Connect(link);

        Assert.AreSame(store, client.Store);
        Assert.AreEqual(SessionPhase.Syncing, client.Phase);
        Assert.AreEqual(KlothoState.Idle, client.State);
        Assert.AreEqual(-1, client.LocalPlayerId);
        Assert.AreEqual(-1, client.LastVerifiedTick);
        Assert.Throws<InvalidOperationException>(() => _ = client.Engine);
        Assert.Throws<InvalidOperationException>(() => _ = client.Simulation);
        Assert.Throws<InvalidOperationException>(() => client.SetReady());

        client.Stop();
        client.Stop();
        Assert.DoesNotThrow(() => client.Update(0.02f));
        Assert.AreEqual(SessionPhase.Disconnected, client.Phase);
        Assert.AreEqual(KlothoState.Finished, client.State);
        Assert.IsFalse(link.IsConnected, "stopping a pending handshake must also disconnect its transport");
        Assert.AreEqual(0, store.Count);
    }

    private sealed class CredentialsStore : IReconnectCredentialsStore
    {
        internal bool Cleared;
        public void Save(PersistedReconnectCredentials credentials) { }
        public PersistedReconnectCredentials Load() => null;
        public void Clear() => Cleared = true;
        public bool IsValid(PersistedReconnectCredentials credentials, long nowUnixMs, string appVersion) => false;
    }

    [TestCase(false)]
    [TestCase(true)]
    public static void PendingConnect_StopHonorsReconnectCredentialsPolicy(bool keepCredentials)
    {
        var hub = new LoopbackHub();
        hub.CreateServer();
        var link = hub.CreateClient();
        using var client = FrifloKlothoClient.Connect(
            new EntityStore(), link, new SimulationConfig { Mode = NetworkMode.ServerDriven },
            new SessionConfig(), new Callbacks());
        var credentials = new CredentialsStore();
        client.SetReconnectCredentialsStore(credentials, "test-build");

        client.Stop(keepCredentials);

        Assert.AreEqual(!keepCredentials, credentials.Cleared,
            "cancelling before handshake must retain the same credentials cleanup policy as stopping an initialized client");
    }

    private static void PumpUntil(Action pump, Func<bool> condition)
    {
        for (int frame = 0; frame < 2000; frame++) {
            if (condition()) return;
            pump();
            System.Threading.Thread.Sleep(1);
        }
        Assert.Fail("the client never reached the expected lifecycle state");
    }
}
