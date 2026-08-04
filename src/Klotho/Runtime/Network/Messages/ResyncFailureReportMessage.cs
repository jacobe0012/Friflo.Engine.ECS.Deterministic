using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Why a guest reports that determinism recovery is failing.
    /// Wire-stable byte values — append-only.
    /// </summary>
    public enum ResyncFailureReason : byte
    {
        /// <summary>A received full state was applied but the post-apply hash did not match.</summary>
        ApplyMismatch = 0,
        /// <summary>Full-state resync retries were exhausted (OnResyncFailed).</summary>
        RetryExhausted = 1,
    }

    /// <summary>
    /// Guest → host (Reliable): determinism recovery is failing on this peer. The host responds
    /// with a corrective reset (rung 3) and, when its attempt budget is exhausted, aborts the
    /// match (rung 4). Hash pair is diagnostic-only.
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.ResyncFailureReport)]
    public partial class ResyncFailureReportMessage : NetworkMessageBase
    {
        [KlothoOrder]
        public int PlayerId;

        [KlothoOrder]
        public int Tick;

        [KlothoOrder]
        public byte Reason; // ResyncFailureReason

        [KlothoOrder]
        public long LocalHash;

        [KlothoOrder]
        public long RemoteHash;
    }
}
