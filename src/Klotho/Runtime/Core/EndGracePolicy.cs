namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Simulation behavior during the post-match grace window.
    /// Selected per session via ISessionConfig.EndGracePolicy.
    /// </summary>
    public enum EndGracePolicy
    {
        /// <summary>
        /// (Default) Simulation keeps advancing ticks. KlothoState remains Running.
        /// Input/heartbeat/replay continuity preserved. GameOverFired guard prevents
        /// further match-end firings on the game side.
        /// </summary>
        Continue,

        /// <summary>
        /// Characters are halted at the match-end tick by emitting StopCommand on the
        /// deterministic input stream each tick (in place of game input). KlothoState
        /// remains Running and ticks keep advancing; network send/recv is preserved
        /// (heartbeat/keepalive). Use when post-result physics drift or stray events
        /// would clutter the result screen.
        /// </summary>
        Pause,
    }
}
