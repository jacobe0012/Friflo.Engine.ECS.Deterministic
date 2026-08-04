namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Guest-side, signature-only re-verification of a peer's original lobby-issued ticket propagated by
    /// the P2P host. Distinct from <see cref="IPlayerIdentityValidator"/>:
    /// the validator is the authority-side join-time gate (host validates a joining peer, SD server
    /// redeems); this hook lets EACH guest independently re-verify the propagated original ticket so the
    /// host is no longer the single trusted verifier (full zero-trust).
    /// <para>
    /// An optional companion interface, NOT a method on <see cref="IPlayerIdentityValidator"/>: an SD
    /// validator never re-verifies (the server is trusted), so it simply does not implement this — the
    /// core detects support via <c>validator is IPropagatedTicketVerifier</c> and skips re-verification
    /// when absent. A P2P validator (e.g. the Ed25519 reference) implements both.
    /// </para>
    /// <para>
    /// Signature-only: verify ONLY the Ed25519 signature (and an account over-bound guard); do NOT
    /// re-check expiry / nonce / sessionId. This is a correctness requirement, not an optimisation — the
    /// join-time validator already consumed the nonce and bound the session, and a propagated ticket is
    /// received more than once (roster snapshot + later notifications + reconnect re-send). Re-running the
    /// full join-time evaluation would false-reject a still-valid roster member on the second receipt
    /// (nonce already spent), on a long match (expiry passed), or when the guest does not hold the
    /// session id. The signature stays valid forever, so signature-only is the only consistent re-check.
    /// </para>
    /// </summary>
    public interface IPropagatedTicketVerifier
    {
        /// <summary>
        /// Re-verifies a propagated original ticket, signature-only. Synchronous and fully local
        /// (no I/O) — unlike <see cref="IPlayerIdentityValidator.BeginValidate"/> there is no async
        /// handle / poll / Dispose ceremony.
        /// <para>
        /// Returns an <see cref="IdentityValidationOutcome"/> reusing the existing type: on a valid
        /// signature it is <see cref="IdentityValidationOutcome.Accepted"/> with the ticket's own
        /// Account / DisplayName (the value the guest adopts, ignoring the host-relayed roster value)
        /// and any opaque entitlement bytes. <see cref="IdentityValidationOutcome.RejectWireCode"/>
        /// is MEANINGLESS on this path (a join-time disconnect code) — the caller consumes only
        /// <see cref="IdentityValidationOutcome.Accepted"/> + the authoritative fields; a failed
        /// re-verification is routed as a match-integrity failure, not a peer disconnect.
        /// </para>
        /// </summary>
        /// <param name="ticket">The propagated original lobby ticket (opaque, e.g. base64url signed token).</param>
        IdentityValidationOutcome ReverifyPropagatedTicket(string ticket);
    }
}
