using System.Collections.Generic;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    [KlothoSerializable(MessageTypeId = NetworkMessageType.ReconnectAccept)]
    public partial class ReconnectAcceptMessage : NetworkMessageBase
    {
        [KlothoOrder] public int PlayerId;
        [KlothoOrder] public int CurrentTick;
        [KlothoOrder] public long SharedEpoch;
        [KlothoOrder] public long ClockOffset;
        [KlothoOrder] public int PlayerCount;
        [KlothoOrder] public List<RosterEntry> Roster = new List<RosterEntry>();

        // --- SessionConfig fields (canonical order matches ISessionConfig / GameStartMessage) ---

        [KlothoOrder] public int RandomSeed;
        [KlothoOrder] public int MaxPlayers;
        [KlothoOrder] public int MinPlayers;
        [KlothoOrder] public int MaxSpectators;
        [KlothoOrder] public bool AllowLateJoin;
        [KlothoOrder] public int LateJoinDelayTicks;
        [KlothoOrder] public int ReconnectTimeoutMs;
        [KlothoOrder] public int ReconnectMaxRetries;
        [KlothoOrder] public int LateJoinDelaySafety;
        [KlothoOrder] public int RttSanityMaxMs;
        [KlothoOrder] public int MinStallAbortTicks;
        [KlothoOrder] public int CountdownDurationMs;
        [KlothoOrder] public int AbortGraceMs;
        [KlothoOrder] public int EndGracePolicy; // EndGracePolicy enum as int
        [KlothoOrder] public int EndGraceMs;
        [KlothoOrder] public int ClientShutdownGraceMs;

        // Server-recommended extra InputDelay ticks for catchup-gap compensation.
        [KlothoOrder] public int RecommendedExtraDelay;

        // Per-player ORIGINAL lobby-signed tickets, index-parallel to Roster, so the
        // reconnector re-verifies every player independently (P2P full-state is host-authored). Declared
        // LAST so wire offsets stay stable. Empty when the propagation gate is off. base64url strings.
        // NOTE: _reconnectAcceptCache is a reused instance — populate must Clear() this before Add.
        [KlothoOrder] public List<string> RosterTickets = new List<string>();

        // SD: per-player server-verified entitlement bytes, index-parallel to Roster (same
        // concat+lengths encoding as LateJoinAcceptMessage) — covers any player whose
        // PlayerJoinCommand is still pending in the reconnector's replay window. Unused (empty) on
        // the P2P path. Declared LAST so wire offsets stay stable.
        // NOTE: reused-cache instance — populate must Clear() the lengths list before Add.
        [KlothoOrder] public byte[] RosterEntitlementData;
        [KlothoOrder] public List<int> RosterEntitlementLengths = new List<int>();
    }
}
