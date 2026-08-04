using System;
using System.Collections.Generic;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.Core
{
    public partial class KlothoEngine
    {
        // Reusable buffers for event diff computation (avoids GC allocation).
        private readonly List<SimulationEvent> _rollbackOldEventsCache = new List<SimulationEvent>();
        private readonly List<SimulationEvent> _rollbackNewEventsCache = new List<SimulationEvent>();

        // Last tick at which OnSyncedEvent has been dispatched. Guards against re-fire across
        // rollback/resim cycles where _lastVerifiedTick rewinds and chain re-advances past
        // already-dispatched ticks. Reset by ApplyFullState (event buffer ClearAll cascade)
        // and by Spectator.ResetToTick (per-tick re-emit).
        private int _syncedDispatchHighWaterMark = -1;

        // True after OnMatchEnded has fired at least once. Drives the engine's Pause-grace
        // StopCommand emission at the OnPollInput dispatch site, exposed via IKlothoEngine.IsMatchEnded
        // for game-side hooks (UI / audio / Continue-policy input filtering), and used by the
        // ClientTick hard-limit warning to demote the log to Debug. Not reset within an engine instance.
        private bool _matchEndedDispatched;

        /// <inheritdoc />
        public bool IsMatchEnded => _matchEndedDispatched;

        #region Event System Helpers

        private void DispatchTickEvents(int tick, FrameState state)
        {
            var events = _eventBuffer.GetEvents(tick);
            if (state == FrameState.Verified)
            {
                DispatchSyncedEventsForTick(tick, events);
                for (int ei = 0; ei < events.Count; ei++)
                {
                    var evt = events[ei];
                    if (evt.Mode != EventMode.Synced)
                        _dispatcher.Dispatch(OnEventConfirmed, tick, evt, nameof(OnEventConfirmed));
                }
            }
            else
            {
                // On Predicted ticks, only fire Regular events; Synced events are kept in the buffer only.
                for (int ei = 0; ei < events.Count; ei++)
                {
                    var evt = events[ei];
                    if (evt.Mode == EventMode.Regular)
                        _dispatcher.Dispatch(OnEventPredicted, tick, evt, nameof(OnEventPredicted));
                }
            }
        }

        /// <summary>
        /// Dispatches all Synced events at <paramref name="tick"/> exactly once across the engine
        /// lifetime. Idempotent across rollback/resim: re-entry with the same tick (i.e.
        /// <paramref name="tick"/> &lt;= <c>_syncedDispatchHighWaterMark</c>) skips the entire batch.
        ///
        /// Ring-wrap guard: under a deep prediction stall the lead can reach the
        /// event-ring capacity (<c>MaxRollbackTicks + 2</c>), at which point tick T and tick
        /// T + capacity alias the same buffer slot — so the list handed in for <paramref name="tick"/>
        /// may actually hold a different tick's events. The collector stamps <c>evt.Tick</c> at raise
        /// time (<see cref="EventCollector"/>), so we dispatch only events whose own tick matches:
        /// this prevents firing a Synced event at the wrong tick (and re-firing it when the chain
        /// later reaches its real tick — the exactly-once violation). The wrapped-out earlier tick's
        /// events were already overwritten in the ring (surfaced by WarnIfReclaimingPendingSynced);
        /// they are lost rather than mis-fired, and the recovery ladder rebuilds state on a stall
        /// that deep.
        /// </summary>
        private void DispatchSyncedEventsForTick(int tick, IReadOnlyList<SimulationEvent> events)
        {
            if (tick <= _syncedDispatchHighWaterMark) return;
            bool anyFired = false;
            for (int i = 0; i < events.Count; i++)
            {
                var evt = events[i];
                if (evt.Mode != EventMode.Synced) continue;
                if (evt.Tick != tick) continue;  // ring-wrap: slot holds another tick's events — never fire here
                _dispatcher.Dispatch(OnSyncedEvent, tick, evt, nameof(OnSyncedEvent));
                if (evt is IMatchEndEvent endEvt)
                {
                    _matchEndedDispatched = true;
                    OnMatchEnded?.Invoke(tick, endEvt);
                }
                anyFired = true;
            }
            if (anyFired) _syncedDispatchHighWaterMark = tick;
        }

        /// <summary>
        /// <paramref name="dispatchedSyncedMark"/> is the Synced dispatch watermark captured by the
        /// caller BEFORE its re-advance/promotion loop raises it — Synced events above this boundary
        /// are dispatched exactly once by the verified chain/batch path and must not be misread as
        /// divergences; at or below it the exactly-once guard blocks dispatch, so set changes there
        /// are reported via OnSyncedEventDivergence.
        /// </summary>
        private void DiffRollbackEvents(int fromTick, int dispatchedSyncedMark)
        {
            // Collection of events newly gathered after re-simulation.
            _rollbackNewEventsCache.Clear();
            for (int t = fromTick; t < CurrentTick; t++)
            {
                var newEvents = _eventBuffer.GetEvents(t);
                for (int ei = 0; ei < newEvents.Count; ei++)
                    _rollbackNewEventsCache.Add(newEvents[ei]);
            }

            // Regular events that occurred before but disappeared after re-simulation are dispatched as
            // canceled. Already-dispatched Synced events (tick <= watermark) that disappeared are reported
            // as Removed divergence — the payload is a pooled old event, valid only during the callback.
            for (int oi = 0; oi < _rollbackOldEventsCache.Count; oi++)
            {
                var oldEvt = _rollbackOldEventsCache[oi];
                // The new cache spans [fromTick, CurrentTick). Old events at/after
                // CurrentTick are the truncated prediction tail — possible only when CurrentTick
                // shrank below the old-cache bound, i.e. the spectator path (re-sim halts at the
                // confirmed tick). On P2P/SD CurrentTick never shrinks here so this never fires.
                // The tail is not "disappeared" (re-prediction re-fires it; if confirmation removed
                // it, the consumer's state-based cleanup handles it) — skip to avoid a spurious
                // OnEventCanceled flicker. The caller still pool-returns these events.
                if (oldEvt.Tick >= CurrentTick)
                    continue;
                bool dispatchedSynced = oldEvt.Mode == EventMode.Synced && oldEvt.Tick <= dispatchedSyncedMark;
                if (oldEvt.Mode != EventMode.Regular && !dispatchedSynced)
                    continue;

                bool found = false;
                long oldHash = oldEvt.GetContentHash();
                for (int ni = 0; ni < _rollbackNewEventsCache.Count; ni++)
                {
                    var newEvt = _rollbackNewEventsCache[ni];
                    if (newEvt.Tick == oldEvt.Tick &&
                        newEvt.EventTypeId == oldEvt.EventTypeId &&
                        newEvt.GetContentHash() == oldHash)
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    if (dispatchedSynced)
                        ReportSyncedEventDivergence(oldEvt, SyncedDivergenceKind.Removed);
                    else
                        _dispatcher.Dispatch(OnEventCanceled, oldEvt.Tick, oldEvt, nameof(OnEventCanceled));
                }
            }

            // Newly appeared events are dispatched as Confirmed/Predicted depending on whether they are verified.
            // Synced events above the watermark are dispatched separately on the verified chain/batch path, so
            // they are skipped here; at or below it that path is blocked by the tick-level exactly-once guard,
            // so a new Synced event there is reported as Added divergence instead of being silently lost.
            for (int ni = 0; ni < _rollbackNewEventsCache.Count; ni++)
            {
                var newEvt = _rollbackNewEventsCache[ni];

                if (newEvt.Mode == EventMode.Synced && newEvt.Tick > dispatchedSyncedMark) continue;

                bool found = false;
                long newHash = newEvt.GetContentHash();
                for (int oi = 0; oi < _rollbackOldEventsCache.Count; oi++)
                {
                    var oldEvt = _rollbackOldEventsCache[oi];
                    if (oldEvt.Tick == newEvt.Tick &&
                        oldEvt.EventTypeId == newEvt.EventTypeId &&
                        oldEvt.GetContentHash() == newHash)
                    {
                        found = true;
                        break;
                    }
                }
                if (found)
                    continue;

                if (newEvt.Mode == EventMode.Synced)
                {
                    ReportSyncedEventDivergence(newEvt, SyncedDivergenceKind.Added);
                    // Match-end special case: notification alone would leave the match unendable.
                    // The _matchEndedDispatched guard preserves the exactly-once invariant.
                    if (newEvt is IMatchEndEvent endEvt && !_matchEndedDispatched)
                    {
                        _matchEndedDispatched = true;
                        OnMatchEnded?.Invoke(newEvt.Tick, endEvt);
                    }
                    continue;
                }

                FrameState evtState = newEvt.Tick <= _lastVerifiedTick
                    ? FrameState.Verified : FrameState.Predicted;
                if (evtState == FrameState.Verified)
                    _dispatcher.Dispatch(OnEventConfirmed, newEvt.Tick, newEvt, nameof(OnEventConfirmed));
                else
                    _dispatcher.Dispatch(OnEventPredicted, newEvt.Tick, newEvt, nameof(OnEventPredicted));
            }
        }

        private void ReportSyncedEventDivergence(SimulationEvent evt, SyncedDivergenceKind kind)
        {
            _logger?.KWarning($"[KlothoEngine][SyncedDivergence] {kind}: tick={evt.Tick}, type={evt.EventTypeId} — desync recovery changed already-dispatched Synced history; irreversible side effects may be stale");
            OnSyncedEventDivergence?.Invoke(evt.Tick, evt, kind);
        }

        #endregion
    }
}
