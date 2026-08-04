using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    [KlothoSerializable(MessageTypeId = NetworkMessageType.PlayerJoin)]
    public partial class PlayerJoinMessage : NetworkMessageBase
    {
        // Upper bound on the size of an unauthenticated peer's first message (join / spectator /
        // reconnect request). Enforced before Deserialize so a crafted oversized payload cannot
        // force per-field string allocation or pre-validation Ticket retention (pre-auth memory
        // amplification). Generous headroom for a lobby-issued Ticket (base64url signed token).
        public const int MaxPreAuthMessageBytes = 4096;

        [KlothoOrder] public string DeviceId;
        // Declared after DeviceId so existing wire offsets stay stable ([KlothoOrder] uses declaration
        // order as wire order). Unset values serialize as "".
        [KlothoOrder] public string Ticket;             // Lobby-issued credential (opaque, base64url) presented to the host/server for validation.
        [KlothoOrder] public string ClaimedDisplayName;  // Client-claimed display name (no-lobby nickname, unverified).
    }
}
