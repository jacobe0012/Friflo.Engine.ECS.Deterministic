using System;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Thrown by KlothoConnection / KlothoConnectionAsync on the guest Connect path (normal / late join)
    /// when the join fails. The Reason is one of <see cref="xpTURN.Klotho.Network.JoinFailReason"/>
    /// values. Game catch handlers should branch on Reason instead of parsing Message.
    /// Cancellation surfaces as OperationCanceledException, not this type.
    /// </summary>
    public sealed class JoinFailedException : Exception
    {
        public JoinFailReason Reason { get; }

        public JoinFailedException(JoinFailReason reason)
            : base($"Join failed: {reason.ToName()}")
        {
            Reason = reason;
        }
    }
}
