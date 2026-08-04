using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Broadcast from host (P2P) / server (SD) to existing peers when a new player completes the
    /// normal-join (pre-game / lobby) handshake. Recipients add the player to their _players list so
    /// PlayerCount / Players / OnPlayerJoined surfaces stay consistent across all peers before StartGame.
    /// Distinct from LateJoinNotificationMessage (post-game, IsReady implicitly true) — this carries the
    /// lobby IsReady (default false) and connection state explicitly.
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.PlayerJoinNotification)]
    public partial class PlayerJoinNotificationMessage : NetworkMessageBase
    {
        [KlothoOrder] public int PlayerId;
        [KlothoOrder] public byte ConnectionState;
        [KlothoOrder] public bool IsReady;
        // Authoritative identity, declared after the prior fields so wire offsets stay stable. Unset = "".
        [KlothoOrder] public string Account;
        [KlothoOrder] public string DisplayName;
        // The new player's ORIGINAL lobby-signed ticket, so existing peers re-verify it
        // independently. Trailing — declared last so wire offsets stay stable; "" when the P2P
        // original-ticket propagation gate is off (older readers ignore the absent field).
        [KlothoOrder] public string OriginalTicket;
        // Server-verified entitlement bytes (SD only), so connected SD clients set the joining player's
        // entitlement for GetPlayerEntitlement reads. Trailing; null on the P2P host (guests re-derive
        // their entitlement from OriginalTicket instead).
        [KlothoOrder] public byte[] Entitlement;
    }
}
