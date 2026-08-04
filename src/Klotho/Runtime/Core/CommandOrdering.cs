namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Single source of truth for the deterministic ordering of non-slot list-channel commands
    /// (<see cref="ISystemCommand"/> and its <see cref="IReliableCommand"/> subtype) that share a
    /// tick. Both sort sites call this helper so they cannot drift:
    ///   - client execution: InputBuffer.GetCommandList (system/reliable append phase),
    ///   - server sim / replay record: KlothoEngine.s_commandComparer (system branch).
    ///
    /// Total-order key chain: OrderKey → CommandTypeId → PlayerId → SequenceNumber. The leading
    /// OrderKey preserves existing system-command intent (e.g. PlayerJoin); the trailing keys
    /// break OrderKey ties so List.Sort instability cannot produce divergent peer orderings.
    /// All keys are command-intrinsic (serialized / deterministic) — never wall-clock or
    /// authority-arrival order, which would not survive replay / rollback re-simulation.
    ///
    /// SCOPE: this compares two system/reliable commands only. The player-vs-system phase
    /// ("players first") and player-vs-player (PlayerId) ordering remain in each sort site as a
    /// separate invariant.
    /// </summary>
    public static class CommandOrdering
    {
        public static int Compare(ICommand a, ICommand b)
        {
            int c = OrderKeyOf(a).CompareTo(OrderKeyOf(b));
            if (c != 0) return c;

            c = a.CommandTypeId.CompareTo(b.CommandTypeId);
            if (c != 0) return c;

            c = a.PlayerId.CompareTo(b.PlayerId);
            if (c != 0) return c;

            return SequenceOf(a).CompareTo(SequenceOf(b));
        }

        // OrderKey is carried by every ISystemCommand; non-system arrivals (defensive) sort as 0.
        private static int OrderKeyOf(ICommand cmd)
            => cmd is ISystemCommand sys ? sys.OrderKey : 0;

        // SequenceNumber exists only on IReliableCommand; non-reliable system commands
        // (e.g. PlayerJoin) carry no seq and sort as 0.
        private static int SequenceOf(ICommand cmd)
            => cmd is IReliableCommand rel ? rel.SequenceNumber : 0;
    }
}
