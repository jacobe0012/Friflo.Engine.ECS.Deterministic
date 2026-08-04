using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Confirmed roster-transition kind carried by <see cref="PlayerStateNotificationMessage"/>.
    /// Presumed-drop guesses are NOT propagated — only host-confirmed transitions.
    /// </summary>
    public enum PlayerStateChange : byte
    {
        Disconnected = 0,
        Reconnected = 1,
        Left = 2,
    }

    /// <summary>
    /// Broadcast from host (P2P) to existing guests when a player's connection state is
    /// confirmed-changed (disconnect / reconnect / leave). Recipients apply it to the engine
    /// roster sets (_disconnectedPlayerIds / _activePlayerIds) so a departed peer is excluded
    /// from the timing-advantage vote, and to the _players surface so PlayerCount / connection
    /// state / events stay consistent across all peers. In the star topology a guest gets no
    /// transport event for another guest's drop, so this is its only disconnect signal.
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.PlayerStateNotification)]
    public partial class PlayerStateNotificationMessage : NetworkMessageBase
    {
        [KlothoOrder] public int PlayerId;
        [KlothoOrder] public byte State; // PlayerStateChange
    }
}
