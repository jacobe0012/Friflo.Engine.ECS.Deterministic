using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Host → guests (ReliableOrdered): the match is aborted — recovery ladder exhausted.
    /// Reason maps to Core.AbortReason.
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.MatchAbort)]
    public partial class MatchAbortMessage : NetworkMessageBase
    {
        [KlothoOrder]
        public byte Reason; // Core.AbortReason
    }
}
