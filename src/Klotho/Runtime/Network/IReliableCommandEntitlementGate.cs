using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Authority-side (SD server) cross-check of a client-submitted in-match reliable command
    /// (<see cref="IKlothoEngine.IssueOnce"/> → <c>HandleReliableCommandSubmit</c>) against the player's
    /// authoritative entitlement. Invoked synchronously BEFORE the command is placed on an authoritative
    /// tick — all inputs are already in local memory (the stored entitlement and the just-deserialized
    /// command), so like <see cref="IPlayerConfigEntitlementGuard"/> (and unlike
    /// <see cref="IPlayerIdentityValidator"/>) this is a single synchronous call with no async handle /
    /// poll / Dispose contract. Klotho ships no gate; the ownership rules live in the game integration
    /// layer. When unset, the hook is skipped and every reliable command is accepted, so behaviour is
    /// unchanged for games without entitlements.
    /// <para>
    /// This gates a reliable command that mutates the simulation mid-match. An unowned action is dropped,
    /// not replaced: a denied action simply does not happen, because there is no meaningful "default action"
    /// to substitute (unlike a start-of-match loadout, where an unowned selection can be clamped to an owned
    /// default — that is the job of <see cref="IPlayerConfigEntitlementGuard"/>). On a dedicated server the
    /// decision is server-authoritative — only an accepted command is placed on an authoritative tick and
    /// broadcast, so there is no per-peer determinism concern. (Peer-to-peer reuses this interface but
    /// invokes it at a per-peer command-apply site, where <see cref="Check"/> must be a deterministic pure
    /// function — that path is out of scope here.)
    /// </para>
    /// <para>
    /// Prefer a deterministic ownership check inside the simulation when the owned set is already part of
    /// the replicated simulation state: the simulation can no-op an unowned action on every peer with no
    /// gate. This gate's distinct value is validating against ownership that is NOT in the simulation
    /// state — a large server-only catalog (kept off the wire) or ownership that mutates
    /// server-side mid-match. Do not re-gate seeded ownership here (redundant with the sim check).
    /// </para>
    /// </summary>
    public interface IReliableCommandEntitlementGate
    {
        /// <summary>
        /// Cross-checks <paramref name="command"/> against <paramref name="entitlement"/> for the
        /// authoritative <paramref name="playerId"/> (resolved by the core from the authenticated peer, not
        /// the wire-claimed id). <paramref name="entitlement"/> may be null/empty (player has no entitlement
        /// even though a gate is set) — the game decides, typically returning <see cref="ReliableCommandVerdict.Accept"/>.
        /// The command is already deserialized; the game inspects <see cref="ICommand.CommandTypeId"/> or
        /// downcasts to its concrete type to read the payload. Invoked on the network loop thread; must not block.
        /// </summary>
        ReliableCommandVerdict Check(int playerId, byte[] entitlement, ICommand command);
    }

    /// <summary>Kind of a <see cref="ReliableCommandVerdict"/>.</summary>
    public enum ReliableCommandVerdictKind : byte
    {
        /// <summary>Command is allowed onto the authoritative tick stream.</summary>
        Accept = 0,
        /// <summary>Command is dropped (not placed on a tick); the issuing handle never observes a commit.</summary>
        Drop = 1,
    }

    /// <summary>
    /// Result of <see cref="IReliableCommandEntitlementGate.Check"/>. Mirrors the
    /// <see cref="PlayerConfigVerdict"/> / <see cref="IdentityValidationOutcome"/> idiom (readonly struct +
    /// static factories). Drop-only by design — there is no reject-wire variant: a dropped command is
    /// silently discarded (the reliable handle resolves via the caller's state-driven ack or is cancelled),
    /// which is cheat-safe and requires no new wire reason.
    /// </summary>
    public readonly struct ReliableCommandVerdict
    {
        public readonly ReliableCommandVerdictKind Kind;

        private ReliableCommandVerdict(ReliableCommandVerdictKind kind)
        {
            Kind = kind;
        }

        /// <summary>Allow the command onto the authoritative tick stream.</summary>
        public static ReliableCommandVerdict Accept()
            => new ReliableCommandVerdict(ReliableCommandVerdictKind.Accept);

        /// <summary>Drop the command (unowned action — does not happen).</summary>
        public static ReliableCommandVerdict Drop()
            => new ReliableCommandVerdict(ReliableCommandVerdictKind.Drop);
    }
}
