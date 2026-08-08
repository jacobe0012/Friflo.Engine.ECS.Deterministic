using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using Klotho.Friflo;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Serialization;

namespace Klotho.Friflo.Capacity;

/// <summary>
/// End-to-end capacity probe for the sequential Friflo server-driven room server. Clients run in
/// process to produce real handshakes, inputs, acknowledgements and verified-state traffic, but
/// only <see cref="FrifloKlothoRoomServer.Update"/> is inside the measured interval.
/// </summary>
internal static class Program
{
    private const int PlayersPerRoom = 2;
    private const int MoveCommandTypeId = 1901;

    private struct Body : IComponent
    {
        internal FP64 x, y, vx, vy;
    }

    private struct PlayerControl : IComponent
    {
        internal int playerId;
        internal int impulse;
    }

    private sealed class IntegrateSystem : QuerySystem<Body>
    {
        protected override void OnUpdate()
        {
            Query.ForEachEntity((ref Body body, Entity _) => {
                body.x += body.vx;
                body.y += body.vy;
                if (body.x > FP64.FromInt(10000) || body.x < FP64.FromInt(-10000)) body.vx = -body.vx;
                if (body.y > FP64.FromInt(10000) || body.y < FP64.FromInt(-10000)) body.vy = -body.vy;
            });
        }
    }

    private sealed class MoveCommand : CommandBase
    {
        internal int impulse;
        public override int CommandTypeId => MoveCommandTypeId;
        public override bool IsContinuousInput => true;
        public override int GetSerializedSize() => base.GetSerializedSize() + sizeof(int);
        protected override void SerializeData(ref SpanWriter writer) => writer.WriteInt32(impulse);
        protected override void DeserializeData(ref SpanReader reader) => impulse = reader.ReadInt32();
    }

    private sealed class Callbacks : IFrifloSimulationCallbacks, IFrifloCommandApplier
    {
        private readonly int roomId;
        private readonly int entityCount;

        internal Callbacks(int roomId, int entityCount)
        {
            this.roomId = roomId;
            this.entityCount = entityCount;
        }

        public void RegisterSystems(SystemRoot systems, EntityStore store) => systems.Add(new IntegrateSystem());

        public void OnInitializeWorld(EntityStore store, int randomSeed)
        {
            for (int player = 1; player <= PlayersPerRoom; player++) {
                store.CreateEntity(new PlayerControl { playerId = player });
            }
            for (int i = 0; i < entityCount; i++) {
                int seed = unchecked(randomSeed + roomId * 7919 + i * 104729);
                store.CreateEntity(new Body {
                    x  = FP64.FromInt((seed & 1023) - 512),
                    y  = FP64.FromInt(((seed >> 10) & 1023) - 512),
                    vx = FP64.FromInt((i % 5) + 1),
                    vy = FP64.FromInt(((i + roomId) % 7) + 1),
                });
            }
        }

        public void OnPollInput(int playerId, int tick, IFrifloCommandSink sink)
        {
            sink.Send(new MoveCommand { impulse = (tick * 17 + playerId * 31) & 7 });
        }

        public void ApplyCommands(EntityStore store, List<ICommand> commands, int tick)
        {
            for (int i = 0; i < commands.Count; i++) {
                if (commands[i] is not MoveCommand move) continue;
                int playerId = move.PlayerId;
                store.Query<PlayerControl>().ForEachEntity((ref PlayerControl control, Entity _) => {
                    if (control.playerId == playerId) control.impulse = move.impulse;
                });
            }
        }
    }

    private sealed class Match : IDisposable
    {
        private readonly int tickMs;
        private readonly FrifloKlothoClient[][] clients;
        private readonly List<LoopbackTransport> clientLinks = new();

        internal readonly FrifloKlothoRoomServer Server;

        internal Match(int roomCount, int entityCount, int tickMs)
        {
            this.tickMs = tickMs;
            var hub = new LoopbackHub();
            var serverLink = hub.CreateServer();
            Server = FrifloKlothoRoomServer.Create(
                serverLink,
                roomCount,
                new FrifloKlothoRoomServer.RoomSetup {
                    SimulationConfig = _ => NewSimulationConfig(tickMs),
                    SessionConfig = _ => NewSessionConfig(),
                    Callbacks = roomId => new Callbacks(roomId, entityCount),
                    RegisterCommands = RegisterCommands,
                });

            for (int room = 0; room < roomCount; room++) Server.CreateRoom(room);

            clients = new FrifloKlothoClient[roomCount][];
            for (int room = 0; room < roomCount; room++) {
                clients[room] = new FrifloKlothoClient[PlayersPerRoom];
                for (int seat = 0; seat < PlayersPerRoom; seat++) {
                    var link = hub.CreateClient();
                    clients[room][seat] = FrifloKlothoClient.Connect(
                        new EntityStore(), link, NewSimulationConfig(tickMs), NewSessionConfig(),
                        new Callbacks(room, entityCount), RegisterCommands, roomId: room);
                    hub.Connect(link);
                    clientLinks.Add(link);
                }
            }

            PumpUntil(() => clients.SelectMany(static r => r).All(static c => c.Phase == SessionPhase.Synchronized),
                "clients did not synchronize");
            foreach (var client in clients.SelectMany(static r => r)) client.SetReady();
            PumpUntil(
                () => Enumerable.Range(0, roomCount).All(room => Server.GetRoom(room).Engine.State == KlothoState.Running)
                      && clients.SelectMany(static r => r).All(static c => c.State == KlothoState.Running && c.HasWorld),
                "matches did not enter Running");
        }

        internal long PumpMeasured()
        {
            long start = Stopwatch.GetTimestamp();
            Server.Update(tickMs / 1000f);
            long elapsed = Stopwatch.GetTimestamp() - start;
            PumpClients();
            return elapsed;
        }

        internal void Pump()
        {
            Server.Update(tickMs / 1000f);
            PumpClients();
        }

        private void PumpClients()
        {
            for (int i = 0; i < clientLinks.Count; i++) clientLinks[i].PollEvents();
            foreach (var client in clients.SelectMany(static r => r)) client.Update(tickMs / 1000f);
        }

        private void PumpUntil(Func<bool> condition, string message)
        {
            for (int frame = 0; frame < 4000; frame++) {
                if (condition()) return;
                Pump();
                Thread.Sleep(1);
            }
            throw new InvalidOperationException(message);
        }

        public void Dispose()
        {
            foreach (var client in clients.SelectMany(static r => r)) client?.Stop();
            Server.Stop();
        }
    }

    private sealed record Options(int[] Rooms, int Entities, int TickMs, int Warmup, int Frames);

    private static int Main(string[] args)
    {
        try {
            var options = Parse(args);
            if (args.Any(static value => string.Equals(value, "--memory-only", StringComparison.OrdinalIgnoreCase))) {
                RunAuthoritativeMemory(options);
                return 0;
            }
            Console.WriteLine("kind,rooms,entities,tick_ms,warmup,frames,server_ticks_min,mean_ms,p50_ms,p95_ms,p99_ms,max_ms,managed_mb,working_set_mb,gc0,gc1,gc2");
            foreach (int rooms in options.Rooms) Run(rooms, options);
            return 0;
        }
        catch (Exception exception) {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    /// <summary>
    /// Measure only authoritative simulation storage. The end-to-end probe deliberately keeps two
    /// predictive clients per room in the same process; its working set is therefore useful for
    /// stress interference, but is not a server RAM figure.
    /// </summary>
    private static void RunAuthoritativeMemory(Options options)
    {
        Console.WriteLine("kind,rooms,entities,rollback_slots,warmup_ticks,managed_delta_mb,managed_mb_per_room");
        foreach (int roomCount in options.Rooms) {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long baseline = GC.GetTotalMemory(true);

            const int rollbackSlots = 52;
            var simulations = new FrifloSimulation[roomCount];
            for (int room = 0; room < roomCount; room++) {
                var store = new EntityStore();
                for (int player = 1; player <= PlayersPerRoom; player++) {
                    store.CreateEntity(new PlayerControl { playerId = player });
                }
                for (int i = 0; i < options.Entities; i++) {
                    store.CreateEntity(new Body {
                        x = FP64.FromInt(room + i),
                        y = FP64.FromInt(room - i),
                        vx = FP64.FromInt((i % 5) + 1),
                        vy = FP64.FromInt((i % 7) + 1),
                    });
                }
                var systems = new SystemRoot(store) { new IntegrateSystem() };
                simulations[room] = new FrifloSimulation(store, systems, rollbackCapacity: rollbackSlots);
            }

            for (int tick = 0; tick < options.Warmup; tick++) {
                for (int room = 0; room < simulations.Length; room++) {
                    simulations[room].SaveSnapshot();
                    simulations[room].Tick(null);
                    _ = simulations[room].GetStateHash();
                }
            }

            long current = GC.GetTotalMemory(true);
            double deltaMb = (current - baseline) / 1048576.0;
            Console.WriteLine(string.Join(",", "server-sim-memory", roomCount, options.Entities,
                rollbackSlots, options.Warmup, F(deltaMb), F(deltaMb / roomCount)));
            GC.KeepAlive(simulations);
        }
    }

    private static void Run(int roomCount, Options options)
    {
        using var match = new Match(roomCount, options.Entities, options.TickMs);
        for (int i = 0; i < options.Warmup; i++) match.Pump();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        int gc0 = GC.CollectionCount(0);
        int gc1 = GC.CollectionCount(1);
        int gc2 = GC.CollectionCount(2);

        var samples = new double[options.Frames];
        for (int i = 0; i < samples.Length; i++) {
            long ticks = match.PumpMeasured();
            samples[i] = ticks * 1000.0 / Stopwatch.Frequency;
        }
        Array.Sort(samples);

        int minServerTicks = int.MaxValue;
        for (int room = 0; room < roomCount; room++) {
            minServerTicks = Math.Min(minServerTicks, match.Server.GetSimulation(room).CurrentTick);
        }
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        Console.WriteLine(string.Join(",",
            "sd-e2e",
            roomCount,
            options.Entities,
            options.TickMs,
            options.Warmup,
            options.Frames,
            minServerTicks,
            F(samples.Average()),
            F(Percentile(samples, 0.50)),
            F(Percentile(samples, 0.95)),
            F(Percentile(samples, 0.99)),
            F(samples[^1]),
            F(GC.GetTotalMemory(false) / 1048576.0),
            F(process.WorkingSet64 / 1048576.0),
            GC.CollectionCount(0) - gc0,
            GC.CollectionCount(1) - gc1,
            GC.CollectionCount(2) - gc2));
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        int index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private static string F(double value) => value.ToString("F4", CultureInfo.InvariantCulture);

    private static void RegisterCommands(CommandFactory factory) =>
        factory.RegisterCommand<MoveCommand>(MoveCommandTypeId);

    private static SimulationConfig NewSimulationConfig(int tickMs) => new() {
        Mode = NetworkMode.ServerDriven,
        TickIntervalMs = tickMs,
        InputDelayTicks = 2,
        MaxRollbackTicks = 30,
        SyncCheckInterval = 10,
    };

    private static SessionConfig NewSessionConfig() => new() {
        MaxPlayers = PlayersPerRoom,
        MinPlayers = PlayersPerRoom,
        CountdownDurationMs = 1,
    };

    private static Options Parse(string[] args)
    {
        string rooms = Get(args, "--rooms", "1,4,8,16");
        int entities = int.Parse(Get(args, "--entities", "2000"), CultureInfo.InvariantCulture);
        int tickMs = int.Parse(Get(args, "--tick-ms", "20"), CultureInfo.InvariantCulture);
        int warmup = int.Parse(Get(args, "--warmup", "80"), CultureInfo.InvariantCulture);
        int frames = int.Parse(Get(args, "--frames", "200"), CultureInfo.InvariantCulture);
        int[] roomCounts = rooms.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(static value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        if (entities < 1 || tickMs < 1 || warmup < 1 || frames < 10 || roomCounts.Any(static n => n < 1))
            throw new ArgumentOutOfRangeException(nameof(args), "all counts must be positive and --frames must be >= 10");
        return new Options(roomCounts, entities, tickMs, warmup, frames);
    }

    private static string Get(string[] args, string name, string fallback)
    {
        for (int i = 0; i < args.Length - 1; i++) {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }
        return fallback;
    }
}
