using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Broadcast from host (P2P) or server (SD) to existing peers when a new player completes
    /// late-join handshake. Recipients add the player to their _players list so PlayerCount /
    /// Players / OnPlayerJoined surfaces stay consistent across all peers.
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.LateJoinNotification)]
    public partial class LateJoinNotificationMessage : NetworkMessageBase
    {
        [KlothoOrder] public int PlayerId;
        [KlothoOrder] public int JoinTick;
        // Authoritative identity, declared after the prior fields so wire offsets stay stable. Unset = "".
        [KlothoOrder] public string Account;
        [KlothoOrder] public string DisplayName;
        // The late-joining player's ORIGINAL lobby-signed ticket, so existing peers re-verify
        // it independently. Trailing — "" when the propagation gate is off.
        [KlothoOrder] public string OriginalTicket;

        // SD: the late-joiner's server-verified entitlement bytes, so connected SD clients seed the
        // same deterministic join-time state when the join command executes (P2P peers derive this
        // from OriginalTicket instead — unused/empty on that path). Declared LAST so wire offsets
        // stay stable. Empty round-trips as null ("no entitlement" decode).
        [KlothoOrder] public byte[] Entitlement;
    }
}
