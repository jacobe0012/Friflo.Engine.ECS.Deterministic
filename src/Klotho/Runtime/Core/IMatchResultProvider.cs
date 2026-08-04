namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Optional richer accessor on a match-end event: implemented when the game authors a
    /// result payload (winner + stats/acquisitions) alongside the terminal signal. The server
    /// casts the <see cref="IMatchEndEvent"/> it receives in OnMatchEnded to this subinterface
    /// to read the payload; events that do not implement it yield no result (opt-in, no-regression).
    /// </summary>
    /// <remarks>
    /// The payload is an opaque game-owned blob — the core and wire carry only the bytes; the
    /// schema is defined and (de)serialized by the game. null = no result.
    /// </remarks>
    public interface IMatchResultProvider : IMatchEndEvent
    {
        /// <summary>Game-authored opaque result blob. null when the event carries no result.</summary>
        byte[] MatchResultData { get; }
    }
}
