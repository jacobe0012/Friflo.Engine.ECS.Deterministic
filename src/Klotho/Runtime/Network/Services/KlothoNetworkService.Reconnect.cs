using System;
using xpTURN.Klotho.Logging;
using System.Collections.Generic;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    public partial class KlothoNetworkService
    {
        // Reconnect: disconnected-player pool + empty-command cache
        private const int DISCONNECT_INPUT_PREDICTION_LIMIT = 0; // 0 = unlimited until timeout
        private const int TRANSPORT_RECONNECT_INTERVAL_MS = 1000;
        private const int RECONNECT_REQUEST_TIMEOUT_MS = 5000;
        private const int RECONNECT_FULLSTATE_TIMEOUT_MS = 5000;
        private DisconnectedPlayerInfo[] _disconnectedPlayerInfoPool;
        private int _disconnectedPlayerCount;
        private ICommand _emptyCommandCache;

        // Reconnect: guest reconnect state
        private enum ReconnectState { None, WaitingForTransport, SendingRequest, WaitingForFullState, Failed }
        private ReconnectState _reconnectState;
        private long _reconnectStartTimeMs;
        private long _reconnectRequestSentTime;
        private long _fullStateRequestTime;
        private int _reconnectRetryCount;
        private long _lastTransportReconnectTime;

        #region Reconnect: DisconnectedPlayerInfo pool

        private void InitDisconnectedPlayerPool(int maxPlayers)
        {
            _disconnectedPlayerInfoPool = new DisconnectedPlayerInfo[maxPlayers];
            for (int i = 0; i < maxPlayers; i++)
                _disconnectedPlayerInfoPool[i] = new DisconnectedPlayerInfo();
            _disconnectedPlayerCount = 0;
        }

        private DisconnectedPlayerInfo RentDisconnectedInfo()
        {
            for (int i = 0; i < _disconnectedPlayerInfoPool.Length; i++)
            {
                if (!_disconnectedPlayerInfoPool[i].IsActive)
                    return _disconnectedPlayerInfoPool[i];
            }
            return null;
        }

        private DisconnectedPlayerInfo FindDisconnectedInfo(int playerId)
        {
            for (int i = 0; i < _disconnectedPlayerInfoPool.Length; i++)
            {
                if (_disconnectedPlayerInfoPool[i].PlayerId == playerId)
                    return _disconnectedPlayerInfoPool[i];
            }
            return null;
        }

        private bool IsPlayerDisconnected(int playerId)
        {
            return FindDisconnectedInfo(playerId) != null;
        }

        // Returns true ONLY for actively reconnecting peers (IsReconnect=true). Cold-start
        // LateJoin peers (IsReconnect=false) return false — their watchdog activation during
        // the longer JoinTick wait is preserved as intentional behavior.
        private bool IsPlayerInActiveCatchup(int playerId)
        {
            foreach (var kvp in _lateJoinCatchups)
            {
                if (kvp.Value.PlayerId == playerId && kvp.Value.IsReconnect)
                    return true;
            }
            return false;
        }

        #endregion

        #region Reconnect: empty input injection

        /// <summary>
        /// Path A: pre-insertion — invoked once per frame from Update().
        /// Proactively fills each disconnected player's still-empty slots
        /// up to the input frontier (currentTick + InputDelay + RecommendedExtraDelay) — the same
        /// lead the local player's own input lands at — and broadcasts them via SendCommand().
        /// Previously this filled only the current tick, so the proxy fill trailed the verified
        /// frontier by `lead`: the guest verified chain sat one tick behind and ChainBreak spammed
        /// ("host proactive-fill is ~1 tick behind"). Filling ahead lets CanAdvanceTick pass without
        /// the reactive OnDisconnectedInputNeeded callback.
        /// </summary>
        private void InjectDisconnectedPlayerInputs()
        {
            if (!IsHost || _disconnectedPlayerCount == 0)
                return;

            // Same frontier the engine targets for the local player's own input
            // (CurrentTick + InputDelayTicks, KlothoEngine.cs) and the reconnect catchup JoinTick.
            int currentTick = _engine?.CurrentTick ?? _localTick;
            int frontier = currentTick
                + (_engine?.InputDelay ?? 0)
                + (_engine?.RecommendedExtraDelay ?? 0);

            for (int i = 0; i < _disconnectedPlayerInfoPool.Length; i++)
            {
                var info = _disconnectedPlayerInfoPool[i];
                if (!info.IsActive)
                    continue;

                if (DISCONNECT_INPUT_PREDICTION_LIMIT > 0
                    && info.PredictedTickCount >= DISCONNECT_INPUT_PREDICTION_LIMIT)
                    continue;

                // Fill every still-empty slot in [currentTick, frontier]. dedup via the public
                // HasCommand wrapper — proactive fills are NOT sealed (unlike Path B's
                // ForceInsertEmptyCommandsRange), so IsSealed would never short-circuit and we
                // would re-broadcast the whole window every frame (O(lead)). In steady state only
                // the newly-exposed frontier tick is unfilled -> one broadcast/frame/player.
                // Leaving them unsealed keeps the slot overwritable — a NECESSARY precondition for
                // the reconnect real-input override, but not sufficient on its own: the host receive
                // path (HandleCommandReceived) must request the overwrite, which it does for
                // real-over-unsealed-empty. A sealed slot would hard-block the real even
                // with overwrite=true (AddCommandChecked seal guard precedes the overwrite branch).
                for (int t = currentTick; t <= frontier; t++)
                {
                    if (_engine != null && _engine.HasCommand(t, info.PlayerId))
                        continue;
                    _commandFactory.PopulateEmpty(_emptyCommandCache, info.PlayerId, t);
                    // _emptyCommandCache reuse: ILockstepNetworkService.SendCommand exception.
                    // proxy fill — carries host timing, must not vote for info.PlayerId.
                    _proxyTimingPending = true;
                    SendCommand(_emptyCommandCache);
                }

                // PredictedTickCount drives the disconnect timeout and counts FRAMES of proxy fill,
                // not ticks filled — increment once per frame (not inside the tick loop) so the
                // wider range fill does not falsely accelerate the timeout.
                info.PredictedTickCount++;
            }
        }

        private void InjectCatchupPlayerInputs()
        {
            if (!IsHost || _lateJoinCatchups.Count == 0)
                return;

            foreach (var kvp in _lateJoinCatchups)
            {
                var info = kvp.Value;
                // Reconnect: inject from the tick following LastSentTick (= reconnect entry tick).
                // Cold-start LateJoin: defer to JoinTick (existing behavior — pre-join input is
                // semantically undefined for a never-joined player).
                int injectStartTick = info.IsReconnect ? info.LastSentTick + 1 : info.JoinTick;
                if (_localTick >= injectStartTick)
                {
                    _commandFactory.PopulateEmpty(_emptyCommandCache, info.PlayerId, _localTick);
                    // _emptyCommandCache reuse: ILockstepNetworkService.SendCommand exception.
                    // catchup proxy fill — carries host timing, must not vote for info.PlayerId.
                    _proxyTimingPending = true;
                    SendCommand(_emptyCommandCache);
                }
            }
        }

        /// <summary>
        /// Path B: reactive insertion — synchronous callback on engine CanAdvanceTick() failure.
        /// Range-fills [_lastVerifiedTick+1, CurrentTick] for each disconnected/presumed-dropped
        /// player so the chain can advance past the stall window in a single pass. Sealing inside
        /// ForceInsertEmptyCommandsRange protects against late real packet overwrites.
        /// </summary>
        private void HandleDisconnectedInputNeeded(int tick)
        {
            // Host-only — pool is host-only managed. Also guards SendCommand inside
            // FillAndBroadcastDisconnectedRange from running on guests.
            if (!IsHost || _engine == null) return;

            int rangeFrom = _engine.LastVerifiedTick + 1;
            int rangeTo = tick; // caller passes CurrentTick — fill the entire stall window
            if (rangeTo < rangeFrom) return;

            for (int i = 0; i < _disconnectedPlayerInfoPool.Length; i++)
            {
                var info = _disconnectedPlayerInfoPool[i];
                if (!info.IsActive)
                    continue;

                FillAndBroadcastDisconnectedRange(info.PlayerId, rangeFrom, rangeTo);
                // Range fill increments by 1 per call (not per tick filled) to avoid false
                // timeout trigger when stall window is large.
                info.PredictedTickCount++;
            }
        }

        // Shared by CheckQuorumMissPresumedDrop (activation-time direct fill) and
        // HandleDisconnectedInputNeeded (reactive fill on CanAdvanceTick failure). Host-only.
        // Performs local fill + seal via ForceInsertEmptyCommandsRange, then broadcasts the
        // same empty cmds so other guests fill their InputBuffer too (P2P star — host is the
        // single source for this player's input during presumed-drop / disconnected state).
        private void FillAndBroadcastDisconnectedRange(int playerId, int fromTick, int toTick)
        {
            if (toTick < fromTick || _engine == null || _commandFactory == null) return;

            _engine.ForceInsertEmptyCommandsRange(playerId, fromTick, toTick);

            for (int t = fromTick; t <= toTick; t++)
            {
                _commandFactory.PopulateEmpty(_emptyCommandCache, playerId, t);
                // _emptyCommandCache reuse: ILockstepNetworkService.SendCommand exception.
                // reactive range proxy fill — carries host timing, must not vote for playerId.
                _proxyTimingPending = true;
                SendCommand(_emptyCommandCache);
            }
        }

        #endregion

        #region Reconnect: quorum-miss watchdog

        // Telemetry counters — emitted at match end via EmitPresumedDropMetrics().
        private int _presumedDropFalsePositiveCount;
        private int _presumedDropTrueCount;
        // Counts cmds dropped from relay due to local seal. High count
        // signals frequent late retransmits that would have caused cross-peer divergence
        // without the seal guard.
        private int _relaySealDropCount;

        internal int PresumedDropFalsePositiveCount => _presumedDropFalsePositiveCount;
        internal int PresumedDropTrueCount => _presumedDropTrueCount;
        internal int RelaySealDropCount => _relaySealDropCount;

        // Each frame: if a remote peer's input is missing at _lastVerifiedTick + 1 for >= the
        // adaptive threshold, mark them as presumed-dropped so reactive empty-fill activates
        // before the transport-level DisconnectTimeout fires (~5s).
        private void CheckQuorumMissPresumedDrop()
        {
            if (!IsHost || _engine == null || _simConfig == null)
                return;

            // QuorumMissDropTicks acts as the floor (and the disable switch when <= 0).
            int floor = _simConfig.QuorumMissDropTicks;
            if (floor <= 0)
                return;

            // Adaptive threshold. The fixed 20-tick floor mis-fired at high RTT —
            // normal steady-state lag (InputDelay + RecommendedExtraDelay + jitter) routinely
            // reached it, presuming-dropping merely-delayed players and empty-sealing their real
            // input into a permanent desync. Raise the threshold above the engine's own delay
            // compensation, clamped to MaxRollbackTicks: beyond that ceiling the host can no longer
            // speculatively advance (the chain freezes), so a larger threshold would never fire
            // before the freeze anyway. QuorumMissMarginTicks lifts the low-extraDelay threshold
            // above the normal worst-case transient lag: a FullStateResync rollback (e.g. desync@580
            // → rollback to anchor 560) stalls the chain to lag == rollback distance (~20) while
            // extraDelay is still at its RTT baseline (~7); margin 8 left the threshold AT that lag
            // (floor 20 == lag 20 → fp #1, reproduced across runs), so it is raised to 12 (threshold
            // 23 at extraDelay 7, ~3-tick safety over the measured lag 20).
            const int QuorumMissMarginTicks = 12;
            int adaptive = _engine.InputDelay + _engine.RecommendedExtraDelay + QuorumMissMarginTicks;
            int threshold = System.Math.Max(floor, System.Math.Min(adaptive, _simConfig.MaxRollbackTicks));

            // Window collapse → disable. The useful firing window is
            // [threshold, MaxRollbackTicks): the host freezes at lag == MaxRollbackTicks (speculative-
            // advance ceiling), so a threshold at or above that ceiling can never fire before the
            // freeze — clamping-and-firing there only mis-fires on resync/rollback churn that drives
            // lag to the ceiling (observed fp #2). When the window has collapsed, do not fire; a real
            // drop falls back to the transport DisconnectTimeout. Gate on the final threshold (not
            // adaptive alone) so a floor misconfigured above the ceiling is also caught.
            if (threshold >= _simConfig.MaxRollbackTicks)
                return;

            int verifiedTick = _engine.LastVerifiedTick;
            int currentTick = _engine.CurrentTick;
            int lag = currentTick - verifiedTick;
            if (lag < threshold)
                return;

            int stallTick = verifiedTick + 1;

            for (int i = 0; i < _players.Count; i++)
            {
                var player = _players[i];
                int playerId = player.PlayerId;

                // Local player's input is always present (auto-injected empty).
                if (playerId == LocalPlayerId)
                    continue;

                // Already tracked as disconnected (transport-detected or previously presumed).
                if (IsPlayerDisconnected(playerId))
                    continue;

                // Peer has provided input at the stall tick — no presumed drop needed.
                if (_engine.HasCommand(stallTick, playerId))
                    continue;

                // Skip activation for players currently in active reconnect-catchup. Host has
                // already initiated the catchup flow (peerId<->playerId remapped in
                // HandleReconnectRequest, catchup batches in transit), and presuming a drop here
                // would emit a false positive every reconnect whose first-real-input latency
                // exceeds QuorumMissDropTicks.
                if (IsPlayerInActiveCatchup(playerId))
                    continue;

                var info = RentDisconnectedInfo();
                if (info == null)
                    continue;

                info.PlayerId = playerId;
                info.PeerId = -1;
                info.DisconnectTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                info.LastConfirmedTick = verifiedTick;
                info.PredictedTickCount = 0;
                info.IsPresumedDrop = true;
                _disconnectedPlayerCount++;

                _engine.NotifyPlayerDisconnected(playerId);

                _logger?.KInformation($"[KlothoNetworkService][PresumedDrop] Activated quorum-miss watchdog: playerId={playerId}, stallTick={stallTick}, lag={lag}, threshold={threshold}");

                // Directly fill the stall window. CanAdvanceTick may never fail if peer is
                // sending late inputs at currentTick, so cannot rely on OnDisconnectedInputNeeded
                // callback alone. Fill is host-local + broadcast to keep all peers in sync.
                FillAndBroadcastDisconnectedRange(playerId, stallTick, currentTick);
            }
        }

        // Called from HandleCommandMessage when a real command arrives from the network
        // (local echoes excluded by the call-site guard). If the player was
        // previously marked as presumed-dropped by the watchdog AND the cmd is at or beyond
        // the current dynamic stall tick (= _lastVerifiedTick + 1), treat as recovery: fill
        // the [stallTick, cmd.Tick) window so the chain cannot stall once the fill paths
        // deactivate, then clear presumed-drop state. Inputs for ticks before the stall
        // prove nothing fresh and never recover.
        // Host-only: the disconnected player pool is managed by host only.
        private void OnRealCommandReceivedDuringPresumedDrop(ICommand command)
        {
            if (!IsHost) return;
            if (command == null) return;
            int playerId = command.PlayerId;
            var info = FindDisconnectedInfo(playerId);
            if (info == null || !info.IsPresumedDrop)
                return;

            // Recovery = any unsealed network real input at or beyond the
            // dynamic stall tick. The old exact-match missed every resync-snap recovery:
            // with InputDelayTicks the recovered peer's sends land ahead of the stall tick
            // and advance in parallel with it — the entry stayed latched while the peer
            // fully participated (keep-first stores its real input), and the wall-clock
            // timeout then kicked an active player.
            int stallTick = (_engine?.LastVerifiedTick ?? info.LastConfirmedTick) + 1;
            if (command.Tick < stallTick)
                return; // stale input for an already-covered tick proves nothing fresh

            // Sanity bound: reject ticks beyond the prediction window (malicious/skewed
            // peer) — neither release nor fill. MaxRollbackTicks is the natural ceiling:
            // legitimate sends (CurrentTick + InputDelayTicks + _recommendedExtraDelay)
            // always sit far inside it.
            int currentTick = _engine?.CurrentTick ?? stallTick;
            if (_simConfig != null && command.Tick > currentTick + _simConfig.MaxRollbackTicks)
                return;

            // Close the unfilled window BEFORE releasing, so the chain cannot stall once the
            // fill paths deactivate (presumed-drop clear → player "connected" → disconnect-fill
            // stops). Fill the ENTIRE presumed window [stallTick, currentTick],
            // not just [stallTick, command.Tick-1] — command.Tick's own real was DroppedSealed by
            // the watchdog seal and any hole in the window (incl. ticks >= command.Tick) would
            // freeze the chain. Filling empty discards this player's reals in the window (localized,
            // resync-recoverable desync). The Unseal in
            // ForceInsertEmptyCommandsRange lets this fill restore sealed-but-no-command holes.
            // Echoes of this fill are blocked by the call-site guard — no re-entry.
            if (currentTick >= stallTick)
                FillAndBroadcastDisconnectedRange(playerId, stallTick, currentTick);

            int presumedDuration = _engine != null ? _engine.CurrentTick - info.LastConfirmedTick : 0;

            info.Reset();
            _disconnectedPlayerCount--;
            _presumedDropFalsePositiveCount++;

            // Sync engine state. Without this, _disconnectedPlayerIds stays populated while
            // the pool entry is gone — next watchdog iteration would re-add via RentDisconnectedInfo.
            _engine?.NotifyPlayerReconnected(playerId);
            BroadcastPlayerState(playerId, PlayerStateChange.Reconnected);

            _logger?.KInformation($"[KlothoNetworkService][PresumedDrop] False positive — real input arrived at tick {command.Tick} (stallTick={stallTick}): playerId={playerId}, presumedDurationTicks={presumedDuration}");
        }

        // Promote a presumed-drop entry to a confirmed transport disconnect. Called from
        // HandlePeerDisconnected when the entry already exists. Returns true if the entry
        // was a presumed-drop (so caller skips the normal Add path).
        private bool PromotePresumedDropToConfirmed(int playerId, int peerId)
        {
            var info = FindDisconnectedInfo(playerId);
            if (info == null || !info.IsPresumedDrop)
                return false;

            info.IsPresumedDrop = false;
            info.PeerId = peerId;
            _presumedDropTrueCount++;
            return true;
        }

        internal void EmitPresumedDropMetrics(string role, int playerId)
        {
            // Host-only: watchdog and relay seal guard increment counters only on host.
            if (!IsHost) return;
            int total = _presumedDropFalsePositiveCount + _presumedDropTrueCount;
            float ratio = total > 0 ? (float)_presumedDropFalsePositiveCount / total : 0f;
            var engine = _engine as KlothoEngine;
            int unexpectedFullStateDrops = engine?.UnexpectedFullStateDropCount ?? 0;
            int resyncHashMismatches = engine?.ResyncHashMismatchCount ?? 0;
            int consecutiveDesyncPeak = engine?.ConsecutiveDesyncPeak ?? 0;
            int resyncRequestTotal = engine?.ResyncRequestTotalCount ?? 0;
            int postResyncDesync = engine?.PostResyncDesyncCount ?? 0;
            _logger?.KInformation($"[Metrics][PresumedDrop] role={role} playerId={playerId} falsePositive={_presumedDropFalsePositiveCount} truePositive={_presumedDropTrueCount} ratio={ratio:F3} relaySealDrop={_relaySealDropCount} unexpectedFullStateDrop={unexpectedFullStateDrops} resyncHashMismatch={resyncHashMismatches} desyncPeak={consecutiveDesyncPeak} resyncRequestTotal={resyncRequestTotal} postResyncDesync={postResyncDesync}");
        }

        #endregion

        #region Player state notification

        // Host-only: broadcast a host-confirmed roster transition to guests so their engine
        // roster sets (_disconnectedPlayerIds / _activePlayerIds) match the host and a departed
        // peer is excluded from the timing-advantage vote. Presumed-drop guesses are never sent
        // — only confirmed disconnect / reconnect / leave.
        private void BroadcastPlayerState(int playerId, PlayerStateChange state)
        {
            if (!IsHost)
                return;
            BroadcastMessagePooled(
                new PlayerStateNotificationMessage { PlayerId = playerId, State = (byte)state },
                DeliveryMethod.ReliableOrdered);
        }

        // Guest receiver for the host's PlayerStateNotificationMessage. Mirrors the confirmed
        // transition into this guest's engine roster sets (timing-vote exclusion)
        // and into the _players surface (PlayerCount / connection state / events stay consistent
        // with the host, like HandleLateJoinNotification). Host owns its own roster — reject a
        // forged guest notification. Idempotent: set Add/Remove and event re-fires are no-ops.
        private void HandlePlayerStateNotification(PlayerStateNotificationMessage msg)
        {
            if (IsHost)
                return;

            int playerId = msg.PlayerId;
            var player = FindPlayerById(playerId);

            switch ((PlayerStateChange)msg.State)
            {
                case PlayerStateChange.Disconnected:
                    _engine?.NotifyPlayerDisconnected(playerId);
                    if (player != null)
                    {
                        player.ConnectionState = PlayerConnectionState.Disconnected;
                        OnPlayerDisconnected?.Invoke(player);
                    }
                    break;

                case PlayerStateChange.Reconnected:
                    _engine?.NotifyPlayerReconnected(playerId);
                    if (player != null)
                    {
                        player.ConnectionState = PlayerConnectionState.Connected;
                        OnPlayerReconnected?.Invoke(player);
                    }
                    break;

                case PlayerStateChange.Left:
                    _engine?.NotifyPlayerLeft(playerId);
                    if (player != null)
                    {
                        int prevPlayerCount = _players.Count;
                        bool prevAllReady = AllPlayersReady;
                        _players.Remove(player);
                        OnPlayerLeft?.Invoke(player);
                        RaisePlayerCountIfChanged(prevPlayerCount);
                        RaiseAllPlayersReadyIfChanged(prevAllReady);
                    }
                    break;
            }

            _logger?.KInformation($"[KlothoNetworkService][HandlePlayerStateNotification] playerId={playerId}, state={(PlayerStateChange)msg.State}");
        }

        #endregion

        #region Reconnect: timeout

        private void CheckDisconnectedPlayerTimeout()
        {
            if (_disconnectedPlayerCount == 0)
                return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            for (int i = 0; i < _disconnectedPlayerInfoPool.Length; i++)
            {
                var info = _disconnectedPlayerInfoPool[i];
                if (!info.IsActive)
                    continue;

                if (now - info.DisconnectTimeMs <= _sessionConfig.ReconnectTimeoutMs)
                    continue;

                int playerId = info.PlayerId;
                info.Reset();
                _disconnectedPlayerCount--;

                var player = FindPlayerById(playerId);
                if (player != null)
                {
                    int prevPlayerCount = _players.Count;
                    bool prevAllReady = AllPlayersReady;
                    _players.Remove(player);
                    _engine?.NotifyPlayerLeft(playerId);
                    OnPlayerLeft?.Invoke(player);
                    RaisePlayerCountIfChanged(prevPlayerCount);
                    RaiseAllPlayersReadyIfChanged(prevAllReady);
                    BroadcastPlayerState(playerId, PlayerStateChange.Left);

                    // A presumed-drop entry can reach this timeout with its
                    // transport still alive (the quorum-miss watchdog never saw transport
                    // loss). Cut the peer: otherwise the removed player keeps sending — every
                    // cmd would re-insert a fresh _remoteTicks entry and keep flowing into the
                    // buffer/relay — and the client never learns it was removed (the cut lets
                    // it enter its reconnect flow). Removal first, then cut: the re-entrant
                    // HandlePeerDisconnected finds no player and only cleans peer mappings.
                    if (IsHost)
                    {
                        int alivePeerId = -1;
                        foreach (var kvp in _peerToPlayer)
                        {
                            if (kvp.Value == playerId) { alivePeerId = kvp.Key; break; }
                        }
                        if (alivePeerId != -1)
                            _transport?.DisconnectPeer(alivePeerId);
                    }
                }
            }
        }

        internal void CheckChainStallTimeout()
        {
            if (_engine == null || _simConfig == null) return;
            if (_engine.State.IsEnded()) return;
            if (Phase != SessionPhase.Playing) return;
            if (_simConfig.TickIntervalMs <= 0) return;

            int lag = _engine.CurrentTick - _engine.LastVerifiedTick;
            int reconnectTimeoutTicks = _sessionConfig.ReconnectTimeoutMs / _simConfig.TickIntervalMs;
            int threshold = System.Math.Max(reconnectTimeoutTicks + 100, _sessionConfig.MinStallAbortTicks);
            if (lag < threshold) return;

            _logger?.KWarning($"[KlothoNetworkService][ChainStallWatchdog] Aborting match — lag={lag} >= threshold={threshold} " +
                                 $"(ReconnectTimeoutMs={_sessionConfig.ReconnectTimeoutMs}, MinStallAbortTicks={_sessionConfig.MinStallAbortTicks}, " +
                                 $"TickIntervalMs={_simConfig.TickIntervalMs}, CurrentTick={_engine.CurrentTick}, LastVerifiedTick={_engine.LastVerifiedTick})");

            _engine.AbortMatch(AbortReason.ChainStallTimeout);

            if (_reconnectState != ReconnectState.None)
                _reconnectState = ReconnectState.Failed;
            Phase = SessionPhase.Disconnected;
        }

        #endregion

        #region Reconnect: Guest transport reconnect

        /// <summary>
        /// Fault-injection only — forces in-session reconnect arming on a guest, mirroring the
        /// eligible branch of <see cref="HandleDisconnected"/> but without its
        /// Phase==Playing / reason==NetworkFailure gate. The harness severs the guest with
        /// DisconnectPeer (→ LocalDisconnect, not reconnect-eligible), so the natural arm never
        /// fires; calling this drives the same self-reconnect state machine
        /// (<see cref="UpdateReconnect"/> → <see cref="TryReconnectTransport"/>) the production
        /// NetworkFailure path uses. Sets the timer fields too, else UpdateReconnect's elapsed
        /// math would immediately time out.
        /// </summary>
        internal void ArmInSessionReconnectForFaultInjection()
        {
            if (IsHost) return;

            _reconnectState = ReconnectState.WaitingForTransport;
            _reconnectStartTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _reconnectRetryCount = 0;
            OnReconnecting?.Invoke();
            _engine?.PauseForReconnect();
        }

        private void TryReconnectTransport()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - _lastTransportReconnectTime < TRANSPORT_RECONNECT_INTERVAL_MS)
                return;

            _lastTransportReconnectTime = now;
            if (!_transport.Connect(_transport.RemoteAddress, _transport.RemotePort))
            {
                _logger?.KError($"[KlothoNetworkService] Reconnect transport start failed — aborting reconnect");
                _reconnectState = ReconnectState.None;
                Phase = SessionPhase.Disconnected;
            }
        }

        private void SendReconnectRequest()
        {
            _reconnectRequestCache.SessionMagic = _sessionMagic;
            _reconnectRequestCache.PlayerId = LocalPlayerId;
            _reconnectRequestCache.DeviceId = GetDeviceId();
            _logger?.KInformation($"[KlothoNetworkService] Sending ReconnectRequestMessage (playerId={_reconnectRequestCache.PlayerId}, deviceId='{_reconnectRequestCache.DeviceId}')");

            using (var serialized = _messageSerializer.SerializePooled(_reconnectRequestCache))
                _transport.Send(0, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);

            _reconnectRequestSentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        private void UpdateReconnect()
        {
            if (_reconnectState == ReconnectState.None)
                return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long elapsed = now - _reconnectStartTimeMs;

            if (elapsed > _sessionConfig.ReconnectTimeoutMs)
            {
                _reconnectState = ReconnectState.Failed;
                Phase = SessionPhase.Disconnected;
                OnReconnectFailed?.Invoke(ReconnectRejectReason.TimedOut);
                return;
            }

            switch (_reconnectState)
            {
                case ReconnectState.WaitingForTransport:
                    if (_transport.IsConnected)
                    {
                        _reconnectState = ReconnectState.SendingRequest;
                        SendReconnectRequest();
                    }
                    else
                    {
                        TryReconnectTransport();
                    }
                    break;

                case ReconnectState.SendingRequest:
                    if (now - _reconnectRequestSentTime > RECONNECT_REQUEST_TIMEOUT_MS)
                    {
                        _reconnectRetryCount++;
                        if (_reconnectRetryCount > _sessionConfig.ReconnectMaxRetries)
                        {
                            _reconnectState = ReconnectState.Failed;
                            Phase = SessionPhase.Disconnected;
                            OnReconnectFailed?.Invoke(ReconnectRejectReason.MaxRetries);
                            return;
                        }
                        SendReconnectRequest();
                    }
                    break;

                case ReconnectState.WaitingForFullState:
                    if (now - _fullStateRequestTime > RECONNECT_FULLSTATE_TIMEOUT_MS)
                    {
                        _reconnectRetryCount++;
                        if (_reconnectRetryCount > _sessionConfig.ReconnectMaxRetries)
                        {
                            _reconnectState = ReconnectState.Failed;
                            Phase = SessionPhase.Disconnected;
                            OnReconnectFailed?.Invoke(ReconnectRejectReason.MaxRetries);
                            return;
                        }
                        if (_transport.IsConnected)
                        {
                            _reconnectState = ReconnectState.SendingRequest;
                            SendReconnectRequest();
                        }
                        else
                        {
                            _reconnectState = ReconnectState.WaitingForTransport;
                        }
                    }
                    break;
            }
        }

        #endregion

        #region Reconnect: Guest reconnect accept/reject handling

        private void HandleReconnectAccept(ReconnectAcceptMessage msg)
        {
            if (_reconnectState != ReconnectState.SendingRequest)
                return;   // KlothoConnection bypass possible — primary apply via InitializeFromConnection pending buffer.

            LocalPlayerId = msg.PlayerId;
            _sharedClock = new SharedTimeClock(msg.SharedEpoch, msg.ClockOffset);
            // Preserve identity across the reconnect roster rebuild (IsReady stays false).
            // Re-verify every player's propagated ticket (P2P full-state is host-authored).
            RebuildPlayerList(msg.Roster, useReady: false, useNames: true, rosterTickets: msg.RosterTickets);

            _reconnectState = ReconnectState.WaitingForFullState;
            _fullStateRequestTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Defensive — primary application happens via InitializeFromConnection pending buffer.
            // Warm Reconnect (this handler reached): engine reused → overwrites prior value.
            // Caller responsibility: this handler is reached only when engine is wired.
            _engine.ApplyExtraDelay(msg.RecommendedExtraDelay, ExtraDelaySource.Reconnect);
        }

        // useReady lets a normal (lobby) join carry per-player IsReady so a newly joining guest sees the
        // existing Ready flags; LateJoin/Reconnect leave IsReady false (entry.ReadyState meaningful only
        // on normal join). useNames reads the roster's authoritative identity; the LateJoin handler omits
        // it (P2P carries identity via a separate Notification).
        // rosterTickets: per-player original tickets index-parallel to roster, present on the
        // wire only when the propagation gate is on (SyncComplete/LateJoinAccept/ReconnectAccept). When
        // supplied, each entry is re-verified independently and the ticket-derived identity adopted.
        // Re-verification runs inside the build loop, BEFORE the count/ready events below (reverify
        // then publish), so no unverified value is ever observed.
        private void RebuildPlayerList(List<RosterEntry> roster, bool useReady = false, bool useNames = false, List<string> rosterTickets = null)
        {
            int prevPlayerCount = _players.Count;
            bool prevAllReady = AllPlayersReady;
            _players.Clear();
            for (int i = 0; i < roster.Count; i++)
            {
                var e = roster[i];
                string displayName = useNames ? e.DisplayName.ToString() : string.Empty;
                string account = useNames ? e.Account.ToString() : string.Empty;
                byte[] entitlement = null; // adopted from the re-verified ticket below
                // Re-verify the propagated ticket and adopt its identity/entitlement (shared helper).
                // Guard kept so the diff is byte-identical when no tickets are supplied (gate off).
                if (rosterTickets != null)
                {
                    string ticket = i < rosterTickets.Count ? rosterTickets[i] : null;
                    ResolveRosterEntryIdentity(e.PlayerId, ticket, ref account, ref displayName, ref entitlement);
                }
                _players.Add(new PlayerInfo
                {
                    PlayerId = e.PlayerId,
                    Entitlement = entitlement,
                    ConnectionState = (PlayerConnectionState)e.ConnectionState,
                    IsReady = useReady && e.ReadyState != 0,
                    DisplayName = displayName,
                    Account = account,
                });
                if (entitlement != null && entitlement.Length > 0)
                    _logger?.KInformation($"[KlothoNetworkService][Entitlement] loaded via ReconnectRebuild: playerId={e.PlayerId}, bytes={entitlement.Length}");
            }
            RaisePlayerCountIfChanged(prevPlayerCount);
            RaiseAllPlayersReadyIfChanged(prevAllReady);
        }

        // Re-verify a propagated original ticket and adopt its identity/entitlement into the refs.
        // For a peer OTHER than us we adopt the ticket's account/displayName (zero-trust — not the host relay).
        // For our OWN entry we keep the locally-known account/displayName (throwaway refs) but still extract our
        // own entitlement from the propagated, signature-verified ticket — otherwise this peer seeds its own
        // loadout from a null entitlement while every other peer derives the real bytes for us, diverging the
        // tick-0 seed. No-op when the gate is off / the ticket is absent (ReverifyAndAdoptIdentity guards).
        // Shared by RebuildPlayerList (normal join / warm reconnect) and SeedPlayersFromCatchupPayload
        // (late-join / cold-start reconnect) so the per-peer re-verification lives in exactly one place.
        private void ResolveRosterEntryIdentity(int playerId, string ticket, ref string account, ref string displayName, ref byte[] entitlement)
        {
            if (playerId != LocalPlayerId)
            {
                ReverifyAndAdoptIdentity(playerId, ticket, ref account, ref displayName, ref entitlement);
            }
            else
            {
                string ownAccount = account, ownDisplayName = displayName;
                ReverifyAndAdoptIdentity(playerId, ticket, ref ownAccount, ref ownDisplayName, ref entitlement);
            }
        }

        private void HandleReconnectReject(ReconnectRejectMessage msg)
        {
            if (_reconnectState == ReconnectState.None)
                return;

            _reconnectState = ReconnectState.Failed;
            Phase = SessionPhase.Disconnected;

            // Any reject reason invalidates persisted credentials — discard.
            _reconnectCredentialsStore?.Clear();

            OnReconnectFailed?.Invoke((ReconnectRejectReason)msg.Reason);
        }

        #endregion

        #region Reconnect: Host reconnect request handling

        private void SendReconnectReject(int peerId, byte reason)
        {
            _reconnectRejectCache.Reason = reason;
            using (var serialized = _messageSerializer.SerializePooled(_reconnectRejectCache))
                _transport.Send(peerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
        }

        private void HandleReconnectRequest(int peerId, ReconnectRequestMessage msg)
        {
            // 1. Validate magic
            if (msg.SessionMagic != _sessionMagic)
            {
                SendReconnectReject(peerId, 1); // Invalid magic
                return;
            }

            // 2. Validate PlayerId
            var info = FindDisconnectedInfo(msg.PlayerId);
            if (info == null)
            {
                SendReconnectReject(peerId, 2); // Invalid player
                return;
            }

            // 3. Validate timeout
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - info.DisconnectTimeMs > _sessionConfig.ReconnectTimeoutMs)
            {
                info.Reset();
                _disconnectedPlayerCount--;
                SendReconnectReject(peerId, 3); // Timed out
                return;
            }

            // 3.5. Validate deviceId (skip when not bound — info or msg empty)
            if (!string.IsNullOrEmpty(info.DeviceId) && info.DeviceId != msg.DeviceId)
            {
                SendReconnectReject(peerId, (byte)ReconnectRejectReason.DeviceMismatch);
                return;
            }

            // 4. Clean up stale peerId
            int stalePeerId = -1;
            foreach (var kvp in _peerToPlayer)
            {
                if (kvp.Value == msg.PlayerId)
                {
                    stalePeerId = kvp.Key;
                    break;
                }
            }
            if (stalePeerId >= 0)
            {
                _peerToPlayer.Remove(stalePeerId);
                _transport.DisconnectPeer(stalePeerId);
            }

            // 5. Accept the reconnect
            // Snapshot RTT sample before info.Reset() clears LastAvgRtt — used below to seed RecommendedExtraDelay.
            int reconnectAvgRtt = info.LastAvgRtt;
            _peerToPlayer[peerId] = msg.PlayerId;
            info.Reset();
            _disconnectedPlayerCount--;
            _engine?.NotifyPlayerReconnected(msg.PlayerId);
            BroadcastPlayerState(msg.PlayerId, PlayerStateChange.Reconnected);

            // 6. Send SimulationConfig (always sent regardless of cold-start vs warm reconnect).
            SendSimulationConfig(peerId);

            // 7. Send ReconnectAcceptMessage
            // Note: step 5 calls info.Reset() → IsPlayerDisconnected(msg.PlayerId) == false
            //       → the reconnect requester is reported as Connected (intentional)
            _reconnectAcceptCache.PlayerId = msg.PlayerId;
            _reconnectAcceptCache.CurrentTick = _engine?.CurrentTick ?? 0;
            _reconnectAcceptCache.SharedEpoch = _sharedClock.SharedEpoch;
            _reconnectAcceptCache.ClockOffset = 0;
            _reconnectAcceptCache.PlayerCount = _players.Count;
            _reconnectAcceptCache.Roster.Clear();            // reused cache — clear the roster
            _reconnectAcceptCache.RosterTickets.Clear();     // reused cache — clear before repopulate (else stale ticket bleed)
            for (int i = 0; i < _players.Count; i++)
            {
                bool isDisconnected = IsPlayerDisconnected(_players[i].PlayerId);
                _reconnectAcceptCache.Roster.Add(RosterEntry.FromPlayer(
                    _players[i], _logger,
                    isDisconnected ? (byte)PlayerConnectionState.Disconnected : (byte)PlayerConnectionState.Connected));
                // Index-parallel original tickets so the reconnector re-verifies every
                // player (P2P full-state is host-authored). Only when gate on → empty list otherwise.
                if (_propagateOriginalTickets)
                    _reconnectAcceptCache.RosterTickets.Add(_players[i].OriginalTicket ?? string.Empty);
            }

            // SessionConfig block — used by the cold-start guest to rebuild SessionConfig.
            _reconnectAcceptCache.RandomSeed = RandomSeed;
            _reconnectAcceptCache.MaxPlayers = _sessionConfig.MaxPlayers;
            _reconnectAcceptCache.MinPlayers = _sessionConfig.MinPlayers;
            _reconnectAcceptCache.MaxSpectators = _sessionConfig.MaxSpectators;
            _reconnectAcceptCache.AllowLateJoin = _sessionConfig.AllowLateJoin;
            _reconnectAcceptCache.LateJoinDelayTicks = _sessionConfig.LateJoinDelayTicks;
            _reconnectAcceptCache.ReconnectTimeoutMs = _sessionConfig.ReconnectTimeoutMs;
            _reconnectAcceptCache.ReconnectMaxRetries = _sessionConfig.ReconnectMaxRetries;
            _reconnectAcceptCache.LateJoinDelaySafety = _sessionConfig.LateJoinDelaySafety;
            _reconnectAcceptCache.RttSanityMaxMs = _sessionConfig.RttSanityMaxMs;
            _reconnectAcceptCache.MinStallAbortTicks = _sessionConfig.MinStallAbortTicks;
            _reconnectAcceptCache.CountdownDurationMs = _sessionConfig.CountdownDurationMs;
            _reconnectAcceptCache.AbortGraceMs = _sessionConfig.AbortGraceMs;
            _reconnectAcceptCache.EndGracePolicy = (int)_sessionConfig.EndGracePolicy;
            _reconnectAcceptCache.EndGraceMs = _sessionConfig.EndGraceMs;
            _reconnectAcceptCache.ClientShutdownGraceMs = _sessionConfig.ClientShutdownGraceMs;
            _reconnectAcceptCache.RecommendedExtraDelay =
                ComputeRecommendedExtraDelay(reconnectAvgRtt, msg.PlayerId, peerId, "Reconnect");

            using (var serialized = _messageSerializer.SerializePooled(_reconnectAcceptCache))
                _transport.Send(peerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);

            // 8. Send FullState
            OnFullStateRequested?.Invoke(peerId, _engine?.CurrentTick ?? 0);

            // 9. Register cold-start catchup so SpectatorInputMessage batches flow to the reconnecting peer.
            //    JoinTick = CurrentTick (immediate — existing PlayerId, no PlayerJoinCommand).
            //    IsReconnect=true skips OnLateJoinPlayerAdded (= PlayerJoinCommand insertion).
            //    LastSentTick = currentTick - 1 so the catchup batch starts at currentTick
            //    (= FullStateTick). Reconnect receiver's _lastVerifiedTick lands at FullStateTick - 1,
            //    so it needs FullStateTick's quorum to advance — but the receiver is still in
            //    _activePlayerIds (unlike a brand-new LateJoin player), so without this off-by-one
            //    fix the catchup misses sending FullStateTick's [other-player inputs] entirely and
            //    the receiver's chain stalls permanently at FullStateTick.
            int currentTick = _engine?.CurrentTick ?? 0;
            // JoinTick extends by InputDelay (+ host's RecommendedExtraDelay) to cover the
            // input slots whose P2P broadcasts the reconnect peer missed during disconnect.
            // Other players' input for tick T is broadcast at host tick (T - InputDelay -
            // host_extraDelay); the reconnect peer must catch up that window or its chain
            // stalls at JoinTick + 1 waiting for those inputs.
            int inputDelay = _engine?.InputDelay ?? 0;
            int recommendedExtraDelay = _engine?.RecommendedExtraDelay ?? 0;
            int joinTick = currentTick + inputDelay + recommendedExtraDelay;
            _lateJoinCatchups[peerId] = new LateJoinCatchupInfo
            {
                PeerId = peerId,
                PlayerId = msg.PlayerId,
                LastSentTick = currentTick - 1,
                JoinTick = joinTick,
                IsReconnect = true,
            };

            // Zombie cleanup: disconnect port-retry sibling sockets from the same reconnecting
            // client. KlothoConnection's reconnect pattern accepts multiple transport sockets;
            // only one carries the ReconnectRequest. Others linger as zombies, polluting
            // _transport.Broadcast delivery to the legit peer.
            //
            // Keep criteria (conservative — wrong cleanup permanently kills legit peers, and
            // client-side does NOT auto-retry):
            //   - _peerToPlayer: registered players (incl. just-reconnected one above)
            //   - _peerSyncStates: mid-handshake peers (cold-start sync / LateJoin sync)
            //   - _spectators: registered spectators (no _peerToPlayer entry by design)
            //
            // NOTE: _pendingPeers is intentionally NOT a keep collection. Zombie port-retry
            // peers also occupy _pendingPeers (they never deliver app data, so the Remove
            // at first-msg never fires). Protecting _pendingPeers would shield zombies and
            // defeat this cleanup. Legit Step 1 cold-start peers are protected only by the
            // bundle window narrowness below.
            //
            // Bundle filter: peer accepted within +/-BUNDLE_WINDOW_MS of reconnect peer's
            // accept time. Tighter than age-based — port-retry burst is ~tens of ms; unrelated
            // cold-start peers virtually never accept within the window of a reconnect event.
            const long BUNDLE_WINDOW_MS = 200;
            if (!_peerConnectedAtMs.TryGetValue(peerId, out var reconnectAcceptedAtMs))
            {
                reconnectAcceptedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            // Snapshot before iterating — DisconnectPeer may trigger synchronous unregister
            // on the transport side, mutating its internal peer collection.
            _zombieScanSnapshot.Clear();
            foreach (var id in _transport.GetConnectedPeerIds())
                _zombieScanSnapshot.Add(id);
            int cleanupCount = 0;
            for (int idx = 0; idx < _zombieScanSnapshot.Count; idx++)
            {
                int connectedPeerId = _zombieScanSnapshot[idx];
                if (connectedPeerId == peerId) continue;
                if (_peerToPlayer.ContainsKey(connectedPeerId)) continue;
                if (_peerSyncStates.ContainsKey(connectedPeerId)) continue;
                bool isSpectator = false;
                for (int i = 0; i < _spectators.Count; i++)
                {
                    if (_spectators[i].PeerId == connectedPeerId) { isSpectator = true; break; }
                }
                if (isSpectator) continue;
                if (!_peerConnectedAtMs.TryGetValue(connectedPeerId, out var connAt))
                    continue;
                long deltaMs = System.Math.Abs(connAt - reconnectAcceptedAtMs);
                if (deltaMs > BUNDLE_WINDOW_MS) continue;
                _logger?.KDebug($"[KlothoNetworkService][ZombieCleanup] peerId={connectedPeerId}, reason=bundle, deltaMs={deltaMs}");
                _transport.DisconnectPeer(connectedPeerId);
                cleanupCount++;
            }
            if (cleanupCount > 0)
                _logger?.KDebug($"[KlothoNetworkService][ZombieCleanupSummary] reconnectPeerId={peerId}, disconnected={cleanupCount}, bundleMs={BUNDLE_WINDOW_MS}");

            // 10. Restore player state + raise events
            var player = FindPlayerById(msg.PlayerId);
            if (player != null)
                player.ConnectionState = PlayerConnectionState.Connected;
            OnPlayerReconnected?.Invoke(player);

            _logger?.KInformation($"[KlothoNetworkService][HandleReconnectRequest] Player {msg.PlayerId} reconnected: peerId={peerId}");
        }

        #endregion
    }
}
