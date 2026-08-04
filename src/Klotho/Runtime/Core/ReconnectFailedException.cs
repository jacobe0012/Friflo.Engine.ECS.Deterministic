using System;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Thrown by KlothoConnection / KlothoConnectionAsync paths when a cold-start Reconnect attempt
    /// is rejected by the server. The Reason is one of <see cref="xpTURN.Klotho.Network.ReconnectRejectReason"/>
    /// values. Game catch handlers should branch on Reason instead of parsing Message.
    /// </summary>
    public sealed class ReconnectFailedException : Exception
    {
        public ReconnectRejectReason Reason { get; }

        public ReconnectFailedException(ReconnectRejectReason reason)
            : base($"Reconnect rejected: {reason.ToName()}")
        {
            Reason = reason;
        }
    }
}
