using System.Collections.Generic;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Sync complete message (host → client, handshake complete)
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.SyncComplete)]
    public partial class SyncCompleteMessage : NetworkMessageBase
    {
        [KlothoOrder]
        public long Magic;

        [KlothoOrder]
        public int PlayerId;

        [KlothoOrder]
        public long SharedEpoch;

        [KlothoOrder]
        public long ClockOffset;

        // Server-recommended extra InputDelay ticks for normal-join player seed.
        // Trailing field — backward-compat with older clients via deserialize underrun (server-first deploy assumption).
        [KlothoOrder]
        public int RecommendedExtraDelay;

        // Pre-game roster snapshot so the joining guest builds its full player list immediately.
        // Inline-initialized because the generated deserializer calls .Clear()/.Capacity and the
        // serializer reads .Count, both of which throw on a null list. ReadyState is meaningful here
        // (normal-join path) so per-player Ready flags reach the joining guest.
        [KlothoOrder]
        public List<RosterEntry> Roster = new List<RosterEntry>();

        // Per-player ORIGINAL lobby-signed tickets, index-parallel to Roster, so the joining
        // guest re-verifies every existing player independently. Trailing field; inline-init
        // (generated (de)serializer calls .Clear()/.Count). Empty when the propagation gate is off — the
        // guest then skips re-verification and keeps the host-relayed roster identity (semi-trust).
        // base64url strings (codegen List<string> first-class), NOT the PlayerConfigData
        // byte[]+lengths pattern (that is for already-serialized binary sub-messages).
        [KlothoOrder]
        public List<string> RosterTickets = new List<string>();

        // Per-player server-verified entitlement bytes, index-parallel to Roster (SD only). Trailing;
        // all players' bytes are concatenated into RosterEntitlementData with each player's byte length
        // in RosterEntitlementLengths, so the receiver slices each player's bytes back out. Empty on the
        // P2P host (which propagates entitlement through the RosterTickets re-verification path instead).
        // RosterEntitlementLengths is inline-initialized — the generated (de)serializer calls .Count /
        // .Clear() on it, which throw on a null list.
        [KlothoOrder]
        public byte[] RosterEntitlementData;
        [KlothoOrder]
        public List<int> RosterEntitlementLengths = new List<int>();
    }
}
