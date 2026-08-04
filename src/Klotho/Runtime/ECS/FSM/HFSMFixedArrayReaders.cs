using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.ECS.FSM
{
    internal static class HFSMFixedArrayReaders
    {
        private static bool _registered;

        internal static void Register()
        {
            if (_registered) return;
            _registered = true;

            ComponentStorageRegistry.RegisterFixedArrayReader(
                typeof(HFSMComponent), nameof(HFSMState.ActiveStateIds),
                boxed =>
                {
                    var comp = (HFSMComponent)boxed;
                    var buf = new int[HFSMComponent.MaxDepth];
                    unsafe { for (int i = 0; i < HFSMComponent.MaxDepth; i++) buf[i] = comp.State.ActiveStateIds[i]; }
                    return buf;
                });

            ComponentStorageRegistry.RegisterFixedArrayReader(
                typeof(HFSMComponent), nameof(HFSMState.PendingEventIds),
                boxed =>
                {
                    var comp = (HFSMComponent)boxed;
                    var buf = new int[HFSMComponent.MaxPendingEvents];
                    unsafe { for (int i = 0; i < HFSMComponent.MaxPendingEvents; i++) buf[i] = comp.State.PendingEventIds[i]; }
                    return buf;
                });
        }
    }
}
