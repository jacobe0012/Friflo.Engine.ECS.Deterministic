using System.Collections.Generic;
using xpTURN.Klotho.Logging;
using System.IO;

using xpTURN.Klotho.Input;
using xpTURN.Klotho.Replay;

namespace xpTURN.Klotho.Core
{
    public partial class KlothoEngine
    {
        // Replay system
        private ReplaySystem _replaySystem;
        private bool _isReplayMode;

        /// <summary>
        /// Replay system instance.
        /// </summary>
        public IReplaySystem ReplaySystem => _replaySystem;

        /// <summary>
        /// Whether the engine is currently in replay playback mode.
        /// </summary>
        public bool IsReplayMode => _isReplayMode;

        #region Replay Methods

        /// <summary>
        /// Starts replay playback.
        /// </summary>
        public void StartReplay(IReplayData replayData)
        {
            if (replayData == null)
            {
                _logger?.KError($"[KlothoEngine][Replay] Cannot start replay: null replay data");
                return;
            }

            _isReplayMode = true;
            _randomSeed = replayData.Metadata.RandomSeed;

            // Reset state
            CurrentTick = 0;
            _lastVerifiedTick = -1;
            _accumulator = 0;
            _inputBuffer.Clear();

            // Initialize simulation with the replay seed
            _simulation.Initialize();

            // Restore initial state snapshot - via RestoreFromFullState instead of OnInitializeWorld
            var snapshot = replayData.Metadata.InitialStateSnapshot;
            if (snapshot == null || snapshot.Length == 0)
                throw new InvalidDataException(
                    "[Replay] InitialStateSnapshot missing - corrupted file or snapshot was not injected during recording");
            _simulation.RestoreFromFullState(snapshot);

            // Save initial snapshot
            SaveSnapshot(0);

            // Load replay
            _replaySystem.Load(replayData, _logger);
            _replaySystem.OnTickPlayed += HandleReplayTick;
            _replaySystem.OnPlaybackFinished += HandleReplayFinished;

            State = KlothoState.Running;
            _replaySystem.Play();

            // Semantic symmetry with the normal start path - game code guards live-only behavior with IsReplayMode
            _viewCallbacks?.OnGameStart(this);
            OnGameStart?.Invoke();

            _logger?.KInformation($"[KlothoEngine][Replay] started: {replayData.Metadata.TotalTicks} ticks, {replayData.Metadata.DurationMs}ms");
        }

        /// <summary>
        /// Stops replay playback.
        /// </summary>
        public void StopReplay()
        {
            if (!_isReplayMode)
                return;

            _replaySystem.Stop();
            _replaySystem.OnTickPlayed -= HandleReplayTick;
            _replaySystem.OnPlaybackFinished -= HandleReplayFinished;

            _isReplayMode = false;
            State = KlothoState.Finished;

            _logger?.KInformation($"[KlothoEngine][Replay] stopped");
        }

        /// <summary>
        /// Pauses replay playback.
        /// </summary>
        public void PauseReplay()
        {
            if (_isReplayMode)
            {
                _replaySystem.Pause();
                State = KlothoState.Paused;
            }
        }

        /// <summary>
        /// Resumes replay playback.
        /// </summary>
        public void ResumeReplay()
        {
            if (_isReplayMode && State == KlothoState.Paused)
            {
                _replaySystem.Resume();
                State = KlothoState.Running;
            }
        }

        /// <summary>
        /// Sets the replay playback speed.
        /// </summary>
        public void SetReplaySpeed(ReplaySpeed speed)
        {
            _replaySystem.Speed = speed;
        }

        /// <summary>
        /// Seeks to a specific tick in the replay.
        /// </summary>
        public void SeekReplay(int tick)
        {
            if (!_isReplayMode)
                return;

            // Find the snapshot closest to the target tick via the simulation's own snapshot
            // history; with no history, replay seek re-simulates from tick 0.
            int startTick = 0;
            int nearest = _simulation.GetNearestRollbackTick(tick);
            if (nearest >= 0)
                startTick = nearest;

            _simulation.Rollback(startTick);

            // A backward seek must un-latch the Synced dispatch watermark so the
            // resumed HandleReplayTick re-dispatches Synced events at re-played ticks (mirror
            // Spectator.ResetToTick). GetNearestRollbackTick returns startTick <= tick, so
            // startTick - 1 < tick keeps the resumed tick above the lowered watermark.
            if (_syncedDispatchHighWaterMark >= startTick)
                _syncedDispatchHighWaterMark = startTick - 1;
            // Drop stale buffered events (pool-safe) and reset ring-wrap slot markers so the
            // resumed ClearTick does not false-fire the newer-occupant dev guard after a long
            // backward seek. Replay only plays forward from `tick` after a seek (no rollback that
            // reads earlier ticks), so wiping the whole buffer is safe.
            _eventBuffer.ClearAll();
            // ClearAll just pooled events still referenced by the collector's residue (the prior
            // HandleReplayTick leaves _collected populated). Drop those refs now so the empty-seek
            // path (startTick == tick, loop body never runs) holds no dangling pointers.
            _eventCollector.Clear();

            // Re-simulate from the nearest snapshot up to the target tick
            CurrentTick = startTick;
            var replayData = _replaySystem.CurrentReplayData;

            while (CurrentTick < tick && CurrentTick <= replayData.Metadata.TotalTicks)
            {
                var commands = replayData.GetCommandsForTick(CurrentTick);
                _tickCommandsCache.Clear();
                for (int i = 0; i < commands.Count; i++)
                    _tickCommandsCache.Add(commands[i]);
                // Open the collector so RaiseEvent stamps evt.Tick correctly and
                // any residue from the prior path is cleared (mirrors every other Tick path).
                // BeginTick is load-bearing here: it clears the entry residue before the drop loop,
                // so the loop never re-returns events already pooled by ClearAll above.
                _eventCollector.BeginTick(CurrentTick);
                _simulation.Tick(_tickCommandsCache);
                // Seek re-simulation is state-advance only — these events fall outside the resumed
                // dispatch range (HandleReplayTick re-runs from `tick`), so buffering them would
                // double-dispatch. Return them to the pool instead, or they leak (the next BeginTick
                // clears _collected with no pool return).
                for (int ei = 0; ei < _eventCollector.Count; ei++)
                    EventPool.Return(_eventCollector.Collected[ei]);
                _eventCollector.Clear();

                SaveSnapshot(CurrentTick);

                CurrentTick++;
            }

            _replaySystem.SeekToTick(tick);

            _logger?.KInformation($"[KlothoEngine][Replay] seek: tick={tick}");
        }

        /// <summary>
        /// Saves the current replay to a file.
        /// </summary>
        public void SaveReplayToFile(string filePath, bool dumpJson = false)
        {
            _replaySystem.SaveToFile(filePath, dumpJson);
        }

        /// <summary>
        /// Gets the current replay data.
        /// </summary>
        public IReplayData GetCurrentReplayData()
        {
            return _replaySystem.CurrentReplayData;
        }

        /// <summary>
        /// Gets the random seed used for this game.
        /// </summary>
        public int GetRandomSeed()
        {
            return _randomSeed;
        }

        private void HandleReplayTick(int tick, System.Collections.Generic.IReadOnlyList<ICommand> commands)
        {
            // Save snapshot for seeking - per-tick save
            SaveSnapshot(tick);

            // Run the simulation with replay commands and collect events
            _tickCommandsCache.Clear();
            for (int i = 0; i < commands.Count; i++)
                _tickCommandsCache.Add(commands[i]);
            _eventCollector.BeginTick(tick);
            _simulation.Tick(_tickCommandsCache);

            // Store the collected events
            _eventBuffer.ClearTick(tick);
            for (int ei = 0; ei < _eventCollector.Count; ei++)
                _eventBuffer.AddEvent(tick, _eventCollector.Collected[ei]);

            _lastVerifiedTick = tick;
            CurrentTick = tick + 1;
            OnTickExecuted?.Invoke(tick);
            _viewCallbacks?.OnTickExecuted(tick);
            OnTickExecutedWithState?.Invoke(tick, FrameState.Verified);
            OnFrameVerified?.Invoke(tick);

            // Dispatch all events as confirmed (replay = all verified)
            DispatchTickEvents(tick, FrameState.Verified);
        }

        private void HandleReplayFinished()
        {
            State = KlothoState.Finished;
            _isReplayMode = false;

            _replaySystem.OnTickPlayed -= HandleReplayTick;
            _replaySystem.OnPlaybackFinished -= HandleReplayFinished;

            _logger?.KInformation($"[KlothoEngine][Replay] playback finished");
        }

        #endregion
    }
}
