using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// The resolved local role. Mode constrains the valid set: P2P offers host/guest,
    /// ServerDriven collapses to client regardless of the host preference.
    /// </summary>
    public enum KlothoRole { P2PHost, P2PGuest, SdClient }

    /// <summary>
    /// Per-mode behavior dispatcher. Game code consults the strategy resolved from
    /// <c>ISimulationConfig.Mode</c> instead of branching on the enum directly.
    /// </summary>
    public interface IKlothoModeStrategy
    {
        NetworkMode Mode { get; }

        /// <summary>True when the local Inspector "Host" affordance is meaningful for this mode.</summary>
        bool SupportsLocalHost { get; }

        /// <summary>True when the local Inspector "Guest" affordance is meaningful for this mode.</summary>
        bool SupportsLocalGuest { get; }

        /// <summary>True when cold-start AutoReconnect should be attempted (P2P host is excluded).</summary>
        bool IsReconnectEligible(bool isLocalHost);

        /// <summary>
        /// Resolves the effective role from the host preference. P2P maps the preference to
        /// host/guest; ServerDriven ignores it and returns client.
        /// </summary>
        KlothoRole ResolveRole(bool isHostPreference);

        /// <summary>Builds the pre-join handshake (returns null for P2P).</summary>
        NetworkMessageBase BuildPreJoinHandshake(int roomId);

        /// <summary>Normalizes the room id (P2P returns -1).</summary>
        int NormalizeRoomId(int requestedRoomId);
    }

    public static class KlothoRoleExtensions
    {
        /// <summary>True only for the P2P host (the local session authority).</summary>
        public static bool IsLocalHost(this KlothoRole role) => role == KlothoRole.P2PHost;

        /// <summary>True when cold-start AutoReconnect applies — every role except the P2P host.</summary>
        public static bool IsReconnectEligible(this KlothoRole role) => role != KlothoRole.P2PHost;
    }
}
