using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Broadcast from host (P2P) / server (SD) to remaining peers when a player leaves during the
    /// pre-game lobby. Recipients remove the player from their _players list so PlayerCount / Players /
    /// OnPlayerLeft surfaces stay consistent before StartGame. In-game leave continues to use
    /// PlayerStateNotificationMessage (PlayerStateChange.Left); this is lobby-only.
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.PlayerLeaveNotification)]
    public partial class PlayerLeaveNotificationMessage : NetworkMessageBase
    {
        [KlothoOrder] public int PlayerId;
    }
}
