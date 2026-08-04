namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Read/write implementation of ISessionConfig.
    /// Constructed from KlothoSessionSetup values inside KlothoSession.Create(),
    /// or from the deserialized result of GameStartMessage / LateJoinAcceptMessage.
    /// </summary>
    public class SessionConfig : ISessionConfig
    {
        // --- Determinism ---

        /// <inheritdoc />
        public int RandomSeed { get; set; } = 0;

        // --- Membership ---

        /// <inheritdoc />
        public int MaxPlayers { get; set; } = 4;

        /// <inheritdoc />
        public int MinPlayers { get; set; } = 2;

        /// <inheritdoc />
        public int MaxSpectators { get; set; } = 0;

        // --- LateJoin / Reconnect Policy ---

        /// <inheritdoc />
        public bool AllowLateJoin { get; set; } = true;

        /// <inheritdoc />
        public int LateJoinDelayTicks { get; set; } = 10;

        /// <inheritdoc />
        public int ReconnectTimeoutMs { get; set; } = 60000;

        /// <inheritdoc />
        public int ValidationTimeoutMs { get; set; } = 5000;

        /// <inheritdoc />
        public int ReconnectMaxRetries { get; set; } = 3;

        // --- LateJoin / Reconnect Tuning ---

        /// <inheritdoc />
        public int LateJoinDelaySafety { get; set; } = 2;

        /// <inheritdoc />
        public int RttSanityMaxMs { get; set; } = 240;

        // --- Chain-Stall Watchdog ---

        /// <inheritdoc />
        public int MinStallAbortTicks { get; set; } = 600;

        // --- Match Start Countdown ---

        /// <inheritdoc />
        public int CountdownDurationMs { get; set; } = 3000;

        // --- Match End Grace ---

        /// <inheritdoc />
        public int AbortGraceMs { get; set; } = 1500;

        /// <inheritdoc />
        public EndGracePolicy EndGracePolicy { get; set; } = EndGracePolicy.Continue;

        /// <inheritdoc />
        public int EndGraceMs { get; set; } = 5000;

        /// <inheritdoc />
        public int ClientShutdownGraceMs { get; set; } = 4500;
    }
}
