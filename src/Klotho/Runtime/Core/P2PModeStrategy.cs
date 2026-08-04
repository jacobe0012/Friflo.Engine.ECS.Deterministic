using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    public sealed class P2PModeStrategy : IKlothoModeStrategy
    {
        public static readonly P2PModeStrategy Instance = new P2PModeStrategy();
        private P2PModeStrategy() { }

        public NetworkMode Mode => NetworkMode.P2P;
        public bool SupportsLocalHost => true;
        public bool SupportsLocalGuest => true;
        // P2P host death ends the session — only guests are reconnect-eligible.
        public bool IsReconnectEligible(bool isLocalHost) => !isLocalHost;
        public KlothoRole ResolveRole(bool isHostPreference)
            => isHostPreference ? KlothoRole.P2PHost : KlothoRole.P2PGuest;
        public NetworkMessageBase BuildPreJoinHandshake(int roomId) => null;
        public int NormalizeRoomId(int requestedRoomId) => -1;
    }
}
