namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Simulation event dispatch mode.
    /// </summary>
    public enum EventMode
    {
        /// <summary>Predict-then-confirm/cancel lifecycle.</summary>
        Regular,
        /// <summary>Dispatched only on verified ticks.</summary>
        Synced
    }

    /// <summary>
    /// Kind of Synced-event divergence reported by OnSyncedEventDivergence:
    /// desync-recovery re-simulation changed the Synced event set at ticks at or below the
    /// already-dispatched watermark, where the exactly-once guard blocks (re-)dispatch.
    /// A changed payload at the same (tick, type) surfaces as a Removed + Added pair.
    /// </summary>
    public enum SyncedDivergenceKind
    {
        /// <summary>A Synced event appeared at an already-dispatched tick — it will never fire normally.</summary>
        Added,
        /// <summary>An already-dispatched Synced event disappeared — its side effects are stale.</summary>
        Removed
    }

    /// <summary>
    /// Base class for game events raised during the simulation.
    /// Subclasses define concrete event types (damage, spawn, death, etc.).
    /// </summary>
    public abstract class SimulationEvent
    {
        /// <summary>Event type identifier (similar to CommandType).</summary>
        public abstract int EventTypeId { get; }

        /// <summary>The tick on which this event occurred.</summary>
        public int Tick { get; set; }

        /// <summary>Regular or Synced dispatch mode.</summary>
        public virtual EventMode Mode => EventMode.Regular;

        /// <summary>
        /// Deterministic content hash (FNV-1a) used for duplicate matching.
        /// Subclasses must override to incorporate payload data.
        /// </summary>
        public virtual long GetContentHash()
        {
            return EventTypeId;
        }

        /// <summary>
        /// Reset for pool reuse.
        /// </summary>
        public virtual void Reset()
        {
            Tick = 0;
        }
    }
}
