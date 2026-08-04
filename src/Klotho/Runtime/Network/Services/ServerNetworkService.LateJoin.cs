using System;
using xpTURN.Klotho.Logging;
using System.Collections.Generic;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    public partial class ServerNetworkService
    {
        private const int LATE_JOIN_HANDSHAKE_TIMEOUT_MS = 10000;

        /// <summary>
        /// Starts the Late Join handshake when a new peer connects during Playing state.
        /// </summary>
        private void HandleLateJoin(int peerId)
        {
            if (_sessionConfig != null && !_sessionConfig.AllowLateJoin)
            {
                _logger?.KWarning($"[ServerNetworkService] Late join not allowed, peer {peerId} rejected");
                DisconnectWithReason(peerId, JoinFailReason.LateJoinDisabled.ToWireCode());
                return;
            }

            // Pending-aware capacity gate. Redundant with the gate in HandleDataReceived,
            // but kept as second-line defense for callers that may not pass through that dispatch.
            if (EffectivePlayerCount >= MaxPlayerCapacity)
            {
                _logger?.KWarning($"[ServerNetworkService][HandleLateJoin] Room full, peer {peerId} rejected: gameStarted={_gameStarted}, players={_players.Count}, assigned={_assignedPlayerIdCount}, pending={CountPendingHandshakes()}, max={MaxPlayerCapacity}");
                DisconnectWithReason(peerId, JoinFailReason.RoomFull.ToWireCode());
                return;
            }

            _logger?.KInformation($"[ServerNetworkService] Late join handshake started: peerId={peerId}");
            var state = new PeerSyncState
            {
                PeerId = peerId,
                SyncPacketsSent = 0,
                RttSamples = new long[NUM_SYNC_PACKETS],
                ClockOffsetSamples = new long[NUM_SYNC_PACKETS],
                Attempt = 0,
                Completed = false,
                IsLateJoin = true
            };
            _peerSyncStates[peerId] = state;
            SendSyncRequest(peerId, state);
        }

        /// <summary>
        /// Synchronizes after Late Join handshake completes (section 3.8.3).
        /// CompleteLateJoinSync: assign PlayerId → send FullState → register for broadcast.
        /// </summary>
        private void CompleteLateJoinSync(int peerId, PeerSyncState state)
        {
            // Identity validation gate — before slot reservation (reject ⇒ no slot consumed).
            // FinalizeLateJoin runs the rest (inline now, or from the poll-drain later for async redeem).
            RunJoinValidation(peerId, state, isLateJoin: true);
        }

        private void FinalizeLateJoin(int peerId, PeerSyncState state, bool validatorRan, string account, string displayName, byte[] entitlement)
        {
            // PlayerId allocation goes through TryReservePlayerSlot — the Post-GameStart
            // path bumps _nextPlayerId and _assignedPlayerIdCount; on overflow it rejects + cleans up.
            if (!TryReservePlayerSlot(peerId, out int newPlayerId))
                return;

            state.Completed = true;
            state.AwaitingValidation = false;

            state.GetBestSample(out int avgRtt, out long avgOffset);

            // Identity: validated value when a validator ran (claimed name ignored), else the
            // claimed name, else a fabricated fallback. Account stays empty unless validated.
            var newPlayer = new PlayerInfo
            {
                PlayerId = newPlayerId,
                DisplayName = ResolveJoinDisplayName(peerId, newPlayerId, validatorRan, displayName),
                Account = validatorRan ? (account ?? string.Empty) : string.Empty,
                Entitlement = validatorRan ? entitlement : null, // server-only; null when no validator ran
                Ping = avgRtt,
                IsReady = true
            };
            int prevCount = _players.Count;
            bool prevReady = AllPlayersReady;
            _players.Add(newPlayer);
            RaisePlayerCountIfChanged(prevCount);
            RaiseAllPlayersReadyIfChanged(prevReady);
            if (newPlayer.Entitlement != null && newPlayer.Entitlement.Length > 0)
                _logger?.KInformation($"[ServerNetworkService][Entitlement] loaded via LateJoin: playerId={newPlayerId}, bytes={newPlayer.Entitlement.Length}");
            _peerToPlayer[peerId] = newPlayerId;

            // 2. Calculate joinTick
            int joinTick = _engine.CurrentTick + _sessionConfig.LateJoinDelayTicks;

            // Hold the joiner's reliable commands (e.g. spawn) until joinTick so the join-time seed
            // always precedes them — the SD counterpart of the P2P joiner-side cmd.Tick floor.
            // Race-free: the client cannot submit before receiving the accept sent below.
            _inputCollector.SetReliablePlacementFloor(newPlayerId, joinTick);

            // Send SimulationConfig first. The client's KlothoConnection must receive it before initialization
            // so the handshake completes in the same order as the Normal Join path.
            SendSimulationConfig(peerId);

            // 4. Send LateJoinAcceptMessage
            int lateJoinSeedExtraDelay = ComputeRecommendedExtraDelay(avgRtt, newPlayerId, peerId, "LateJoin");
            var accept = new LateJoinAcceptMessage
            {
                PlayerId = newPlayerId,
                CurrentTick = _engine.CurrentTick,
                Magic = _sessionMagic,
                SharedEpoch = _sharedClock.SharedEpoch,
                ClockOffset = avgOffset,
                PlayerCount = _players.Count,
                RandomSeed = _randomSeed,
                MaxPlayers = _sessionConfig.MaxPlayers,
                MinPlayers = _sessionConfig.MinPlayers,
                MaxSpectators = _sessionConfig.MaxSpectators,
                AllowLateJoin = _sessionConfig.AllowLateJoin,
                LateJoinDelayTicks = _sessionConfig.LateJoinDelayTicks,
                ReconnectTimeoutMs = _sessionConfig.ReconnectTimeoutMs,
                ReconnectMaxRetries = _sessionConfig.ReconnectMaxRetries,
                LateJoinDelaySafety = _sessionConfig.LateJoinDelaySafety,
                RttSanityMaxMs = _sessionConfig.RttSanityMaxMs,
                MinStallAbortTicks = _sessionConfig.MinStallAbortTicks,
                CountdownDurationMs = _sessionConfig.CountdownDurationMs,
                AbortGraceMs = _sessionConfig.AbortGraceMs,
                EndGracePolicy = (int)_sessionConfig.EndGracePolicy,
                EndGraceMs = _sessionConfig.EndGraceMs,
                ClientShutdownGraceMs = _sessionConfig.ClientShutdownGraceMs,
                RecommendedExtraDelay = lateJoinSeedExtraDelay,
            };
            // Seed push baseline so the first mid-match recompute does not redundantly push the same value.
            _lastPushedExtraDelay[peerId] = lateJoinSeedExtraDelay;
            for (int i = 0; i < _players.Count; i++)
            {
                accept.Roster.Add(RosterEntry.FromPlayer(
                    _players[i], _logger, (byte)_players[i].ConnectionState));
            }

            // Concat existing player PlayerConfigs — new player is excluded as config is not yet received.
            // accept.Roster order and PlayerConfigLengths order must match (client parses i-th length ↔ Roster[i].PlayerId).
            int totalConfigBytes = 0;
            for (int i = 0; i < _players.Count; i++)
            {
                int pid = _players[i].PlayerId;
                if (pid == newPlayerId) { accept.PlayerConfigLengths.Add(0); continue; }
                if (_playerConfigBytes.TryGetValue(pid, out var bytes))
                {
                    accept.PlayerConfigLengths.Add(bytes.Length);
                    totalConfigBytes += bytes.Length;
                }
                else
                {
                    accept.PlayerConfigLengths.Add(0);
                }
            }
            if (totalConfigBytes > 0)
            {
                accept.PlayerConfigData = new byte[totalConfigBytes];
                int writeOffset = 0;
                for (int i = 0; i < _players.Count; i++)
                {
                    int pid = _players[i].PlayerId;
                    if (pid == newPlayerId) continue;
                    if (!_playerConfigBytes.TryGetValue(pid, out var bytes)) continue;
                    Buffer.BlockCopy(bytes, 0, accept.PlayerConfigData, writeOffset, bytes.Length);
                    writeOffset += bytes.Length;
                }
            }
            else
            {
                accept.PlayerConfigData = Array.Empty<byte>();
            }

            // Per-player server-verified entitlement bytes, index-parallel to Roster (same
            // concat+lengths encoding as PlayerConfigData). Covers the joiner itself plus any player
            // whose PlayerJoinCommand is still pending in the joiner's replay window (overlapping
            // late-joins) — the join-time seed callback reads these via GetPlayerEntitlement.
            accept.RosterEntitlementData = BuildRosterEntitlements(accept.RosterEntitlementLengths);

            using (var serialized = _messageSerializer.SerializePooled(accept))
            {
                _transport.Send(peerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
            }

            // 5. Send FullState (must be before broadcast registration)
            OnFullStateRequested?.Invoke(peerId, _engine.CurrentTick);

            // 6. Peer state: Playing — immediately included in BroadcastVerifiedState targets.
            //    Previously: CatchingUp → promoted via TryPromoteCatchingUpPeer (on first accepted input).
            //    Bug: late-join guest's initial inputs are outside the server's Hard Tolerance window and get rejected,
            //    causing the peer to never get promoted. Now goes directly to Playing like Normal Join.
            _peerStates[peerId] = new ServerPeerInfo
            {
                PeerId = peerId,
                PlayerId = newPlayerId,
                State = ServerPeerState.Playing,
                LastAckedTick = -1
            };

            // 7. Add player to InputCollector
            _inputCollector.AddPlayer(newPlayerId);

            // 8. PlayerJoinCommand — the engine handler schedules it into the input collector's
            //    system lane at joinTick (SD SendCommand is a no-op; unlike P2P there is no broadcast
            //    here — the command reaches clients inside the verified stream of tick joinTick).
            OnLateJoinPlayerAdded?.Invoke(newPlayerId, joinTick);

            // 9. Immediately resend the missed VerifiedState (lastVerifiedTick) — avoids hitting the guest client's Hard limit.
            //    The late-join client starts from fullStateTick but the server is already ahead,
            //    so if the latest VerifiedState is not sent immediately, `leadTicks` can accumulate
            //    until the next broadcast and hit the Hard limit.
            if (_lastVerifiedTick > 0 && _lastVerifiedBytes != null)
            {
                _transport.Send(peerId, _lastVerifiedBytes, _lastVerifiedBytesLength, DeliveryMethod.ReliableOrdered);
                _logger?.KInformation(
                    $"[ServerNetworkService] Initial VerifiedState sent to late-join peer {peerId}: tick={_lastVerifiedTick}");
            }

            // Notify existing peers of the new player so their _players list stays consistent.
            // Exclude the new joiner — they already received the full roster via LateJoinAcceptMessage.
            var ljNewInfo = FindPlayerById(newPlayerId);  // propagate the new player's authoritative identity
            var notification = new LateJoinNotificationMessage
            {
                PlayerId = newPlayerId,
                JoinTick = joinTick,
                Account = ljNewInfo?.Account ?? string.Empty,
                DisplayName = ljNewInfo?.DisplayName ?? string.Empty,
                // Server-verified entitlement bytes so connected clients seed the same deterministic
                // join-time state when the joinTick command executes (arrives before the tick-joinTick
                // verified state on the same ReliableOrdered stream).
                Entitlement = ljNewInfo?.Entitlement,
            };
            using (var notificationSerialized = _messageSerializer.SerializePooled(notification))
            {
                foreach (var kvp in _peerToPlayer)
                {
                    if (kvp.Key == peerId) continue;
                    _transport.Send(kvp.Key, notificationSerialized.Data, notificationSerialized.Length, DeliveryMethod.ReliableOrdered);
                }
                // Spectators are tracked separately (disjoint from _peerToPlayer).
                for (int i = 0; i < _spectators.Count; i++)
                {
                    _transport.Send(_spectators[i].PeerId, notificationSerialized.Data, notificationSerialized.Length, DeliveryMethod.ReliableOrdered);
                }
            }

            _logger?.KInformation(
                $"[ServerNetworkService] Late join complete: peerId={peerId}, playerId={newPlayerId}, joinTick={joinTick}");
            OnPlayerJoined?.Invoke(newPlayer);
        }

        // Builds roster-parallel entitlement propagation data (index-parallel to a roster built from
        // _players in the same order; concat bytes + per-entry lengths, the PlayerConfigData encoding).
        // Shared by the late-join and reconnect accept builders. Clears the lengths list first
        // (reconnect uses a reused cache instance).
        private byte[] BuildRosterEntitlements(List<int> lengths)
        {
            lengths.Clear();
            int total = 0;
            for (int i = 0; i < _players.Count; i++)
            {
                var ent = _players[i].Entitlement;
                int len = ent?.Length ?? 0;
                lengths.Add(len);
                total += len;
            }
            if (total == 0)
                return Array.Empty<byte>();

            var data = new byte[total];
            int offset = 0;
            for (int i = 0; i < _players.Count; i++)
            {
                var ent = _players[i].Entitlement;
                if (ent == null || ent.Length == 0) continue;
                Buffer.BlockCopy(ent, 0, data, offset, ent.Length);
                offset += ent.Length;
            }
            return data;
        }

        // Pending handshake count (LateJoin + general — both use _peerSyncStates).
        private int CountPendingHandshakes()
        {
            int count = 0;
            foreach (var kvp in _peerSyncStates)
            {
                if (!kvp.Value.Completed)
                    count++;
            }
            return count;
        }

        // Instance wrapper — config injection + path-tagged logging + structured metrics emission.
        // playerId = game-level player identifier (reported in metrics JSON for telemetry analysis).
        // peerId = connection-level identifier (kept in operational logs for connection debugging).
        // Pure computation lives in xpTURN.Klotho.Core.RecommendedExtraDelayCalculator (shared with P2P path).
        private int ComputeRecommendedExtraDelay(int avgRtt, int playerId, int peerId, string pathTag)
        {
            var (extraDelay, fallback, rttTicks, raw, clamped) = RecommendedExtraDelayCalculator.Compute(
                avgRtt,
                _simConfig.TickIntervalMs,
                _sessionConfig.LateJoinDelaySafety,
                _sessionConfig.RttSanityMaxMs,
                _simConfig.MaxRollbackTicks);

            if (fallback)
                _logger?.KWarning($"[ServerNetworkService][{pathTag}] FallbackPath: avgRtt={avgRtt}ms invalid, peerId={peerId}, clamped={extraDelay}");
            else
                _logger?.KDebug($"[ServerNetworkService][{pathTag}] RecommendedExtraDelay computed: peerId={peerId}, avgRtt={avgRtt}ms, clamped={extraDelay}");

            // Structured JSON-line metrics — single source of truth for verification scripts and
            // production telemetry. Bool literals lowercased for JSON validity. Path tag is controlled
            // ("LateJoin" / "Reconnect" / "Sync") so no escaping needed.
            string clampedStr = clamped ? "true" : "false";
            string fallbackStr = fallback ? "true" : "false";
            int safety = _sessionConfig.LateJoinDelaySafety;
            _logger?.KInformation($"[Metrics][{pathTag}] {{\"playerId\":{playerId},\"peerId\":{peerId},\"tag\":\"{pathTag}\",\"avgRtt\":{avgRtt},\"rttTicks\":{rttTicks},\"safety\":{safety},\"raw\":{raw},\"clamped\":{clampedStr},\"extraDelay\":{extraDelay},\"fallback\":{fallbackStr}}}");

            return extraDelay;
        }
    }
}
