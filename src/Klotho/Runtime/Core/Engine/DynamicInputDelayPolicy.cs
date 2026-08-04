using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Client-reactive Dynamic InputDelay fallback. Watches non-spawn PastTick rejects
    /// and rollback bursts, escalates engine.RecommendedExtraDelay when the server's
    /// push-based control is delayed/missed. Host is excluded by IsHost guard.
    /// Thresholds are sourced from SimulationConfig (server-authoritative).
    /// </summary>
    public sealed class DynamicInputDelayPolicy
    {
        private readonly IKlothoEngine _engine;
        private readonly IKLogger _logger;

        // Sliding window state (PastTick reject)
        private int _lastServerPushTick           = int.MinValue;
        private int _reactiveWindowStartTick      = int.MinValue;
        private int _reactiveRejectCount          = 0;

        // Rollback burst state
        private int _lastRollbackBurstWindowStartTick = int.MinValue;
        private int _rollbackCountInWindow            = 0;
        private int _lastReactiveEscalateTick         = int.MinValue;

        // De-escalation dwell reference. Updated on every PastTick reject (genuine
        // instability) and on every escalation; routine individual rollbacks do NOT reset it (only a
        // burst escalation does), else P2P decay would starve. Decay fires after ReactiveDeEscalateStableTicks.
        private int _lastReactiveActivityTick         = int.MinValue;

        // Re-entrancy guard: set around the policy's own Escalate/DeEscalate calls so the resulting
        // OnExtraDelayChanged is not mistaken for a server push (which would reset the grace window and
        // let reactive self-suppress). Only ApplyExtraDelay(DynamicPush) should update _lastServerPushTick.
        private bool _selfModifying;

        public DynamicInputDelayPolicy(IKlothoEngine engine, IKLogger logger = null)
        {
            _engine = engine;
            _logger = logger;
        }

        internal void Attach()
        {
            if (_engine.IsHost) return;
            _engine.OnCommandRejected   += HandleCommandRejected;
            _engine.OnRollbackExecuted  += HandleRollback;
            _engine.OnExtraDelayChanged += HandleExtraDelayChanged;
            _engine.OnTickExecuted      += HandleTickExecuted;
        }

        internal void Detach()
        {
            if (_engine.IsHost) return;
            _engine.OnCommandRejected   -= HandleCommandRejected;
            _engine.OnRollbackExecuted  -= HandleRollback;
            _engine.OnExtraDelayChanged -= HandleExtraDelayChanged;
            _engine.OnTickExecuted      -= HandleTickExecuted;
        }

        // Only a genuine server push (DynamicPush) opens the grace window. The policy's own
        // escalate/decay also fire OnExtraDelayChanged; the _selfModifying guard skips those so they
        // don't reset the grace window and self-suppress reactive.
        private void HandleExtraDelayChanged(int newDelay)
        {
            if (_selfModifying) return;
            _lastServerPushTick = _engine.CurrentTick;
        }

        // Stable-interval reactive de-escalation. CurrentTick-difference (not an increment counter)
        // so resim/rollback re-firing of OnTickExecuted does not corrupt the elapsed measure.
        private void HandleTickExecuted(int executedTick)
        {
            if (_lastReactiveActivityTick == int.MinValue) return; // no reactive escalation yet
            var cfg = _engine.SimulationConfig;
            int now = _engine.CurrentTick;
            if (now - _lastReactiveActivityTick < cfg.ReactiveDeEscalateStableTicks) return;
            if (_engine.RecommendedExtraDelay <= 0) return; // nothing to decay

            _selfModifying = true;
            _engine.DeEscalateExtraDelay(cfg.ReactiveStep);
            _selfModifying = false;
            // Debounce: next decay step waits another full stable interval.
            _lastReactiveActivityTick = now;
        }

        private void HandleCommandRejected(int tick, int cmdTypeId, RejectionReason reason)
        {
            if (reason != RejectionReason.PastTick) return;
            HandleReactivePastTick(tick, cmdTypeId);
        }

        private void HandleReactivePastTick(int tick, int cmdTypeId)
        {
            var cfg = _engine.SimulationConfig;
            int currentTick = _engine.CurrentTick;

            // PastTick reject is genuine instability — reset the de-escalation dwell even at ceiling.
            _lastReactiveActivityTick = currentTick;

            // Ceiling reached — escalation is impossible; skip the bookkeeping and logs
            // entirely (same early-return as the rollback path).
            if (_engine.RecommendedExtraDelay >= cfg.ReactiveMax) return;

            if (_lastServerPushTick != int.MinValue
                && currentTick - _lastServerPushTick < cfg.ServerPushGraceTicks) return;

            // First-window guard: with the int.MinValue sentinel the subtraction overflows
            // negative and the window never initializes, so rejects accumulate match-wide
            // instead of sliding (same guard as the rollback path).
            if (_reactiveWindowStartTick == int.MinValue
                || currentTick - _reactiveWindowStartTick > cfg.ReactiveWindowTicks)
            {
                _reactiveRejectCount     = 0;
                _reactiveWindowStartTick = currentTick;
            }

            _reactiveRejectCount++;
            _logger?.KDebug(
                $"[DynamicInputDelay] PastTick trigger: count={_reactiveRejectCount}, cmdTypeId={cmdTypeId}, windowStart={_reactiveWindowStartTick}, currentTick={currentTick}");
            if (_reactiveRejectCount < cfg.ReactiveEscalateThreshold) return;

            // Escalation cooldown is shared with the rollback path — check AND set — so the two
            // triggers combined cannot stack more than one step per cooldown window.
            if (_lastReactiveEscalateTick != int.MinValue
                && currentTick - _lastReactiveEscalateTick <= cfg.ReactiveEscalateCooldownTicks) return;

            _selfModifying = true;
            _engine.EscalateExtraDelay(cfg.ReactiveStep, cfg.ReactiveMax);
            _selfModifying = false;
            _lastReactiveEscalateTick = currentTick;
            _logger?.KWarning(
                $"[DynamicInputDelay] Reactive escalate (PastTick): step={cfg.ReactiveStep}, max={cfg.ReactiveMax}, recommendedExtraDelay={_engine.RecommendedExtraDelay}");
            _reactiveRejectCount     = 0;
            _reactiveWindowStartTick = currentTick;
        }

        private void HandleRollback(int fromTick, int toTick)
        {
            var cfg = _engine.SimulationConfig;
            if (_engine.RecommendedExtraDelay >= cfg.ReactiveMax) return;

            int now = _engine.CurrentTick;

            if (_lastRollbackBurstWindowStartTick == int.MinValue
                || now - _lastRollbackBurstWindowStartTick > cfg.RollbackWindowTicks)
            {
                _lastRollbackBurstWindowStartTick = now;
                _rollbackCountInWindow            = 0;
            }
            _rollbackCountInWindow++;

            if (_rollbackCountInWindow >= cfg.RollbackBurstCount
                && (_lastReactiveEscalateTick == int.MinValue || now - _lastReactiveEscalateTick > cfg.ReactiveEscalateCooldownTicks)
                && (_lastServerPushTick == int.MinValue || now - _lastServerPushTick > cfg.ServerPushGraceTicks))
            {
                _selfModifying = true;
                _engine.EscalateExtraDelay(cfg.ReactiveStep, cfg.ReactiveMax);
                _selfModifying = false;
                _lastReactiveEscalateTick = now;
                // Burst escalation resets the de-escalation dwell; routine individual rollbacks do not.
                _lastReactiveActivityTick = now;
                _logger?.KWarning(
                    $"[DynamicInputDelay] Reactive escalate (rollback): rollbackCount={_rollbackCountInWindow}, depth={fromTick - toTick}, windowTicks={cfg.RollbackWindowTicks}, recommendedExtraDelay={_engine.RecommendedExtraDelay}");
            }
        }
    }
}
