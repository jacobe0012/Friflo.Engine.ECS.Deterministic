using System;
using System.Collections.Generic;
using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    public partial class KlothoEngine
    {
        // Time sync
        private TimeSyncService _timeSync;
        private bool _timeSyncEnabled;
        // Per-remote-peer timing sample. remoteAdvantage is the sender's OWN measured
        // frame-advantage at send time (CommandMessage.SenderAdvantage) — used by
        // CalculateRemoteAdvantage to feed the true (exchanged) remote channel instead of a mirror.
        private readonly Dictionary<int, (int remoteTick, int remoteAdvantage, int receivedAtTick)> _remoteTicks =
            new Dictionary<int, (int remoteTick, int remoteAdvantage, int receivedAtTick)>();

        // One-shot throttle budget in SKIPPED-TICK (tick-time) units. A wait
        // recommendation is consumed one skipped tick at a time; once exhausted, at least
        // one tick MUST execute before the throttle can re-engage. Without this the
        // throttle is a permanent latch: its gate inputs (window means, idle window)
        // update only inside tick execution.
        private int _throttleBudget;     // remaining skipped ticks this wait may consume
        private bool _throttleNeedsTick; // budget exhausted — re-engage only after a tick runs

        #region TimeSync

        private void HandleFrameAdvantage(int playerId, int senderTick, int senderAdvantage)
        {
            // Defense-in-depth: _remoteTicks must never contain the local player —
            // a self entry collapses the 2-peer median to our own tick.
            if (playerId == LocalPlayerId) return;
            // Non-roster senders get no timing vote: a timeout-removed peer
            // whose transport survived keeps sending — every cmd would re-insert a fresh
            // entry (NotifyPlayerLeft's removal is one-shot, no disconnected-set membership,
            // staleness defeated by the per-cmd refresh).
            if (!_activePlayerIds.Contains(playerId)) return;
            // Proxy-timing broadcasts are already filtered at the wire layer (KlothoNetworkService
            // skips the invoke when IsProxyTiming is set), so senderAdvantage here is
            // always the slot owner's own measurement.
            _remoteTicks[playerId] = (senderTick, senderAdvantage, CurrentTick);
        }

        // Median tick across live remote peers -> CurrentTick - median = how far LOCAL is ahead.
        private float CalculateLocalAdvantage()
        {
            Span<int> valid = _remoteTicks.Count > 0 ? stackalloc int[_remoteTicks.Count] : default;
            int validCount = CollectLiveRemote(valid, useAdvantage: false);
            if (validCount == 0) return 0;
            return CurrentTick - Median(valid, validCount);
        }

        // Median of the remote peers' OWN measured advantage (received over the
        // wire). Returns false when no live remote advantage is available — the caller then falls
        // back to the legacy mirror (+localAdv). The caller negates this value for the remote
        // channel: a remote that is behind us reports a negative advantage, and the remote channel
        // needs `our - their` = +localAdv, so AdvanceFrame gets -CalculateRemoteAdvantage.
        private bool TryCalculateRemoteAdvantage(out float remoteAdvantage)
        {
            Span<int> valid = _remoteTicks.Count > 0 ? stackalloc int[_remoteTicks.Count] : default;
            int validCount = CollectLiveRemote(valid, useAdvantage: true);
            if (validCount == 0)
            {
                remoteAdvantage = 0;
                return false;
            }
            remoteAdvantage = Median(valid, validCount);
            return true;
        }

        // Collects (sorted, insertion) the remote tick OR advantage of every live remote peer —
        // disconnected peers and stale samples excluded — into `dst`. Returns the count.
        private int CollectLiveRemote(Span<int> dst, bool useAdvantage)
        {
            if (_remoteTicks.Count == 0) return 0;
            int staleThreshold = _simConfig.MaxRollbackTicks;
            int validCount = 0;
            foreach (var kvp in _remoteTicks)
            {
                // Disconnected peers' ticks are frozen. The staleness gate below cannot
                // expire them on its own: a frozen remote throttles CurrentTick, which keeps
                // (CurrentTick - receivedAtTick) under the threshold — a self-reinforcing
                // stall (this surfaced once the local self-entry stopped masking it).
                if (_disconnectedPlayerIds.Contains(kvp.Key))
                    continue;
                var (remoteTick, remoteAdvantage, receivedAtTick) = kvp.Value;
                if (CurrentTick - receivedAtTick > staleThreshold)
                    continue;
                int value = useAdvantage ? remoteAdvantage : remoteTick;
                // Insertion sort (player count is small).
                int i = validCount;
                while (i > 0 && dst[i - 1] > value)
                {
                    dst[i] = dst[i - 1];
                    i--;
                }
                dst[i] = value;
                validCount++;
            }
            return validCount;
        }

        // Even-count median averages the two middle values (no upper-value bias that
        // under-throttled a 3-player match — always an even remote set). Returned as float; only
        // the wire SenderAdvantage rounds, the local window mean keeps the fraction.
        private static float Median(Span<int> sorted, int count)
        {
            if ((count & 1) == 1)
                return sorted[count / 2];
            return (sorted[count / 2 - 1] + sorted[count / 2]) / 2f;
        }

        // Shared advantage sampler: feeds the window with the local advantage and the true
        // (exchanged) remote advantage, falling back to the legacy mirror when no remote sample
        // has arrived yet (gradual, regression-safe transition).
        private void SampleAdvantageFrame()
        {
            float localAdv = CalculateLocalAdvantage();
            float remoteAdv = TryCalculateRemoteAdvantage(out float r) ? -r : localAdv;
            _timeSync.AdvanceFrame(-localAdv, remoteAdv);
        }

        public void EnableTimeSync()
        {
            _timeSyncEnabled = true;
            _timeSync.Reset();
            _remoteTicks.Clear();
            _throttleBudget = 0;
            _throttleNeedsTick = false;
        }

        public void DisableTimeSync()
        {
            _timeSyncEnabled = false;
        }

        public bool IsTimeSyncEnabled => _timeSyncEnabled;
        public ITimeSyncService TimeSyncService => _timeSync;

        #endregion
    }
}
