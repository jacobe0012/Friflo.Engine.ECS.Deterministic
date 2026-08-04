using System;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Authority-side (P2P host / SD server) validator of the lobby-issued identity credential
    /// carried in the join handshake (<see cref="Messages.PlayerJoinMessage"/>.Ticket). The core only
    /// invokes this hook before reserving a player slot; the actual verification (Ed25519 signature /
    /// lobby redeem) lives in the game integration layer — Klotho ships no validator, since
    /// authentication is intentionally outside the engine. When no lobby is used, this validator is
    /// left unset and the hook is skipped, so behaviour is unchanged. Mirrors the client-side
    /// <see cref="IPlayerIdentityProvider"/>.
    /// </summary>
    public interface IPlayerIdentityValidator
    {
        /// <summary>
        /// Begins validation of a join request. Invoked on the network loop thread, before slot
        /// reservation. Returns a handle the core polls each tick.
        /// <para>
        /// A synchronous validator (P2P offline signature check) returns an already-complete handle
        /// (<see cref="IIdentityValidation.IsComplete"/> == true). An asynchronous validator (SD lobby
        /// redeem) returns an incomplete handle and flips it complete later from its own thread.
        /// </para>
        /// <para>
        /// Asynchronous implementations MUST copy <paramref name="request"/> by value if they capture it
        /// for background use — it is passed by <c>in</c> (a stack reference) and must not be retained.
        /// Its string fields are immutable, so a copied request is safe to read from a background thread.
        /// </para>
        /// </summary>
        IIdentityValidation BeginValidate(in IdentityValidationRequest request);
    }

    /// <summary>
    /// A pending or completed validation handle. The core polls <see cref="IsComplete"/> each loop tick
    /// and reads <see cref="Outcome"/> once complete. Implemented by the game.
    /// <para>Threading contract (the game must honor):</para>
    /// <list type="bullet">
    /// <item>Write <see cref="Outcome"/> fully, THEN set <see cref="IsComplete"/> with a release/volatile
    /// write. The core acquire-reads <see cref="IsComplete"/> before reading <see cref="Outcome"/>. The
    /// core reader may be a different ThreadPool worker each tick, but volatile happens-before is not
    /// thread-pair specific, so this is safe.</item>
    /// <item><see cref="Outcome"/> is immutable once <see cref="IsComplete"/> is true. This includes
    /// <see cref="IdentityValidationOutcome.Entitlement"/>: the producer MUST NOT mutate or reuse that
    /// byte[] after publishing (publish-once) — unlike the string fields, a byte[] is mutable, so this is
    /// a caller contract, not a language guarantee.</item>
    /// <item><see cref="IDisposable.Dispose"/>: the core calls it when a pending peer disconnects or the
    /// validation times out, signalling the validator to cancel an in-flight redeem before it consumes
    /// the lobby nonce. Dispose must be idempotent, safe to call after completion (cancel becomes a
    /// no-op), and safe to race with the in-flight redeem thread.</item>
    /// </list>
    /// </summary>
    public interface IIdentityValidation : IDisposable
    {
        /// <summary>True once validation has finished. A synchronous (P2P) implementation is always true.</summary>
        bool IsComplete { get; }

        /// <summary>The result. Only valid (and immutable) once <see cref="IsComplete"/> is true.</summary>
        IdentityValidationOutcome Outcome { get; }
    }

    /// <summary>
    /// Context passed to <see cref="IPlayerIdentityValidator.BeginValidate"/>. Opaque ticket plus the
    /// session/peer context a validator needs to bind the ticket to this session and verify it.
    /// </summary>
    public readonly struct IdentityValidationRequest
    {
        /// <summary>Lobby-issued ticket (e.g. base64url-encoded signed payload). Empty when none provided.</summary>
        public readonly string Ticket;
        /// <summary>No-lobby claimed display name. The authority ignores it when a validator is present,
        /// so a verified ticket's name always wins.</summary>
        public readonly string ClaimedDisplayName;
        /// <summary>Session identifier the validator uses to bind a ticket to this session. Per-room unique on SD.</summary>
        public readonly long SessionMagic;
        /// <summary>Connection-level peer id (operational; not a stable account).</summary>
        public readonly int PeerId;
        /// <summary>Reconnect device fingerprint (client-provided). Not an account identifier.</summary>
        public readonly string DeviceId;
        /// <summary>True when this join arrives after the game has started (late join).</summary>
        public readonly bool IsLateJoin;
        /// <summary>True when the authority is validating its own ticket (the P2P host validating itself
        /// as it adds itself to the roster).</summary>
        public readonly bool IsHostSelf;
        /// <summary>The room the peer was routed to (SD multi-room). The validator carries this to the lobby
        /// redeem so the lobby can cross-check it against the ticket's bound (server, room). -1 when not
        /// room-scoped (P2P / an SD path that never calls SetRoomId) — the validator ignores a -1.</summary>
        public readonly int RoomId;

        public IdentityValidationRequest(
            string ticket,
            string claimedDisplayName,
            long sessionMagic,
            int peerId,
            string deviceId,
            bool isLateJoin,
            bool isHostSelf,
            int roomId)
        {
            Ticket = ticket ?? string.Empty;
            ClaimedDisplayName = claimedDisplayName ?? string.Empty;
            SessionMagic = sessionMagic;
            PeerId = peerId;
            DeviceId = deviceId ?? string.Empty;
            IsLateJoin = isLateJoin;
            IsHostSelf = isHostSelf;
            RoomId = roomId;
        }
    }

    /// <summary>
    /// Validation result. Accepted carries the authoritative account/displayName; rejected carries a
    /// wire reason code (6~11, see <see cref="JoinFailReason"/> / disconnect payload).
    /// </summary>
    public readonly struct IdentityValidationOutcome
    {
        public readonly bool Accepted;
        /// <summary>Authoritative account id on accept (may be empty).</summary>
        public readonly string Account;
        /// <summary>Authoritative display name on accept (may be empty → core fabricates a fallback).</summary>
        public readonly string DisplayName;
        /// <summary>Disconnect-payload reason code on reject (6~11). Ignored when <see cref="Accepted"/>.</summary>
        public readonly byte RejectWireCode;
        /// <summary>
        /// Opaque, authoritative entitlement bytes carried alongside the account on accept (null when none /
        /// no entitlement path). The core treats this as an opaque blob — it never parses it; the game layer
        /// reads it (e.g. the player-config entitlement guard, tick-0 seed). Like the string fields it is
        /// immutable once published via <see cref="IIdentityValidation.IsComplete"/>: a producer MUST NOT
        /// mutate or reuse the array after handing it to <see cref="Accept(string,string,byte[])"/>
        /// (publish-once — unlike strings, a byte[] is mutable, so this is the caller's contract to honor).
        /// </summary>
        public readonly byte[] Entitlement;

        private IdentityValidationOutcome(bool accepted, string account, string displayName, byte rejectWireCode, byte[] entitlement)
        {
            Accepted = accepted;
            Account = account ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            RejectWireCode = rejectWireCode;
            Entitlement = entitlement;
        }

        /// <summary>Accept with the authoritative account/displayName (either may be empty). No entitlement.</summary>
        public static IdentityValidationOutcome Accept(string account, string displayName)
            => new IdentityValidationOutcome(true, account, displayName, 0, null);

        /// <summary>
        /// Accept carrying an opaque authoritative entitlement blob (publish-once — see <see cref="Entitlement"/>).
        /// A null entitlement is equivalent to the no-entitlement overload.
        /// </summary>
        public static IdentityValidationOutcome Accept(string account, string displayName, byte[] entitlement)
            => new IdentityValidationOutcome(true, account, displayName, 0, entitlement);

        /// <summary>Reject with a disconnect-payload wire reason code (6~11).</summary>
        public static IdentityValidationOutcome Reject(byte rejectWireCode)
            => new IdentityValidationOutcome(false, string.Empty, string.Empty, rejectWireCode, null);
    }
}
