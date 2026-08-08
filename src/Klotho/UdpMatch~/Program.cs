using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Friflo.Engine.ECS;
using Klotho.Friflo;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.LiteNetLib;
using xpTURN.Klotho.Network;

namespace Klotho.Friflo.UdpMatch;

/// <summary>
/// Runs a server-driven match over real LiteNetLib UDP, each peer in its own OS process.
/// </summary>
/// <remarks>
/// Every other networked check runs on the in-memory <c>LoopbackHub</c>, where delivery is
/// synchronous, in-process, ordered on demand and unbounded in size. That is the right default for
/// a determinism test — it makes a hash mismatch attributable to the simulation rather than to
/// scheduling — but it means an entire layer has never executed: the real socket, the real
/// serializer across a process boundary, MTU, and two peers on independent clocks that the test
/// cannot interleave.
/// <para>
/// Each process writes the hash of every tick it considers settled to a file, and the coordinator
/// compares the files once they have all exited. Comparing after the fact rather than over a side
/// channel keeps the processes genuinely independent: nothing but UDP passes between them while
/// the match runs.
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>Frames the match runs once every client is in.</summary>
    private const int MatchFrames = 400;

    /// <summary>
    /// How long a peer waits for the match to come up before giving up, in seconds.
    /// </summary>
    /// <remarks>
    /// Generous: a real handshake involves process startup, socket binding and five sync round
    /// trips, and the coordinator starts the clients without knowing exactly when the server's
    /// socket is ready.
    /// </remarks>
    private const int StartupTimeoutSeconds = 30;

    private static int Main(string[] args)
    {
        if (args.Length == 0) {
            Console.Error.WriteLine("usage: Klotho.UdpMatch <match|server|client> [--port N] [--players N] [--out PATH]");
            return 2;
        }
        var options = Options.Parse(args);
        try {
            return args[0] switch {
                "match"  => RunMatch(options),
                "server" => RunServer(options),
                "client" => RunClient(options),
                _        => Fail($"unknown role '{args[0]}' - expected match, server or client"),
            };
        }
        catch (Exception e) {
            Console.Error.WriteLine($"[{args[0]}] {e}");
            return 1;
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    #region coordinator

    /// <summary>
    /// Launch the server and the clients as separate processes, wait for them, and compare what
    /// they recorded.
    /// </summary>
    private static int RunMatch(Options options)
    {
        string workDir = Path.Combine(Path.GetTempPath(), "klotho-udp-match-" + Environment.ProcessId);
        Directory.CreateDirectory(workDir);
        Console.WriteLine($"UDP match: {options.Players} players, port {options.Port}, recording to {workDir}");

        var processes = new List<Process>();
        try {
            string exePath = Environment.ProcessPath;
            string dll     = System.Reflection.Assembly.GetEntryAssembly()?.Location;

            processes.Add(Spawn(exePath, dll,
                $"server --port {options.Port} --players {options.Players} --out \"{Path.Combine(workDir, "server.txt")}\""));
            for (int i = 0; i < options.Players; i++) {
                processes.Add(Spawn(exePath, dll,
                    $"client --port {options.Port} --players {options.Players} --out \"{Path.Combine(workDir, $"client{i}.txt")}\""));
            }

            bool allExitedCleanly = true;
            for (int i = 0; i < processes.Count; i++) {
                // Bounded so a peer that hangs fails the run rather than wedging it forever.
                if (!processes[i].WaitForExit((StartupTimeoutSeconds + 120) * 1000)) {
                    Console.Error.WriteLine($"[match] process {i} did not exit - killing it");
                    try { processes[i].Kill(entireProcessTree: true); } catch { /* already gone */ }
                    allExitedCleanly = false;
                    continue;
                }
                if (processes[i].ExitCode != 0) {
                    Console.Error.WriteLine($"[match] process {i} exited {processes[i].ExitCode}");
                    allExitedCleanly = false;
                }
            }
            if (!allExitedCleanly) {
                return 1;
            }
            return Compare(workDir, options.Players);
        }
        finally {
            foreach (var process in processes) {
                process.Dispose();
            }
        }
    }

    /// <summary>Start one peer process, inheriting this one's stdout and stderr.</summary>
    /// <remarks>
    /// Run through the same host this process was started by: launched via <c>dotnet</c> the entry
    /// assembly is a dll and needs the host, whereas an apphost exe runs directly.
    /// </remarks>
    private static Process Spawn(string exePath, string dll, string arguments)
    {
        bool viaHost = exePath != null && Path.GetFileNameWithoutExtension(exePath)
                                              .Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        var info = new ProcessStartInfo {
            FileName        = viaHost ? exePath : exePath ?? "dotnet",
            Arguments       = viaHost ? $"\"{dll}\" {arguments}" : arguments,
            UseShellExecute = false,
        };
        return Process.Start(info);
    }

    /// <summary>
    /// Compare the recorded hashes, requiring every client to agree with the server wherever both
    /// recorded a tick.
    /// </summary>
    private static int Compare(string workDir, int players)
    {
        var server = ReadHashes(Path.Combine(workDir, "server.txt"));
        if (server.Count == 0) {
            Console.Error.WriteLine("[match] the server recorded nothing - the match never ran");
            return 1;
        }

        int totalComparisons = 0;
        for (int i = 0; i < players; i++) {
            var client = ReadHashes(Path.Combine(workDir, $"client{i}.txt"));
            if (client.Count == 0) {
                Console.Error.WriteLine($"[match] client {i} recorded nothing - it never entered the match");
                return 1;
            }
            int comparisons = 0;
            foreach (var (tick, clientHash) in client) {
                if (!server.TryGetValue(tick, out long serverHash)) {
                    continue;
                }
                if (serverHash != clientHash) {
                    Console.Error.WriteLine(
                        $"[match] client {i} disagrees with the server at verified tick {tick}: " +
                        $"{clientHash} vs {serverHash}");
                    return 1;
                }
                comparisons++;
            }
            // A handful of overlapping ticks would be a technically-passing run that looked at
            // almost nothing.
            if (comparisons < 20) {
                Console.Error.WriteLine(
                    $"[match] only {comparisons} verified ticks were compared for client {i} - too few to conclude anything");
                return 1;
            }
            totalComparisons += comparisons;
            Console.WriteLine($"[match] client {i}: {comparisons} verified ticks matched the server");
        }

        Console.WriteLine($"UDP match: PASS ({totalComparisons} verified ticks compared across {players} clients)");
        return 0;
    }

    /// <summary>Read a peer's recorded "tick hash" lines.</summary>
    private static Dictionary<int, long> ReadHashes(string path)
    {
        var hashes = new Dictionary<int, long>();
        if (!File.Exists(path)) {
            return hashes;
        }
        foreach (string line in File.ReadAllLines(path)) {
            var parts = line.Split(' ');
            if (parts.Length != 2) {
                continue;
            }
            if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int tick)
                && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long hash)) {
                hashes[tick] = hash;
            }
        }
        return hashes;
    }

    #endregion

    #region peers

    /// <summary>Run the authoritative server until the match has played out.</summary>
    private static int RunServer(Options options)
    {
        var transport = new LiteNetLibTransport(null);
        if (!transport.Listen("0.0.0.0", options.Port, options.Players + 4)) {
            return Fail($"[server] could not bind port {options.Port}");
        }

        using var server = FrifloKlothoServer.Create(
            new EntityStore(), transport, Game.NewSimulationConfig(), Game.NewSessionConfig(options.Players),
            new Game.Callbacks(options.Players), Game.RegisterCommands);

        var recorded = new Dictionary<int, long>();
        var clock    = Stopwatch.StartNew();

        // ServerNetworkService.Update does NOT poll its transport - unlike the client service,
        // which polls its own - so the server would never see a single packet if this loop only
        // called Update. The in-process tests hide this by pumping every link themselves.
        void Step()
        {
            transport.PollEvents();
            server.Update(Game.TickIntervalMs / 1000f);
        }

        // Wait for every client to connect and the match to start. Real processes, so this is
        // wall-clock bound rather than a fixed number of frames.
        while (server.State != KlothoState.Running) {
            if (clock.Elapsed.TotalSeconds > StartupTimeoutSeconds) {
                return Fail($"[server] the match did not start: phase={server.Phase} players={server.PlayerCount}");
            }
            Pump(Step);
        }
        Console.WriteLine($"[server] match started with {server.PlayerCount} players");

        for (int frame = 0; frame < MatchFrames; frame++) {
            Pump(Step);
            // Every server tick is verified by definition - the server is the authority - so the
            // state after executing CurrentTick - 1 is final as soon as it exists.
            int executed = server.Simulation.CurrentTick - 1;
            if (executed >= 0 && !recorded.ContainsKey(executed)) {
                recorded[executed] = server.Simulation.GetStateHash();
            }
        }

        // Keep serving after the recording window so a client trailing slightly behind can finish
        // its own; exiting here would cut its last ticks off and shrink the overlap.
        for (int frame = 0; frame < MatchFrames / 4; frame++) {
            Pump(Step);
        }

        Write(options.OutputPath, recorded);
        Console.WriteLine($"[server] recorded {recorded.Count} verified ticks, final tick {server.Simulation.CurrentTick}");
        return recorded.Count > 0 ? 0 : Fail("[server] the match never advanced");
    }

    /// <summary>Run one client until the match has played out.</summary>
    private static int RunClient(Options options)
    {
        var transport = new LiteNetLibTransport(null);

        using var client = FrifloKlothoClient.Connect(
            new EntityStore(), transport, Game.NewSimulationConfig(), Game.NewSessionConfig(options.Players),
            new Game.Callbacks(options.Players), Game.RegisterCommands,
            address: "127.0.0.1", port: options.Port);

        var recorded = new Dictionary<int, long>();
        // Sampled inside the verified-tick event, the only moment the store holds the confirmed
        // state: the client re-simulates back up to its prediction lead as soon as a verified tick
        // lands, so after Update returns it is always on a predicted tick.
        client.OnTickVerified += tick => {
            if (!recorded.ContainsKey(tick)) {
                recorded[tick] = client.Simulation.GetStateHash();
            }
        };

        // The SD client service polls its own transport inside Update, so unlike the server this
        // needs no explicit poll.
        var clock = Stopwatch.StartNew();
        while (client.Phase != SessionPhase.Synchronized) {
            if (clock.Elapsed.TotalSeconds > StartupTimeoutSeconds) {
                return Fail($"[client] handshake did not complete: phase={client.Phase} state={client.State}");
            }
            Pump(() => client.Update(Game.TickIntervalMs / 1000f));
        }
        client.SetReady();

        while (client.State != KlothoState.Running || !client.HasWorld) {
            if (clock.Elapsed.TotalSeconds > StartupTimeoutSeconds) {
                return Fail($"[client] the match did not start: phase={client.Phase} state={client.State} hasWorld={client.HasWorld}");
            }
            Pump(() => client.Update(Game.TickIntervalMs / 1000f));
        }
        Console.WriteLine($"[client] in the match as player {client.LocalPlayerId}");

        for (int frame = 0; frame < MatchFrames; frame++) {
            Pump(() => client.Update(Game.TickIntervalMs / 1000f));
        }

        Write(options.OutputPath, recorded);
        Console.WriteLine(
            $"[client] player {client.LocalPlayerId}: {recorded.Count} verified ticks, " +
            $"{client.Simulation.RollbackCount} rollbacks, {client.ResyncCount} resyncs");

        // A client the server had to rebuild is a client that desynced. Recovery makes the hashes
        // agree again afterwards, so the comparison alone would not notice.
        if (client.ResyncCount != 0) {
            return Fail($"[client] was rebuilt from server state {client.ResyncCount} times - it desynced");
        }
        return recorded.Count > 0 ? 0 : Fail("[client] never recorded a verified tick");
    }

    /// <summary>
    /// Advance one frame and sleep out the rest of the tick interval.
    /// </summary>
    /// <remarks>
    /// Real time, unlike the in-process tests where one loop drives every peer and time is whatever
    /// the loop says. Here each process has its own clock and the engine paces itself against it,
    /// so a frame that returns immediately would spin the CPU and starve the others.
    /// </remarks>
    private static void Pump(Action update)
    {
        update();
        System.Threading.Thread.Sleep(Game.TickIntervalMs / 2);
    }

    /// <summary>Write the recorded hashes, one "tick hash" line each, in tick order.</summary>
    private static void Write(string path, Dictionary<int, long> hashes)
    {
        if (path == null) {
            return;
        }
        var ticks = new List<int>(hashes.Keys);
        ticks.Sort();
        var text = new StringBuilder();
        foreach (int tick in ticks) {
            text.Append(tick).Append(' ').Append(hashes[tick]).Append('\n');
        }
        File.WriteAllText(path, text.ToString());
    }

    #endregion

    /// <summary>Command-line options, shared by every role.</summary>
    private sealed class Options
    {
        internal int    Port       { get; private set; } = 9050;
        internal int    Players    { get; private set; } = 2;
        internal string OutputPath { get; private set; }

        internal static Options Parse(string[] args)
        {
            var options = new Options();
            for (int i = 1; i < args.Length - 1; i++) {
                switch (args[i]) {
                    case "--port":    options.Port       = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--players": options.Players    = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--out":     options.OutputPath = args[++i];                                          break;
                }
            }
            return options;
        }
    }
}
