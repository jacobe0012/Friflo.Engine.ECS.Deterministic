namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Server-mode EventCollector. Stores only EventMode.Synced events, drops Regular ones.
    /// Regular events have no server-side subscribers (they are visualization hooks consumed on
    /// clients via deterministic re-simulation). Synced events are server-only and must reach
    /// the network layer for unicast feedback (e.g. CommandRejectedSimEvent → CommandRejectedMessage).
    /// Dropped Regular instances are returned to the EventPool at RaiseEvent (the event buffer owns
    /// the return for collected/Synced events; dropped ones have no buffer, so the collector returns).
    /// </summary>
    public sealed class SyncedOnlyEventCollector : EventCollector
    {
        public override void RaiseEvent(SimulationEvent evt)
        {
            if (evt == null)
                return;
            if (evt.Mode != EventMode.Synced)
            {
                // Dropped events have no server-side consumer and never reach the event buffer
                // (which owns the return for collected events) — return the rented instance
                // immediately, or it leaks out of the pool: release builds fall back to new T()
                // per event, DEBUG builds grow _outstanding unboundedly.
                EventPool.Return(evt);
                return;
            }
            base.RaiseEvent(evt);
        }
    }
}
