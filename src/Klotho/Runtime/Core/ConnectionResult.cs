namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Result of KlothoConnection.Connect() / Reconnect().
    /// Encapsulates the completed state of the handshake + SimulationConfig reception.
    /// Injected into KlothoSessionSetup.Connection and handed off to NetworkService.
    /// </summary>
    public class ConnectionResult
    {
        /// <summary>
        /// The (already-connected) transport used for the handshake.
        /// </summary>
        public Network.INetworkTransport Transport { get; set; }

        /// <summary>
        /// SimulationConfig received from the host.
        /// </summary>
        public ISimulationConfig SimulationConfig { get; set; }

        /// <summary>
        /// Player ID assigned by the host (Normal / LateJoin) or echoed back unchanged (Reconnect).
        /// </summary>
        public int LocalPlayerId { get; set; }

        /// <summary>
        /// Session Magic. The SyncCompleteMessage.Magic / LateJoinAccept.Magic / ReconnectAccept (via creds) value
        /// is forwarded as-is.
        /// </summary>
        public long SessionMagic { get; set; }

        /// <summary>
        /// Clock synchronization — SharedClock epoch.
        /// </summary>
        public long SharedEpoch { get; set; }

        /// <summary>
        /// Clock synchronization — local-host offset.
        /// </summary>
        public long ClockOffset { get; set; }

        /// <summary>
        /// Server-recommended extra input delay forwarded from SyncCompleteMessage.RecommendedExtraDelay.
        /// LateJoin / Reconnect carry the same value inside their AcceptMessage payloads.
        /// </summary>
        public int RecommendedExtraDelay { get; set; }

        /// <summary>
        /// Which guest join path produced this result.
        /// </summary>
        public JoinKind Kind { get; set; } = JoinKind.Normal;

        /// <summary>
        /// Late Join-specific payload. Valid only when Kind == JoinKind.LateJoin.
        /// </summary>
        public LateJoinPayload LateJoinPayload { get; set; }

        /// <summary>
        /// Cold-start Reconnect-specific payload. Valid only when Kind == JoinKind.Reconnect.
        /// </summary>
        public ReconnectPayload ReconnectPayload { get; set; }

        /// <summary>
        /// Pre-game roster snapshot forwarded from SyncCompleteMessage on a normal join. The joining
        /// guest builds its full player list from this so the lobby roster is consistent immediately;
        /// InitializeFromConnection rebuilds from it. Null/empty for LateJoin and Reconnect, which carry
        /// the roster in their own AcceptMessage payloads instead. ReadyState is carried per-entry
        /// (in-memory only; this type is not serialized).
        /// </summary>
        public System.Collections.Generic.List<Network.RosterEntry> Roster { get; set; }

        /// <summary>
        /// Per-player ORIGINAL lobby-signed tickets forwarded from SyncCompleteMessage.RosterTickets,
        /// index-parallel to <see cref="Roster"/>. The joining guest re-verifies each entry independently
        /// in InitializeFromConnection. Null/empty when the P2P original-ticket propagation gate is off
        /// (the guest then keeps host-relayed roster identity, i.e. semi-trust). In-memory only (this type
        /// is not serialized); the wire carrier is SyncCompleteMessage.RosterTickets.
        /// </summary>
        public System.Collections.Generic.List<string> RosterTickets { get; set; }

        /// <summary>
        /// Per-player server-verified entitlement bytes forwarded from SyncCompleteMessage (SD normal join),
        /// index-parallel to <see cref="Roster"/>: all players' bytes concatenated, with each player's byte
        /// length in <see cref="RosterEntitlementLengths"/>. InitializeFromConnection applies these to the
        /// rebuilt player list so GetPlayerEntitlement returns non-null for tick-0 players. Null/empty on P2P
        /// and when no validator ran. In-memory only; the wire carrier is SyncCompleteMessage.RosterEntitlementData.
        /// </summary>
        public byte[] RosterEntitlementData { get; set; }
        public System.Collections.Generic.List<int> RosterEntitlementLengths { get; set; }
    }
}
