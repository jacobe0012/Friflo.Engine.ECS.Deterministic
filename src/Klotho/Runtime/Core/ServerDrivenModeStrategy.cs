using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    public sealed class ServerDrivenModeStrategy : IKlothoModeStrategy
    {
        public static readonly ServerDrivenModeStrategy Instance = new ServerDrivenModeStrategy();
        private ServerDrivenModeStrategy() { }

        public NetworkMode Mode => NetworkMode.ServerDriven;
        // SD client always joins the configured server — no local host affordance.
        public bool SupportsLocalHost => false;
        public bool SupportsLocalGuest => true;
        // SD client is always reconnect-eligible (server outlives the client).
        public bool IsReconnectEligible(bool isLocalHost) => true;
        // SD has no local host — the preference is ignored.
        public KlothoRole ResolveRole(bool isHostPreference) => KlothoRole.SdClient;
        public NetworkMessageBase BuildPreJoinHandshake(int roomId)
            => new RoomHandshakeMessage { RoomId = roomId };
        public int NormalizeRoomId(int requestedRoomId) => requestedRoomId;
    }
}
