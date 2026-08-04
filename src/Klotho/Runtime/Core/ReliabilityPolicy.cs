using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Per-command reliability policy controlling retry cadence, lead-margin escalation,
    /// and wire-reject ack interpretation. Used by <see cref="IKlothoEngine.IssueOnce"/>.
    /// Default values match the spawn-once use case (commit-exactly-once with PastTick
    /// escalation and Duplicate-as-ack).
    /// </summary>
    public sealed class ReliabilityPolicy
    {
        /// <summary>Tick gap between successive retries while the handle remains outstanding.</summary>
        public int RetryIntervalTicks = 20;

        /// <summary>extraDelay increment applied on PastTick reject.</summary>
        public int ExtraDelayStep = 4;

        /// <summary>extraDelay cap. Reaching this fires a single Error log and downgrades subsequent rejects to Debug.</summary>
        public int ExtraDelayMax = 40;

        /// <summary>Treat <see cref="RejectionReason.Duplicate"/> as an ack (resolves the handle).</summary>
        public bool TreatDuplicateAsAck = true;

        /// <summary>Treat <see cref="RejectionReason.PastTick"/> as an escalation trigger.</summary>
        public bool TreatPastTickAsEscalation = true;

        public static readonly ReliabilityPolicy Default = new ReliabilityPolicy();
    }
}
