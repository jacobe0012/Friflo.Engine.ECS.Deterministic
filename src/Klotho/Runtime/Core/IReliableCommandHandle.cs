using System;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Tracks a reliable command issued via <see cref="IKlothoEngine.IssueOnce"/>. The framework
    /// retries the command on the configured cadence and escalates lead margin on PastTick reject
    /// until either (a) the caller resolves via <see cref="Confirm"/> (state-driven ack), or
    /// (b) the framework receives a wire-level ack (Duplicate reject, when policy enables it),
    /// or (c) the caller abandons via <see cref="Cancel"/>.
    /// </summary>
    public interface IReliableCommandHandle
    {
        /// <summary>Command type id of the first sent cmd. Retries must produce the same type — used to filter reject events.</summary>
        int CommandTypeId { get; }

        /// <summary>Total number of sends (initial + retries).</summary>
        int IssueCount { get; }

        /// <summary>Per-command extra delay currently applied to retries. Bumped on PastTick reject by ExtraDelayStep, capped at ExtraDelayMax.</summary>
        int CurrentExtraDelay { get; }

        /// <summary>True once the handle has been resolved (via Confirm, Duplicate ack, or Cancel).</summary>
        bool IsResolved { get; }

        /// <summary>
        /// Target tick of the last sent retry that is currently outstanding on the wire
        /// (= sendTick + InputDelay + CurrentExtraDelay, where sendTick is the tick at which the
        /// last InputCommand was emitted). -1 if no send has happened yet. Used by the caller's
        /// OnPollInput to skip emitting per-tick filler cmds that would collide with this slot.
        /// </summary>
        int OutstandingTargetTick { get; }

        /// <summary>
        /// Returns true if a sender.Send at the given OnPollInput tick would collide with the
        /// outstanding retry's target tick (single cmd per (tick, playerId) slot, last write wins).
        /// </summary>
        bool WouldCollideAt(int pollTick);

        /// <summary>
        /// State-driven ack — caller observed the commit via the simulation frame (e.g. character entity exists).
        /// Fires <see cref="OnResolved"/>. Idempotent — subsequent calls after IsResolved are no-ops.
        /// </summary>
        void Confirm();

        /// <summary>Caller abandons retry (e.g. match ended before commit). Fires <see cref="OnResolved"/>.</summary>
        void Cancel();

        /// <summary>Fired when the framework receives a reject for this handle's cmd (regardless of whether it resolves).</summary>
        event Action<RejectionReason> OnRejected;

        /// <summary>Fired exactly once when the handle becomes resolved.</summary>
        event Action OnResolved;
    }
}
