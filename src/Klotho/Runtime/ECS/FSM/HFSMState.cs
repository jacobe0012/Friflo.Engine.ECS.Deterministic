using System.Runtime.InteropServices;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.ECS.FSM
{
    /// <summary>
    /// Per-axis HFSM runtime state (active-state stack, pending events, elapsed ticks), extracted from
    /// <see cref="HFSMComponent"/> so multiple HFSM axes can coexist on one entity as distinct component
    /// types — each embedding this as its <b>first field (offset 0)</b>. <see cref="IHFSMHost"/>-constrained
    /// generic <c>HFSMManager</c> entry points reinterpret the host component's first field as this via
    /// <c>Unsafe.As</c>.
    ///
    /// <c>[KlothoSerializableStruct]</c> generates its inline codec; the host component's generated codec
    /// delegates Serialize/Deserialize/GetSerializedSize/GetHash to it. Layout/serialization/hash are
    /// byte-identical to the pre-extraction flat <see cref="HFSMComponent"/> (same fields, same order).
    /// </summary>
    [KlothoSerializableStruct]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public unsafe partial struct HFSMState
    {
        public const int MaxDepth = 8;
        public const int MaxPendingEvents = 4;

        public int RootId;

        public fixed int ActiveStateIds[MaxDepth];
        public int ActiveDepth;

        public fixed int PendingEventIds[MaxPendingEvents];
        public int PendingEventCount;

        public int StateElapsedTicks;
    }

    /// <summary>
    /// Marker for a component that hosts an <see cref="HFSMState"/> as its <b>first field (offset 0)</b>.
    /// Has no members — it only constrains/documents the generic <c>HFSMManager&lt;TComp&gt;</c> entry points,
    /// which reinterpret the component as <c>HFSMState</c> via <c>Unsafe.As</c>. The first-field invariant is
    /// a convention (Sequential layout puts the first field at offset 0); not enforceable by the marker alone.
    /// </summary>
    public interface IHFSMHost { }
}
