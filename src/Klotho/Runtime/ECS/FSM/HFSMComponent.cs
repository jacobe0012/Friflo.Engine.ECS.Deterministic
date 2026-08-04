using System.Runtime.InteropServices;

namespace xpTURN.Klotho.ECS.FSM
{
    /// <summary>
    /// Default single-axis HFSM component. Embeds <see cref="HFSMState"/> as its <b>first field</b>; the
    /// generated codec delegates to HFSMState's. Layout / serialized bytes / hash / <c>SizeOf</c> are
    /// byte-identical to the pre-extraction flat struct (same fields, same order, State at offset 0), so the
    /// extraction is transparent to existing snapshots, replays, and the component-id 200 plane.
    ///
    /// Additional HFSM axes are declared as further <c>[KlothoComponent] : IComponent, IHFSMHost</c> structs
    /// that likewise embed <see cref="HFSMState"/> as their first field (see HFSMManager generic overloads).
    /// </summary>
    [KlothoComponent(200)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct HFSMComponent : IComponent, IHFSMHost
    {
        public HFSMState State;   // first field (offset 0) — required by HFSMManager's Unsafe.As reinterpret

        // Back-compat constant re-export: callers / HFSMBuilder reference HFSMComponent.MaxDepth.
        public const int MaxDepth = HFSMState.MaxDepth;
        public const int MaxPendingEvents = HFSMState.MaxPendingEvents;
    }
}
