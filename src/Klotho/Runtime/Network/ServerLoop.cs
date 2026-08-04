using System;
using xpTURN.Klotho.Logging;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Multi-room server main loop.
    /// 3-Phase structure: Poll → ThreadPool room updates → Flush.
    /// A CountdownEvent-based timeout barrier isolates slow rooms as stragglers.
    /// </summary>
    public class ServerLoop
    {
        private const int STRAGGLER_OVERLOAD_THRESHOLD = 10;
        private const int DRIFT_LOG_INTERVAL_TICKS = 1000;
        private const int BUDGET_MARGIN_MS = 2;
        private const int UNROUTED_CLEANUP_INTERVAL_MS = 1000;

        // Graceful Shutdown timeouts
        private const int SHUTDOWN_PHASE2_TIMEOUT_MS = 1000;
        private const int SHUTDOWN_FLUSH_WAIT_MS = 100;
        private const int SHUTDOWN_TIMEOUT_MS = 3000;

        private readonly INetworkTransport _transport;
        private readonly RoomManager _roomManager;
        private readonly RoomRouter _router;
        private readonly int _tickIntervalMs;
        private readonly IKLogger _logger;
        private readonly CancellationTokenSource _cts;

        private readonly Stopwatch _stopwatch = new Stopwatch();
        private readonly List<Room> _readyRooms = new List<Room>();

        // Per-cycle barrier CountdownEvents pending disposal. Each instance appears exactly once;
        // disposed only when its count reaches 0 (Wait(0)==true) so a late worker Signal cannot
        // hit a disposed instance. Straggler marking/recovery uses Room.UpdateComplete, not this list.
        private readonly List<CountdownEvent> _pendingCountdowns = new List<CountdownEvent>();

        // Drift measurement
        private long _startTimeMs;
        private int _totalCycles;
        private int _lastDriftLogCycle;
        private long _lastUnroutedCleanupMs;
        private long _nextCycleTimeMs;
        private int _startCycle;

        public CancellationToken Token => _cts.Token;

        public ServerLoop(
            INetworkTransport transport,
            RoomManager roomManager,
            int tickIntervalMs,
            IKLogger logger)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _roomManager = roomManager ?? throw new ArgumentNullException(nameof(roomManager));
            _router = roomManager.Router;
            _tickIntervalMs = tickIntervalMs > 0 ? tickIntervalMs : 25;
            _logger = logger;
            _cts = new CancellationTokenSource();
        }

        /// <summary>
        /// Runs the server loop. Blocks until the CancellationToken is canceled.
        /// </summary>
        public void Run()
        {
            Console.CancelKeyPress += OnCancelKeyPress;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

            _stopwatch.Start();
            long lastUpdateTime = _stopwatch.ElapsedMilliseconds;
            _startTimeMs = lastUpdateTime;
            _totalCycles = 0;
            _nextCycleTimeMs = lastUpdateTime + _tickIntervalMs;
            _lastDriftLogCycle = 0;
            _lastUnroutedCleanupMs = lastUpdateTime;

            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    long cycleStart = _stopwatch.ElapsedMilliseconds;
                    float elapsed = cycleStart - lastUpdateTime;
                    lastUpdateTime = cycleStart;
                    float elapsedSec = elapsed / 1000f;

                    ExecuteCycle(elapsedSec);

                    _totalCycles++;
                    LogDriftIfNeeded();

                    // Yield CPU (drift correction based on target time)
                    long now = _stopwatch.ElapsedMilliseconds;
                    long sleepMs = _nextCycleTimeMs - now;
                    if (sleepMs > 1)
                        Thread.Sleep((int)sleepMs - 1);
                    _nextCycleTimeMs += _tickIntervalMs;
                }
            }
            finally
            {
                Console.CancelKeyPress -= OnCancelKeyPress;
                AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
                _stopwatch.Stop();

                GracefulShutdown();
            }
        }

        /// <summary>
        /// Runs one server cycle: Stage 0 (recover/transition/cleanup) → Stage 1 (poll) →
        /// Stage 2 (ThreadPool room updates with a timeout barrier) → Stage 3 (flush).
        /// Extracted from Run() so tests can drive deterministic single cycles.
        /// </summary>
        private void ExecuteCycle(float elapsedSec)
        {
            long cycleStart = _stopwatch.ElapsedMilliseconds;

            // ── Stage 0: previous cycle cleanup ──
            RecoverStragglers();
            _roomManager.TransitionDrainingRooms();
            _roomManager.CleanupDisposingRooms();

            // ── Stage 1: receive (Main Thread) ──
            _transport.PollEvents();

            // Cleanup unrouted peer timeouts (1-second interval)
            if (cycleStart - _lastUnroutedCleanupMs >= UNROUTED_CLEANUP_INTERVAL_MS)
            {
                _router.CleanupUnroutedPeers();
                _lastUnroutedCleanupMs = cycleStart;
            }

            long pollElapsed = _stopwatch.ElapsedMilliseconds - cycleStart;

            // ── Stage 2: room updates (ThreadPool) ──
            _roomManager.GetReadyRooms(_readyRooms);
            int readyCount = _readyRooms.Count;

            if (readyCount > 0)
            {
                // New CountdownEvent each cycle (per-cycle isolation). Disposed later in
                // RecoverStragglers when its count reaches 0 — never disposed in-cycle (avoids the
                // Set/Dispose race with a worker still inside Signal()).
                var countdown = new CountdownEvent(readyCount);

                for (int i = 0; i < readyCount; i++)
                {
                    var room = _readyRooms[i];
                    room.UpdateComplete = false;   // reset before dispatch (main thread)
                    ThreadPool.QueueUserWorkItem(state =>
                    {
                        var r = (Room)state;
                        try
                        {
                            r.Update(elapsedSec);
                        }
                        catch (Exception ex)
                        {
                            _logger?.KError($"[ServerLoop] Room {r.RoomId} Update exception: {ex.Message}\n{ex.StackTrace}");
                        }
                        finally
                        {
                            r.UpdateComplete = true;   // recovery signal (Signal() touches no room state)
                            countdown.Signal();
                        }
                    }, room);
                }

                // Timeout barrier
                int budgetMs = Math.Max(1, _tickIntervalMs - (int)pollElapsed - BUDGET_MARGIN_MS);
                bool allCompleted = countdown.Wait(budgetMs);

                if (!allCompleted)
                    HandleStragglers();

                // Single disposal path: always defer to RecoverStragglers (Wait(0)==true).
                _pendingCountdowns.Add(countdown);

                // Force close overloaded rooms
                HandleOverloadedRooms();
            }

            // ── Stage 3: flush send queue ──
            _transport.FlushSendQueue();
        }

        /// <summary>
        /// Test seam: runs N deterministic cycles without Run()'s blocking, drift correction,
        /// or Console/ProcessExit hooks. Used by the straggler-safety tests.
        /// </summary>
        internal void RunCyclesForTest(int cycles, float elapsedSec)
        {
            for (int i = 0; i < cycles; i++)
                ExecuteCycle(elapsedSec);
        }

        /// <summary>
        /// Stage-0 recovery: (1) clears IsStraggler for completed stragglers (per-room UpdateComplete),
        /// (2) disposes each pending barrier CountdownEvent once its count reaches 0. Must run before
        /// TransitionDrainingRooms/CleanupDisposingRooms so a just-completed straggler is processed
        /// in the same cycle.
        /// </summary>
        private void RecoverStragglers()
        {
            // (1) Room recovery — completion flag based, independent of the countdowns.
            _roomManager.RecoverCompletedStragglers();

            // (2) Countdown disposal — each instance appears once; dispose only when count==0
            //     (all workers Signaled), so no late worker can hit a disposed instance.
            for (int i = _pendingCountdowns.Count - 1; i >= 0; i--)
            {
                var cd = _pendingCountdowns[i];
                if (cd.Wait(0))
                {
                    cd.Dispose();
                    _pendingCountdowns.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Marks rooms still running at the stage-2 timeout as stragglers. Uses per-room
        /// UpdateComplete so rooms that finished within budget are not falsely marked (MT-2 fix).
        /// </summary>
        private void HandleStragglers()
        {
            for (int i = 0; i < _readyRooms.Count; i++)
            {
                var room = _readyRooms[i];
                if (!room.UpdateComplete && !room.IsStraggler)
                {
                    room.MarkStraggler();
                    _logger?.KWarning(
                        $"[ServerLoop] Room {room.RoomId} straggler (count={room.StragglerCount})");
                }
            }
        }

        /// <summary>
        /// Force-closes rooms whose cumulative straggler count exceeds the threshold.
        /// </summary>
        private void HandleOverloadedRooms()
        {
            for (int i = 0; i < _readyRooms.Count; i++)
            {
                var room = _readyRooms[i];
                if (room.StragglerCount >= STRAGGLER_OVERLOAD_THRESHOLD)
                {
                    _logger?.KError(
                        $"[ServerLoop] Room {room.RoomId} overloaded ({room.StragglerCount} cumulative straggles), force closing");

                    // Broadcast ServerShutdown (Reason=2: RoomOverloaded)
                    try
                    {
                        var shutdownMsg = new ServerShutdownMessage { Reason = 2 };
                        var serializer = new MessageSerializer();
                        using var msg = serializer.SerializePooled(shutdownMsg);
                        room.Transport.Broadcast(msg.Data, msg.Length, DeliveryMethod.Reliable);
                    }
                    catch { /* Already overloaded — ignore send failure */ }

                    room.State = RoomState.Disposing;
                }
            }
        }

        private void LogDriftIfNeeded()
        {
            if (_totalCycles - _lastDriftLogCycle < DRIFT_LOG_INTERVAL_TICKS) return;

            // Reset the baseline on the first log (removes initialization overhead)
            if (_lastDriftLogCycle == 0)
            {
                _startTimeMs = _stopwatch.ElapsedMilliseconds;
                _startCycle = _totalCycles;
            }

            _lastDriftLogCycle = _totalCycles;

            long nowMs = _stopwatch.ElapsedMilliseconds;
            long elapsedMs = nowMs - _startTimeMs;
            long expectedMs = (long)(_totalCycles - _startCycle) * _tickIntervalMs;
            long driftMs = elapsedMs - expectedMs;
            float cycleRate = elapsedMs > 0 ? (_totalCycles - _startCycle) / (elapsedMs / 1000f) : 0;

            _logger?.KInformation(
                $"[ServerLoop] cycles={_totalCycles}, drift={driftMs}ms, " +
                $"rate={cycleRate:F1}Hz (target={1000f / _tickIntervalMs:F0}Hz), " +
                $"activeRooms={_roomManager.ActiveRoomCount}");
        }

        /// <summary>
        /// Graceful Shutdown.
        /// (1) Reject new connections → (2) wait for stragglers to complete → (3) broadcast Shutdown to all rooms → (4) flush + Disconnect.
        /// </summary>
        private void GracefulShutdown()
        {
            _logger?.KInformation($"[ServerLoop] Graceful shutdown starting...");

            // Hard timeout: force-exit if the overall shutdown exceeds SHUTDOWN_TIMEOUT_MS
            var hardTimeout = new Thread(() =>
            {
                Thread.Sleep(SHUTDOWN_TIMEOUT_MS);
                _logger?.KError($"[ServerLoop] Shutdown hard timeout ({SHUTDOWN_TIMEOUT_MS}ms), forcing exit");
                Environment.Exit(1);
            }) { IsBackground = true };
            hardTimeout.Start();

            // (1) Reject new connections
            _router.StopAccepting();

            // (2) Wait for stragglers to complete.
            //     _pendingCountdowns may also hold already-complete cycles' countdowns; count only the
            //     truly-outstanding ones (Wait(0)==false) for the log. Dispose only the ones that
            //     complete within the timeout — a still-counting one may be Signaled by a late worker,
            //     so disposing it would risk a disposed-Signal (ThreadPool unobserved exception).
            int outstanding = 0;
            for (int i = 0; i < _pendingCountdowns.Count; i++)
                if (!_pendingCountdowns[i].Wait(0)) outstanding++;

            if (outstanding > 0)
                _logger?.KInformation(
                    $"[ServerLoop] Waiting for {outstanding} straggler room(s) to complete...");

            for (int i = 0; i < _pendingCountdowns.Count; i++)
            {
                var cd = _pendingCountdowns[i];
                if (cd.Wait(SHUTDOWN_PHASE2_TIMEOUT_MS))
                    cd.Dispose();
                // else: leave undisposed — GC + hard-timeout (Environment.Exit) reclaims it.
            }
            _pendingCountdowns.Clear();

            // (3) Broadcast ServerShutdown to all rooms
            _roomManager.ShutdownAllRooms();

            // (4) Flush sends + wait
            _transport.FlushSendQueue();
            Thread.Sleep(SHUTDOWN_FLUSH_WAIT_MS);
            _transport.Disconnect();

            _logger?.KInformation($"[ServerLoop] Graceful shutdown complete.");
        }

        public void Stop()
        {
            _cts.Cancel();
        }

        private void OnCancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            _cts.Cancel();
        }

        private void OnProcessExit(object sender, EventArgs e)
        {
            _cts.Cancel();
        }
    }
}
