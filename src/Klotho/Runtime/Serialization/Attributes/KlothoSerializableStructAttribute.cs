using System;

namespace xpTURN.Klotho.Serialization
{
    /// <summary>
    /// Marks an <c>unmanaged partial struct</c> as a reusable, inline-serialized field bundle.
    /// The generator emits <c>Serialize</c>/<c>Deserialize</c>/<c>GetSerializedSize</c>/<c>GetHash</c>
    /// for it (same field codec as <c>[KlothoComponent]</c>, incl. fixed buffers), and a host type
    /// (<c>[KlothoComponent]</c> / <c>[KlothoSerializable]</c> / <c>[KlothoDataAsset]</c>) that has a
    /// field of this struct delegates to those methods.
    ///
    /// Unlike the three host attributes it carries <b>no wire TYPE_ID / factory</b> — it is never
    /// dispatched on its own; the host's codec/hash inlines it. Must be <c>unmanaged</c> and
    /// <c>[StructLayout(LayoutKind.Sequential, Pack = 4)]</c> so it composes into a component's
    /// deterministic memory layout.
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct)]
    public class KlothoSerializableStructAttribute : Attribute { }
}
