using System;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Serialization
{
    /// <summary>
    /// Marks an Entity / Command / NetworkMessage type for serializer / factory code generation.
    /// The positional <paramref name="typeId"/> populates EntityFactory.TYPE_ID / CommandFactory.TYPE_ID
    /// depending on the base class. For NetworkMessage types, additionally set <see cref="MessageTypeId"/>
    /// via named-arg — the generator emits the dispatch override and registration.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public class KlothoSerializableAttribute : Attribute
    {
        /// <summary>
        /// Wire-format type discriminator. Maps to <c>EntityFactory.TYPE_ID</c> /
        /// <c>CommandFactory.TYPE_ID</c> depending on base class. Independent of
        /// <see cref="xpTURN.Klotho.ECS.KlothoComponentAttribute.ComponentTypeId"/> and
        /// <see cref="xpTURN.Klotho.ECS.KlothoDataAssetAttribute"/> id planes.
        /// </summary>
        public new int TypeId { get; }

        /// <summary>
        /// Network message type discriminator. Accepts named enum members
        /// (e.g. <c>NetworkMessageType.Command</c>) or user-defined values
        /// past <c>NetworkMessageType.UserDefined_Start</c> via explicit cast
        /// (e.g. <c>(NetworkMessageType)201</c>). The generator emits an
        /// override for either form.
        /// </summary>
        public NetworkMessageType MessageTypeId { get; set; }

        public KlothoSerializableAttribute(int typeId = -1) => TypeId = typeId;
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public class KlothoOrderAttribute : Attribute
    {
        public int Order { get; }
        public KlothoOrderAttribute(int order = -1) => Order = order;
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public class KlothoIgnoreAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public class KlothoHashIgnoreAttribute : Attribute { }
}
