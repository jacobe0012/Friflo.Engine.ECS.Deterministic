using System;
using xpTURN.Klotho.Logging;
using System.Collections.Generic;

using xpTURN.Klotho.Diagnostics;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Tracks reliable commands issued via <see cref="IKlothoEngine.IssueOnce"/>. Per-player retry /
    /// escalation / duplicate-ack interpretation. Subscribes to engine OnTickExecuted for retry due
    /// checks (post-tick hook avoids OnPollInput same-tick slot race) and OnCommandRejected for
    /// wire-level ack / escalation. FaultInjection drop / force-retry hooks evaluated internally.
    /// </summary>
    internal sealed class ReliableCommandTracker
    {
        private readonly KlothoEngine _engine;
        private readonly IKLogger _logger;
        private readonly Dictionary<int, ReliableCommandHandle> _handles
            = new Dictionary<int, ReliableCommandHandle>();

        public ReliableCommandTracker(KlothoEngine engine, IKLogger logger)
        {
            _engine = engine;
            _logger = logger;
        }

        internal void Attach()
        {
            _engine.OnTickExecuted += HandleTickExecuted;
            _engine.OnCommandRejected += HandleCommandRejected;
            _engine.OnResyncCompleted += HandleResyncCompleted;
        }

        internal void Detach()
        {
            _engine.OnTickExecuted -= HandleTickExecuted;
            _engine.OnCommandRejected -= HandleCommandRejected;
            _engine.OnResyncCompleted -= HandleResyncCompleted;

            // Cancel all outstanding handles so subscribers receive OnResolved teardown signal.
            foreach (var kv in _handles)
                kv.Value.Cancel();
            _handles.Clear();
        }

        public IReliableCommandHandle Issue(Func<ICommand> factory, ReliabilityPolicy policy)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            policy ??= ReliabilityPolicy.Default;

            int playerId = _engine.LocalPlayerId;

            // Replace any outstanding handle for this player (caller invariant: at most one IssueOnce per player).
            if (_handles.TryGetValue(playerId, out var existing) && !existing.IsResolved)
                existing.Cancel();

            var handle = new ReliableCommandHandle
            {
                Engine = _engine,
                Factory = factory,
                Policy = policy,
            };

            // Initial send. factory() must return a fresh CommandPool instance — framework takes ownership.
            var cmd = factory();
            handle.CommandTypeId = cmd.CommandTypeId;
            handle.IssueCount = 1;

            // Reliable-channel path: assign a per-player monotonic sequence number, then submit to the
            // authority, which assigns the execution tick. The client does not predict a tick, so there
            // is no retry/escalation here (HandleTickExecuted skips reliable-channel handles); the handle
            // resolves via Confirm (state-driven ack) or the wire dedup-ack. Falls back to the legacy
            // slot path when the reliable channel is unavailable — peer-to-peer, a server-side issue, or
            // a command that does not implement IReliableCommand.
            if (cmd is IReliableCommand reliable)
            {
                reliable.SequenceNumber = NextSequence(playerId);
                if (_engine.SubmitReliable(cmd))
                {
                    handle.ReliableChannel = true;
                    handle.AssignedSeq = reliable.SequenceNumber;
                    _handles[playerId] = handle;
                    _logger?.KInformation($"[KlothoEngine][Reliable] Submit: playerId={playerId}, cmdType={cmd.GetType().Name}, seq={reliable.SequenceNumber}");
                    return handle;
                }
            }

            // Legacy slot path (P2P / server-side issue / non-reliable command).
            _engine.InputCommand(cmd, extraDelay: 0);
            handle.LastAttemptTick = _engine.CurrentTick;
            handle.OutstandingTargetTick = _engine.CurrentTick + _engine.InputDelay;

            _handles[playerId] = handle;

            _logger?.KInformation($"[KlothoEngine][Reliable] Issue: playerId={playerId}, cmdType={cmd.GetType().Name}, targetTick={handle.OutstandingTargetTick}");

            return handle;
        }

        // Per-player monotonic reliable-command sequence (client-assigned; the authority dedups by it).
        private readonly Dictionary<int, int> _nextSeq = new Dictionary<int, int>();
        private int NextSequence(int playerId)
        {
            _nextSeq.TryGetValue(playerId, out int next);
            _nextSeq[playerId] = next + 1;
            return next;
        }

        private void HandleTickExecuted(int executedTick)
        {
            int currentTick = _engine.CurrentTick;
            int inputDelay = _engine.InputDelay;

            // Snapshot keys to allow safe mutation (resolve / remove) during iteration.
            // Allocation here is bounded by outstanding-handle count (typically 1 in current use-case).
            int handleCount = _handles.Count;
            if (handleCount == 0) return;

            // Walk dictionary directly — current spec is at most 1 entry per player, and no mutation
            // happens inside this loop (retry path doesn't add/remove; only Detach / OnCommandRejected do).
            foreach (var kv in _handles)
            {
                int playerId = kv.Key;
                var handle = kv.Value;

                // Reliable-channel handles are placed by the authority and delivered reliably-ordered,
                // so they take no per-tick retry/escalation (they resolve via Confirm or wire dedup-ack).
                if (handle.ReliableChannel) continue;

                bool forced = FaultInjection.ForceSpawnRetryPlayerIds.Contains(playerId);
                if (handle.IsResolved && !forced) continue;

                bool dueCooldown = handle.LastAttemptTick < 0
                                || currentTick >= handle.LastAttemptTick + handle.Policy.RetryIntervalTicks;
                if (!dueCooldown) continue;

                if (FaultInjection.DropSpawnCommandPlayerIds.Contains(playerId))
                {
                    handle.LastAttemptTick = currentTick;
                    _logger?.KWarning($"[FaultInjection][Reliable] cmd dropped: playerId={playerId}, tick={currentTick}, cmdTypeId={handle.CommandTypeId}");
                    continue;
                }

                var cmd = handle.Factory();
                _engine.InputCommand(cmd, extraDelay: handle.CurrentExtraDelay);
                handle.LastAttemptTick = currentTick;
                handle.OutstandingTargetTick = currentTick + inputDelay + handle.CurrentExtraDelay;
                handle.IssueCount++;

                if (forced)
                    _logger?.KWarning($"[FaultInjection][Reliable] Forced retry: playerId={playerId}, tick={currentTick}, cmdType={cmd.GetType().Name}");
                else
                    _logger?.KWarning($"[KlothoEngine][Reliable] Retry: playerId={playerId}, cmdType={cmd.GetType().Name}, targetTick={handle.OutstandingTargetTick}, issueCount={handle.IssueCount}, extraDelay={handle.CurrentExtraDelay}");
            }
        }

        // Forwards engine-public reject events to the matching outstanding handle. Filtering is by
        // CommandTypeId (LocalPlayerId path — server unicasts CommandRejectedMessage to the originator).
        private void HandleCommandRejected(int tick, int cmdTypeId, RejectionReason reason)
        {
            int playerId = _engine.LocalPlayerId;
            if (!_handles.TryGetValue(playerId, out var handle)) return;
            if (handle.CommandTypeId != cmdTypeId) return;
            if (handle.IsResolved) return;

            handle.RaiseRejected(reason);

            if (reason == RejectionReason.Duplicate && handle.Policy.TreatDuplicateAsAck)
            {
                _logger?.KInformation($"[KlothoEngine][Reliable] Duplicate ack: cmdTypeId={cmdTypeId} — handle resolved");
                ResolveAndRemove(playerId, handle);
                return;
            }

            if (reason == RejectionReason.PastTick && handle.Policy.TreatPastTickAsEscalation)
            {
                // Clear LastAttemptTick on both paths — immediate retry on next due check.
                // Brawler's original handler cleared cooldown unconditionally on PastTick; preserve that.
                handle.LastAttemptTick = -1;

                if (handle.CurrentExtraDelay < handle.Policy.ExtraDelayMax)
                {
                    int newDelay = Math.Min(handle.CurrentExtraDelay + handle.Policy.ExtraDelayStep, handle.Policy.ExtraDelayMax);
                    _logger?.KWarning($"[KlothoEngine][Reliable] PastTick escalate: cmdTypeId={cmdTypeId}, extraDelay {handle.CurrentExtraDelay}->{newDelay} (cap={handle.Policy.ExtraDelayMax})");
                    handle.CurrentExtraDelay = newDelay;
                }
                else
                {
                    if (!handle.CapHitLogged)
                    {
                        handle.CapHitLogged = true;
                        _logger?.KError($"[KlothoEngine][Reliable] PastTick cap hit: cmdTypeId={cmdTypeId}, extraDelay={handle.Policy.ExtraDelayMax} — server may be unreachable or RTT abnormal");
                    }
                    handle.CapHitRejectCount++;
                    _logger?.KDebug($"[KlothoEngine][Reliable] PastTick post-cap: cmdTypeId={cmdTypeId}, count={handle.CapHitRejectCount}, tick={tick}");
                }
            }

            // ToleranceExceeded / PeerMismatch — ignore (preserved behavior from brawler's original handler).
        }

        private void HandleResyncCompleted(int restoredTick)
        {
            foreach (var kv in _handles)
            {
                var handle = kv.Value;

                // A reliable-channel submit may have been lost across a resync/reconnect (reliable-ordered
                // delivery is per-connection). Re-submit unresolved handles with the SAME sequence number —
                // the authority dedups by (playerId, sequence), so a still-live original is harmless. The
                // commit tick is re-assigned by the authority on re-arrival.
                if (handle.ReliableChannel)
                {
                    if (handle.IsResolved) continue;
                    var cmd = handle.Factory();
                    if (cmd is IReliableCommand rc)
                        rc.SequenceNumber = handle.AssignedSeq;
                    if (!_engine.SubmitReliable(cmd))
                        CommandPool.Return(cmd);   // channel unavailable (mode flip) — drop, no leak
                    continue;
                }

                // Legacy slot path: clear cooldown so the next due check re-emits immediately.
                handle.LastAttemptTick = -1;
            }
        }

        private void ResolveAndRemove(int playerId, ReliableCommandHandle handle)
        {
            handle.MarkResolved();
            _handles.Remove(playerId);
        }
    }

    /// <summary>
    /// Concrete implementation of <see cref="IReliableCommandHandle"/>. Internal to the Core asmdef —
    /// the tracker has friend access to mutate state via internal properties / fields.
    /// </summary>
    internal sealed class ReliableCommandHandle : IReliableCommandHandle
    {
        public int CommandTypeId { get; internal set; }
        public int IssueCount { get; internal set; }
        public int CurrentExtraDelay { get; internal set; }
        public bool IsResolved { get; internal set; }
        public int OutstandingTargetTick { get; internal set; } = -1;

        internal IKlothoEngine Engine;
        internal Func<ICommand> Factory;
        internal ReliabilityPolicy Policy;
        internal int LastAttemptTick = -1;
        internal bool CapHitLogged = false;
        internal int CapHitRejectCount = 0;

        // True when this handle was submitted on the reliable channel (the authority assigns the
        // execution tick). Such handles do NOT retry/escalate — they resolve via Confirm (state-driven
        // ack) or wire dedup-ack. Legacy slot handles (ReliableChannel == false) keep the retry loop.
        internal bool ReliableChannel = false;

        // The per-player sequence number assigned at issue. Reused on resync re-submit so the authority
        // dedups (playerId, sequence) against a still-live original. -1 until a reliable-channel submit.
        internal int AssignedSeq = -1;

        public bool WouldCollideAt(int pollTick)
        {
            if (LastAttemptTick < 0) return false;
            if (Engine != null && pollTick + Engine.InputDelay == OutstandingTargetTick) return true;
            return false;
        }

        public void Confirm()
        {
            if (IsResolved) return;
            IsResolved = true;
            OnResolved?.Invoke();
        }

        public void Cancel()
        {
            if (IsResolved) return;
            IsResolved = true;
            OnResolved?.Invoke();
        }

        internal void MarkResolved()
        {
            if (IsResolved) return;
            IsResolved = true;
            OnResolved?.Invoke();
        }

        internal void RaiseRejected(RejectionReason reason)
        {
            OnRejected?.Invoke(reason);
        }

        public event Action<RejectionReason> OnRejected;
        public event Action OnResolved;
    }
}
