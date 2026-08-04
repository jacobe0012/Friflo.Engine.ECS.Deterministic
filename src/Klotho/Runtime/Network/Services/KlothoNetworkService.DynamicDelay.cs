using System;
using xpTURN.Klotho.Logging;
using System.Collections.Generic;
using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.Network
{
    public partial class KlothoNetworkService
    {
        // ── Mid-match dynamic InputDelay push ─────────────────────

        private readonly Dictionary<int, PlayerRttSmoother> _rttSmoothers = new Dictionary<int, PlayerRttSmoother>();
        // Per-peer target baseline = min(max(rttBased, reportedEffective), clampMax).
        // The broadcast value is the MAX over all peers (P2P uses one common delay = worst-case peer,
        // not the last-measured peer). _reportedEffective absorbs each guest's reactive report.
        private readonly Dictionary<int, int> _peerTargetBaseline = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _reportedEffective = new Dictionary<int, int>();
        private int _lastBroadcastBaseline = 0;
        private long _lastBroadcastTimeMs = 0;

        private const int EXTRA_DELAY_PUSH_THRESHOLD_UP = 2;
        private const int EXTRA_DELAY_PUSH_THRESHOLD_DOWN = 4;
        private const long MIN_PUSH_INTERVAL_MS = 500;

        private void MaybePushExtraDelayUpdate(int playerId, int peerId)
        {
            if (!IsHost) return;
            if (!_rttSmoothers.TryGetValue(playerId, out var smoother)) return;
            if (!smoother.TryGetSmoothedRtt(out int smoothedRtt)) return;

            // Pure compute — no per-sample log emit. Instance wrapper ComputeRecommendedExtraDelay
            // emits [KlothoNetworkService][{tag}] + [Metrics][{tag}] on every call and is reserved
            // for 1-shot seed events (LateJoin/Reconnect/Sync). Mid-match push calls the static
            // calculator directly so only real push events are logged.
            var (rttBased, _, _, _, _) = RecommendedExtraDelayCalculator.Compute(
                smoothedRtt,
                _simConfig.TickIntervalMs,
                _sessionConfig.LateJoinDelaySafety,
                _sessionConfig.RttSanityMaxMs,
                _simConfig.MaxRollbackTicks);

            // Absorb this peer's reported reactive correction and record its target baseline.
            int clampMax = _simConfig.MaxRollbackTicks / 2;
            int reported = _reportedEffective.TryGetValue(peerId, out var r) ? r : 0;
            _peerTargetBaseline[peerId] = System.Math.Min(System.Math.Max(rttBased, reported), clampMax);

            // Broadcast the MAX over all peers — a low-RTT peer must not drag the common delay down.
            int aggregate = 0;
            foreach (var kv in _peerTargetBaseline)
                if (kv.Value > aggregate) aggregate = kv.Value;

            int diff = aggregate - _lastBroadcastBaseline;
            int absDiff = diff >= 0 ? diff : -diff;
            int threshold = (diff > 0) ? EXTRA_DELAY_PUSH_THRESHOLD_UP : EXTRA_DELAY_PUSH_THRESHOLD_DOWN;
            if (absDiff < threshold) return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - _lastBroadcastTimeMs < MIN_PUSH_INTERVAL_MS) return;

            string reason = diff > 0 ? "threshold_up" : "threshold_down";
            PushExtraDelayUpdate(peerId, playerId, aggregate, smoothedRtt, _lastBroadcastBaseline, reason);
            _lastBroadcastBaseline = aggregate;
            _lastBroadcastTimeMs = now;
        }

        private void PushExtraDelayUpdate(int peerId, int playerId, int extraDelay, int avgRttMs, int prevDelay, string reason)
        {
            var msg = _recommendedExtraDelayCache;
            msg.RecommendedExtraDelay = extraDelay;
            msg.AvgRttMs = avgRttMs;
            // Broadcast to all peers and apply locally on the host. Transport.Broadcast does not
            // loop back to the sender, so the host needs the direct handler call to update its
            // own engine — same pattern as StartGame's GameStartMessage path.
            BroadcastMessagePooled(msg, DeliveryMethod.ReliableOrdered);
            HandleRecommendedExtraDelayUpdate(msg);

            _logger?.KDebug(
                $"[KlothoNetworkService][DynamicDelay] Push: targetPlayerId={playerId}, prev={prevDelay}, new={extraDelay}, avgRtt={avgRttMs}ms, reason={reason}");
            _logger?.KInformation(
                $"[Metrics][DynamicDelay] {{\"playerId\":{playerId},\"peerId\":{peerId},\"tag\":\"DynamicDelayPush\",\"avgRtt\":{avgRttMs},\"prevDelay\":{prevDelay},\"newDelay\":{extraDelay},\"reason\":\"{reason}\"}}");
        }

        private void HandleRecommendedExtraDelayUpdate(RecommendedExtraDelayUpdateMessage msg)
        {
            if (_engine == null) return;
            _engine.ApplyExtraDelay(msg.RecommendedExtraDelay, ExtraDelaySource.DynamicPush);
        }
    }
}
