#if DEVELOPMENT_BUILD || UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Threading;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using Klotho.Friflo;
using NUnit.Framework;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Network;

// ReSharper disable once CheckNamespace
namespace Klotho.Friflo.Tests;

/// <summary>Development diagnostics must not turn a persistent delayed peer into per-tick log spam.</summary>
public static class Test_DiagnosticLogging
{
    private sealed class CaptureLogger : IKLogger
    {
        internal readonly List<string> Breaks = new();
        internal int Dumps;

        public bool IsEnabled(KLogLevel level) => true;

        public void Log(KLogLevel level, string message, Exception exception)
        {
            if (message.Contains("[ChainBreak]")) Breaks.Add(message);
            if (message.Contains("[DumpTickRange]")) Dumps++;
        }
    }

    private sealed class Callbacks : IFrifloSimulationCallbacks
    {
        public void RegisterSystems(SystemRoot systems, EntityStore store) { }
        public void OnInitializeWorld(EntityStore store, int randomSeed) { }
        public void OnPollInput(int playerId, int tick, IFrifloCommandSink sink) { }
    }

    [Test]
    public static void AdvancingFrontier_ThrottlesTheSameMissingPeerAndReportsSuppression()
    {
        var (hostTransport, guestTransport) = LoopbackTransport.CreatePair();
        var logger = new CaptureLogger();
        var simulationConfig = new SimulationConfig {
            Mode = NetworkMode.P2P,
            TickIntervalMs = 10,
            InputDelayTicks = 2,
            MaxRollbackTicks = 100,
            SyncCheckInterval = 10,
            QuorumMissDropTicks = 1000,
        };
        var sessionConfig = new SessionConfig { MaxPlayers = 2, MinPlayers = 2, CountdownDurationMs = 1 };
        using var host = FrifloKlothoSession.CreateHost(
            new EntityStore(), hostTransport, simulationConfig, sessionConfig, new Callbacks(), logger: logger);
        FrifloKlothoSession guest = null;
        KlothoConnection connection = null;
        try {
            ConnectionResult result = null;
            Exception failure = null;
            connection = KlothoConnection.Connect(guestTransport, "loopback", 0,
                r => result = r, error => failure = error);
            LoopbackTransport.Connect(hostTransport, guestTransport);
            for (int frame = 0; frame < 2000 && result == null && failure == null; frame++) {
                connection.Update();
                host.Update(0.01f);
                Thread.Sleep(1);
            }
            Assert.IsNull(failure, $"guest handshake failed: {failure}");
            Assert.IsNotNull(result, "guest handshake never completed");
            guest = FrifloKlothoSession.CreateGuest(
                new EntityStore(), result, new SessionConfig(), new Callbacks());
            PumpUntil(host, guest, () => host.Phase == SessionPhase.Synchronized && guest.Phase == SessionPhase.Synchronized);
            host.SetReady();
            guest.SetReady();
            PumpUntil(host, guest, () => host.State == KlothoState.Running && guest.State == KlothoState.Running);

            for (int frame = 0; frame < 20; frame++) Pump(host, guest);
            logger.Breaks.Clear();
            int dumpsBefore = logger.Dumps;
            var stalledFrontiers = new HashSet<int>();
            host.Engine.OnChainAdvanceBreak += () => stalledFrontiers.Add(host.Engine.LastVerifiedTick + 1);
            hostTransport.LatencyPolls = 12;
            guestTransport.LatencyPolls = 12;
            for (int frame = 0; frame < 160; frame++) Pump(host, guest);

            Assert.Greater(stalledFrontiers.Count, 10,
                "the frontier did not advance while waiting on the same delayed player");
            Assert.Less(logger.Breaks.Count, stalledFrontiers.Count / 2,
                "a persistent missing peer was logged once for every new stalled tick");
            Assert.LessOrEqual(logger.Dumps - dumpsBefore, 1,
                "the diagnostic buffer dump must remain single-shot");

            // The summary must retain how many frontier breaks were hidden during the interval.
            Thread.Sleep(1100);
            for (int frame = 0; frame < 20; frame++) Pump(host, guest);
            Assert.IsTrue(logger.Breaks.Exists(message => message.Contains("suppressed since tick=")),
                "throttling lost the number of repeated stalls instead of reporting it on the next log");
            Assert.Greater(host.Engine.LastVerifiedTick, 10, "diagnostic throttling must not stop verification");
        }
        finally {
            connection?.Dispose();
            guest?.Stop();
        }
    }

    private static void Pump(FrifloKlothoSession host, FrifloKlothoSession guest)
    {
        host.Update(0.01f);
        guest.Update(0.01f);
    }

    private static void PumpUntil(FrifloKlothoSession host, FrifloKlothoSession guest, Func<bool> condition)
    {
        for (int frame = 0; frame < 2000; frame++) {
            if (condition()) return;
            Pump(host, guest);
            Thread.Sleep(1);
        }
        Assert.Fail($"peers did not reach the requested phase: {host.Phase}/{guest.Phase}");
    }
}
#endif
