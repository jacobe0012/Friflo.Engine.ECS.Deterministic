namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Why a Room transitioned out of Active into Draining.
    /// Used by Room drain logging/metrics. Distinct from engine-level AbortReason —
    /// this enum classifies the room-lifecycle drain trigger, not the engine abort cause.
    /// </summary>
    public enum EndReason
    {
        /// <summary>Normal match end — fired via IMatchEndEvent (e.g., GameOverEvent).</summary>
        MatchEnded,

        /// <summary>Engine AbortMatch — chain stall, divergence, reconnect failure, etc.</summary>
        MatchAborted,
    }
}
