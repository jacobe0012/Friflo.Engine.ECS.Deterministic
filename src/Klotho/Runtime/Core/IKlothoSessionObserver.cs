using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Aggregates session-level lifecycle callbacks. Implemented by the game; consumed by KlothoSession
    /// to bulk-subscribe at Create and bulk-unsubscribe at Stop. Replaces the per-event += wiring
    /// previously spread across StartHost / JoinGameAsync / ReconnectAsync / StopGame sites.
    ///
    /// Default (no-op) implementations let games override only the callbacks they care about.
    /// </summary>
    public interface IKlothoSessionObserver
    {
        // ── NetworkService callbacks ──

        /// <summary>A remote player dropped from the session and is awaiting reconnect.</summary>
        void OnPlayerDisconnected(IPlayerInfo player) { }

        /// <summary>A previously-disconnected remote player resumed the session.</summary>
        void OnPlayerReconnected(IPlayerInfo player) { }

        /// <summary>The local client lost transport and started a reconnect attempt.</summary>
        void OnReconnecting() { }

        /// <summary>A local reconnect attempt failed. <paramref name="reason"/> is one of <see cref="ReconnectRejectReason"/> values.</summary>
        void OnReconnectFailed(ReconnectRejectReason reason) { }

        /// <summary>A local reconnect attempt succeeded; transport is restored.</summary>
        void OnReconnected() { }

        // ── Engine callbacks ──

        /// <summary>Late-join or reconnect catchup finished; the local simulation chain is in sync.</summary>
        void OnCatchupComplete() { }

        /// <summary>A full-state resync completed at <paramref name="tick"/>; verified state replaced prediction.</summary>
        void OnResyncCompleted(int tick) { }

        /// <summary>The match transitioned into the Running state. Fires once per match start.</summary>
        void OnGameStart() { }

        /// <summary>The match terminated abnormally (chain stall, state divergence, reconnect failure, etc.).</summary>
        void OnMatchAborted(AbortReason reason) { }

        /// <summary>The match ended normally at <paramref name="tick"/>; <paramref name="endEvt"/> carries the winner and end reason.</summary>
        void OnMatchEnded(int tick, IMatchEndEvent endEvt) { }

        /// <summary>A corrective reset rolled the simulation back to a known-good state; the match continues.</summary>
        void OnMatchReset(ResetReason reason) { }

        // ── Session state callbacks ──
        // Contract: these run inside the Engine / NetworkService event dispatch — do not throw
        // (an exception propagates into the dispatch).

        /// <summary>KlothoState transition (Idle / Running / ...). Forwards the Engine state transition.</summary>
        void OnStateChanged(KlothoState state) { }

        /// <summary>SessionPhase transition. Forwards the NetworkService phase transition.</summary>
        void OnPhaseChanged(SessionPhase phase) { }

        /// <summary>Player count changed. Forwards the NetworkService / SpectatorService transition.</summary>
        void OnPlayerCountChanged(int count) { }

        /// <summary>All-players-ready toggled. Forwards the NetworkService transition.</summary>
        void OnAllPlayersReadyChanged(bool ready) { }

        // ── Session lifecycle ──

        /// <summary>
        /// A session was created via the given entry kind (Host / Guest / Replay / Spectator).
        /// Fired once per session, after <see cref="KlothoSession"/> is fully constructed — the single
        /// role-bearing session-created surface. Distinct from <c>KlothoSession.OnSessionCreated</c>
        /// (static, editor diagnostics).
        /// </summary>
        void OnSessionCreated(KlothoSession session, SessionEntryKind kind) { }

        /// <summary>
        /// Fired inside <c>KlothoSession.Stop()</c> BEFORE Engine.Stop() — Engine / Simulation are still alive.
        /// Game-side cleanup that needs a live engine (view teardown, EVU cleanup) runs here. Pairs with
        /// <see cref="OnSessionStopped"/>, which fires after framework cleanup for terminal teardown
        /// (session reference null-out, UI reset).
        /// </summary>
        void OnSessionStopping() { }

        /// <summary>
        /// Fired exactly once at the end of <c>KlothoSession.Stop()</c> after framework cleanup
        /// (Engine.Stop / NetworkService.LeaveRoom / observer unsubscribe / replay save). This is the
        /// single terminal-teardown callback: game-side teardown (session reference null-out, UI reset)
        /// runs here. Main-transport disconnect is not game-side — the driver owns it (process-exit).
        ///
        /// Single-path model: <c>Stop()</c> is idempotent via its <c>_stopped</c> guard, so it fires
        /// this callback once regardless of who triggered the stop — game-initiated (the driver's
        /// DetachAndStop) or framework-internal (auto-shutdown grace, spectator-drop). The handler does
        /// not need to drive the driver or guard re-entry; the framework owns idempotency. (Invariant:
        /// <c>session.IsStopped == true</c> at call time.)
        /// </summary>
        void OnSessionStopped() { }

        /// <summary>
        /// The main transport dropped while no session is attached and no connect is in flight
        /// (a genuine idle disconnect). Session-independent — raised by the driver, not inside Stop().
        /// Games typically reset to their initial UI here. <paramref name="reason"/> carries the
        /// categorized disconnect reason.
        /// </summary>
        void OnIdleDisconnected(DisconnectReason reason) { }
    }
}
