using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Authority-side (SD server) cross-check of a client's <see cref="PlayerConfigBase"/> selection against
    /// the player's authoritative entitlement. Invoked synchronously inside the relay/store path
    /// (<c>HandlePlayerConfigMessage</c>), AFTER join — all inputs are already in local memory (the stored
    /// entitlement and the just-deserialized selection), so unlike <see cref="IPlayerIdentityValidator"/>
    /// this is a single synchronous call with no async handle / poll / Dispose contract. Klotho ships no
    /// guard; the actual ownership rules live in the game integration layer. When unset, the hook is skipped
    /// and the selection passes through unchanged, so behaviour is unchanged for games without entitlements.
    /// <para>
    /// On a dedicated server the decision is server-authoritative, applied once and broadcast, so there is
    /// no per-peer determinism concern. In peer-to-peer the same guard runs on every peer independently —
    /// each clamps the relayed config against its own verified entitlement — so <see cref="Check"/> must be
    /// a deterministic pure function: identical (playerId, entitlement, selection) inputs must yield a
    /// byte-identical verdict on every peer and platform. A non-deterministic clamp (for example a default
    /// chosen from Dictionary/HashSet enumeration order, float math, culture-sensitive string operations, or
    /// a wall clock or RNG) diverges the tick-0 seed and desyncs the match. Because every peer holds the same
    /// lobby-signed entitlement bytes, a deterministic pure function with no ambient non-determinism is
    /// sufficient — no cross-platform bit-reproducible derivation is required.
    /// </para>
    /// <para>
    /// A <see cref="PlayerConfigVerdict.Clamp"/> replaces the selection with a chosen valid one; a
    /// <see cref="PlayerConfigVerdict.Reject"/> carries a disconnect-payload wire code (the core clamps it
    /// into the identity range). The opaque entitlement bytes and the default/clamp selection are the game's
    /// to interpret — the core only applies the returned verdict.
    /// </para>
    /// </summary>
    public interface IPlayerConfigEntitlementGuard
    {
        /// <summary>
        /// Cross-checks <paramref name="selection"/> against <paramref name="entitlement"/> for the
        /// authoritative <paramref name="playerId"/> (resolved by the core from the authenticated peer, not
        /// the wire-claimed id). <paramref name="entitlement"/> may be null/empty (player has no entitlement
        /// even though a guard is set) — the game decides, typically returning <see cref="PlayerConfigVerdict.Pass"/>.
        /// Invoked on the network loop thread; must not block.
        /// </summary>
        PlayerConfigVerdict Check(int playerId, byte[] entitlement, PlayerConfigBase selection);
    }

    /// <summary>Kind of a <see cref="PlayerConfigVerdict"/>.</summary>
    public enum PlayerConfigVerdictKind : byte
    {
        /// <summary>Selection is allowed as-is.</summary>
        Pass = 0,
        /// <summary>Selection is not allowed; replace it with <see cref="PlayerConfigVerdict.Replacement"/> (server-decided default).</summary>
        Clamp = 1,
        /// <summary>Selection is rejected; carry <see cref="PlayerConfigVerdict.RejectWireCode"/> (strict-policy games only).</summary>
        Reject = 2,
    }

    /// <summary>
    /// Result of <see cref="IPlayerConfigEntitlementGuard.Check"/>. Mirrors the
    /// <see cref="IdentityValidationOutcome"/> idiom (readonly struct + static factories; no out-params).
    /// The clamp replacement rides in <see cref="Replacement"/>.
    /// </summary>
    public readonly struct PlayerConfigVerdict
    {
        public readonly PlayerConfigVerdictKind Kind;
        /// <summary>Server-chosen replacement selection on <see cref="PlayerConfigVerdictKind.Clamp"/>; null otherwise.</summary>
        public readonly PlayerConfigBase Replacement;
        /// <summary>Disconnect-payload wire reason code on <see cref="PlayerConfigVerdictKind.Reject"/> (core clamps into the identity range); 0 otherwise.</summary>
        public readonly byte RejectWireCode;

        private PlayerConfigVerdict(PlayerConfigVerdictKind kind, PlayerConfigBase replacement, byte rejectWireCode)
        {
            Kind = kind;
            Replacement = replacement;
            RejectWireCode = rejectWireCode;
        }

        /// <summary>Allow the selection as-is.</summary>
        public static PlayerConfigVerdict Pass()
            => new PlayerConfigVerdict(PlayerConfigVerdictKind.Pass, null, 0);

        /// <summary>Replace the selection with a server-chosen valid one.</summary>
        public static PlayerConfigVerdict Clamp(PlayerConfigBase replacement)
            => new PlayerConfigVerdict(PlayerConfigVerdictKind.Clamp, replacement, 0);

        /// <summary>Reject the selection (strict policy) with a disconnect-payload wire reason code.</summary>
        public static PlayerConfigVerdict Reject(byte rejectWireCode)
            => new PlayerConfigVerdict(PlayerConfigVerdictKind.Reject, null, rejectWireCode);
    }
}
