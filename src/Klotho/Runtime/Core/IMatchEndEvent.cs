using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Marker for synced events that signal a deterministic match end.
    /// Must be combined with EventMode.Synced. Engine raises OnMatchEnded
    /// only after the event is verified.
    /// </summary>
    public interface IMatchEndEvent
    {
        /// <summary>
        /// Single winning PlayerId. -1 for draw / no-winner.
        /// Games with team or multi-winner outcomes should expose a richer
        /// accessor via a subinterface and let the server cast for those.
        /// </summary>
        int WinnerPlayerId { get; }

        /// <summary>
        /// Deterministic short reason string (e.g., "stocks", "timeout").
        /// Stable across versions — used as a telemetry key.
        /// FixedString32 keeps the accessor GC-free.
        /// </summary>
        FixedString32 Reason { get; }
    }
}
