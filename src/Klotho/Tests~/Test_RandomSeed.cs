using System;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using NUnit.Framework;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;

// ReSharper disable once CheckNamespace
namespace Klotho.Friflo.Tests;

public static class Test_RandomSeed
{
    private sealed class Callbacks : IFrifloSimulationCallbacks
    {
        internal int? Seed;
        public void RegisterSystems(SystemRoot systems, EntityStore store) { }
        public void OnInitializeWorld(EntityStore store, int randomSeed)
        {
            Seed = randomSeed;
            store.CreateEntity(new SimClock { tick = randomSeed });
        }
        public void OnPollInput(int playerId, int tick, IFrifloCommandSink sink) { }
    }

    /// <summary>The authored seed must reach both world initializers, not merely remain on the config.</summary>
    [TestCase(12345)]
    [TestCase(-12345)]
    public static void P2P_UsesAuthoredSeedForBothPeers(int seed)
    {
        var config = NewSessionConfig(seed, 2);
        Assert.AreEqual(seed, RunP2P(config));
    }

    /// <summary>Reusing an automatic-seed request must not freeze the first match's effective seed.</summary>
    [Test]
    public static void P2P_AutomaticSeedLeavesReusedConfigAuthored()
    {
        var config = NewSessionConfig(0, 2);
        for (int match = 0; match < 2; match++) {
            int before = Environment.TickCount;
            int effective = RunP2P(config);
            int after = Environment.TickCount;
            Assert.LessOrEqual(unchecked((uint)(effective - before)), unchecked((uint)(after - before)),
                "an automatic seed must be chosen when this match starts");
            Assert.AreEqual(0, config.RandomSeed,
                "the effective seed must not overwrite the authored request for the next match");
        }
    }

    /// <summary>Server-driven clients must receive the world initialized from the authored server seed.</summary>
    [TestCase(12345)]
    [TestCase(-12345)]
    public static void ServerDriven_UsesAuthoredSeedForTransferredWorld(int seed)
    {
        var config = NewSessionConfig(seed, 1);
        Assert.AreEqual(seed, RunServerDriven(config));
    }

    /// <summary>Automatic authority seeds are picked per match without rewriting a reusable request.</summary>
    [Test]
    public static void ServerDriven_AutomaticSeedLeavesReusedConfigAuthored()
    {
        var config = NewSessionConfig(0, 1);
        for (int match = 0; match < 2; match++) {
            int before = Environment.TickCount;
            int effective = RunServerDriven(config);
            int after = Environment.TickCount;
            Assert.LessOrEqual(unchecked((uint)(effective - before)), unchecked((uint)(after - before)),
                "an automatic seed must be chosen when this match starts");
            Assert.AreEqual(0, config.RandomSeed,
                "the server must not replace the authored request with this match's seed");
        }
    }

    private static int RunServerDriven(SessionConfig config)
    {
        var hub = new LoopbackHub();
        var serverLink = hub.CreateServer();
        var clientLink = hub.CreateClient();
        var serverCallbacks = new Callbacks();
        var clientCallbacks = new Callbacks();
        using var server = FrifloKlothoServer.Create(
            new EntityStore(), serverLink, NewSimulationConfig(NetworkMode.ServerDriven), config, serverCallbacks);
        using var client = FrifloKlothoClient.Connect(
            new EntityStore(), clientLink, NewSimulationConfig(NetworkMode.ServerDriven),
            NewSessionConfig(0, 1), clientCallbacks);
        hub.Connect(clientLink);
        void Pump()
        {
            serverLink.PollEvents();
            clientLink.PollEvents();
            server.Update(0.01f);
            client.Update(0.01f);
        }
        PumpUntil(Pump, () => client.Phase == SessionPhase.Synchronized,
            "the server-driven configuration handshake did not complete");
        client.SetReady();
        PumpUntil(Pump, () => client.HasWorld && client.State == KlothoState.Running &&
                              server.State == KlothoState.Running, "the server-driven match did not start");

        int effective = server.NetworkService.RandomSeed;
        Assert.AreEqual(effective, client.NetworkService.RandomSeed);
        Assert.AreEqual(effective, server.Engine.RandomSeed);
        Assert.AreEqual(effective, client.Engine.RandomSeed);
        Assert.AreEqual(effective, serverCallbacks.Seed);
        Assert.IsNull(clientCallbacks.Seed, "the client must receive the seeded FullState, not create its own world");
        AssertWorldSeed(server.Store, effective);
        AssertWorldSeed(client.Store, effective);
        Assert.AreEqual(server.Simulation.GetStateHash(), client.Simulation.GetStateHash());
        Assert.AreEqual(0, client.ResyncCount);
        return effective;
    }

    private static int RunP2P(SessionConfig config)
    {
        var (hostLink, guestLink) = LoopbackTransport.CreatePair();
        var hostCallbacks = new Callbacks();
        using var host = FrifloKlothoSession.CreateHost(
            new EntityStore(), hostLink, NewSimulationConfig(NetworkMode.P2P), config, hostCallbacks);
        ConnectionResult result = null;
        Exception failure = null;
        var connection = KlothoConnection.Connect(
            guestLink, "loopback", 0, r => result = r, e => failure = e);
        try {
            LoopbackTransport.Connect(hostLink, guestLink);
            PumpUntil(() => {
                connection.Update();
                hostLink.PollEvents();
                host.Update(0.01f);
            }, () => result != null || failure != null, "the P2P configuration handshake did not complete");
            Assert.IsNull(failure);
            var guestCallbacks = new Callbacks();
            using var guest = FrifloKlothoSession.CreateGuest(
                new EntityStore(), result, NewSessionConfig(0, 2), guestCallbacks);
            void Pump()
            {
                hostLink.PollEvents();
                guestLink.PollEvents();
                host.Update(0.01f);
                guest.Update(0.01f);
            }
            PumpUntil(Pump, () => host.Phase == SessionPhase.Synchronized &&
                                  guest.Phase == SessionPhase.Synchronized, "the P2P roster did not synchronize");
            host.SetReady();
            guest.SetReady();
            PumpUntil(Pump, () => host.State == KlothoState.Running && guest.State == KlothoState.Running,
                "the P2P match did not start");

            int effective = host.NetworkService.RandomSeed;
            Assert.AreEqual(effective, guest.NetworkService.RandomSeed);
            Assert.AreEqual(effective, host.Engine.RandomSeed);
            Assert.AreEqual(effective, guest.Engine.RandomSeed);
            Assert.AreEqual(effective, hostCallbacks.Seed);
            Assert.AreEqual(effective, guestCallbacks.Seed);
            AssertWorldSeed(host.Store, effective);
            AssertWorldSeed(guest.Store, effective);
            Assert.AreEqual(host.Simulation.GetStateHash(), guest.Simulation.GetStateHash());
            return effective;
        }
        finally {
            connection.Dispose();
        }
    }

    private static void AssertWorldSeed(EntityStore store, int seed)
    {
        int count = 0;
        store.Query<SimClock>().ForEachEntity((ref SimClock clock, Entity _) => {
            Assert.AreEqual(seed, clock.tick);
            count++;
        });
        Assert.AreEqual(1, count, "the seed must actually initialize the simulation world");
    }

    private static SimulationConfig NewSimulationConfig(NetworkMode mode) => new() {
        Mode = mode,
        TickIntervalMs = 10,
        InputDelayTicks = 2,
        MaxRollbackTicks = 30,
        SyncCheckInterval = 10,
        ReactiveMax = 10,
    };

    private static SessionConfig NewSessionConfig(int seed, int players) => new() {
        RandomSeed = seed,
        MaxPlayers = players,
        MinPlayers = players,
        CountdownDurationMs = 1,
    };

    private static void PumpUntil(Action pump, Func<bool> condition, string message)
    {
        for (int frame = 0; frame < 2000; frame++) {
            if (condition()) return;
            pump();
            System.Threading.Thread.Sleep(1);
        }
        Assert.Fail(message);
    }
}
