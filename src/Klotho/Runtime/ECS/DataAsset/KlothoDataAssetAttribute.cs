using System;

namespace xpTURN.Klotho.ECS
{
    /// <summary>
    /// DataAsset wire-format type discriminator. Independent of
    /// <see cref="KlothoComponentAttribute.ComponentTypeId"/> and
    /// <see cref="xpTURN.Klotho.Serialization.KlothoSerializableAttribute.TypeId"/> id planes.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    [Preserve]
    public class KlothoDataAssetAttribute : Attribute
    {
        /// <summary>
        /// Wire-format type discriminator. Used by DataAssetTypeRegistry to dispatch Deserialize.
        /// Must remain stable across binary-compatible builds.
        /// </summary>
        public new int TypeId { get; }

        /// <summary>
        /// Runtime instance identifier for single-instance assets. When set, the generator emits
        /// a parameterless ctor that assigns AssetId from this value, and
        /// IDataAssetRegistry.Get&lt;T&gt;() resolves the instance by this id.
        /// Leave unset for multi-instance assets (use Get&lt;T&gt;(int id) instead).
        /// </summary>
        public int AssetId { get; set; }

        /// <summary>
        /// Optional human-readable key for IDataAssetRegistry.GetByKey&lt;T&gt;(string).
        /// Independent of AssetId. Case-sensitive; cross-type uniqueness not required
        /// (lookup key is (Type, string)).
        /// </summary>
        public string Key { get; set; }

        public KlothoDataAssetAttribute(int typeId) => TypeId = typeId;
    }
}
