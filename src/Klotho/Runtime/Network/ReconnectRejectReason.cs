namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Reason for ReconnectRejectMessage.Reason, surfaced to the game via
    /// <see cref="xpTURN.Klotho.Core.ReconnectFailedException"/>.Reason and the OnReconnectFailed callback.
    /// Keep in sync with the reason switch in
    /// KlothoNetworkService.HandleReconnectReject / ServerDrivenClientService.HandleReconnectReject.
    ///
    /// Backwards-compat policy: the underlying type is byte and rides the wire via a cast. Append new
    /// reasons at the end. Never change an existing value (wire format with older clients would break).
    /// </summary>
    public enum ReconnectRejectReason : byte
    {
        InvalidMagic     = 1,
        InvalidPlayer    = 2,
        /// <summary>
        /// Timeout — raised by server reject (disconnect elapsed &gt; ReconnectTimeoutMs) OR by
        /// client-side elapsed-since-attempt-start exceeding the same threshold. Same user-visible
        /// semantics; caller distinction goes into the log message.
        /// </summary>
        TimedOut         = 3,
        AlreadyConnected = 4,
        DeviceMismatch   = 5,
        // ── Client-only reasons — not sent over wire (local OnReconnectFailed event raise only) ──
        TransportStartFailed = 6,   // transport reinit failed during reconnect
        MaxRetries           = 7,   // exhausted ReconnectMaxRetries
        Unknown              = 8,   // generic / unspecified failure
        NetworkFailure       = 9,   // connection lost during reconnect handshake (network failure)
    }

    public static class ReconnectRejectReasonExtensions
    {
        /// <summary>
        /// Returns the symbolic name for a reason (e.g. "InvalidMagic"). Game/UI layers should
        /// localize these via their own string table; the values here are stable identifiers.
        /// </summary>
        public static string ToName(this ReconnectRejectReason reason)
        {
            switch (reason)
            {
                case ReconnectRejectReason.InvalidMagic:         return "InvalidMagic";
                case ReconnectRejectReason.InvalidPlayer:        return "InvalidPlayer";
                case ReconnectRejectReason.TimedOut:             return "TimedOut";
                case ReconnectRejectReason.AlreadyConnected:     return "AlreadyConnected";
                case ReconnectRejectReason.DeviceMismatch:       return "DeviceMismatch";
                case ReconnectRejectReason.TransportStartFailed: return "TransportStartFailed";
                case ReconnectRejectReason.MaxRetries:           return "MaxRetries";
                case ReconnectRejectReason.Unknown:              return "Unknown";
                case ReconnectRejectReason.NetworkFailure:       return "NetworkFailure";
                default:                                         return "Unknown";
            }
        }

        /// <summary>
        /// Whether this reason indicates the same PlayerId is already held by another peer/device
        /// — game layer should offer a user choice (fall back to fresh join, or quit).
        /// </summary>
        public static bool RequiresUserChoice(this ReconnectRejectReason reason)
        {
            return reason == ReconnectRejectReason.AlreadyConnected;
        }

        /// <summary>
        /// Default English user-facing message for a reason. Games needing localization
        /// should write their own switch; this is a one-line fallback. Always returns non-null.
        /// </summary>
        public static string ToDefaultMessage(this ReconnectRejectReason reason)
        {
            switch (reason)
            {
                case ReconnectRejectReason.InvalidMagic:         return "Previous session has ended";
                case ReconnectRejectReason.InvalidPlayer:
                case ReconnectRejectReason.TimedOut:             return "Reconnect timed out";
                case ReconnectRejectReason.AlreadyConnected:     return "Already connected on another device";
                case ReconnectRejectReason.DeviceMismatch:       return "Device mismatch";
                case ReconnectRejectReason.TransportStartFailed: return "Network unavailable";
                case ReconnectRejectReason.MaxRetries:           return "Reconnect failed (max retries)";
                case ReconnectRejectReason.Unknown:              return "Reconnect failed";
                case ReconnectRejectReason.NetworkFailure:       return "Could not reach the host";
                default:                                         return "Reconnect failed";
            }
        }
    }
}
