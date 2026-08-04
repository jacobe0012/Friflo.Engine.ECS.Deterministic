namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Simulation-side callbacks — common to all peers (server / client / replay).
    /// Only deterministic code is allowed.
    /// </summary>
    public interface ISimulationCallbacks
    {
        /// <summary>
        /// Register simulation systems.
        /// Called immediately after EcsSimulation is created and before Engine.Initialize().
        /// </summary>
        void RegisterSystems(ECS.EcsSimulation simulation);

        /// <summary>
        /// Create world-initialization entities.
        /// Called inside Engine.Start(), before SaveSnapshot(0).
        /// Invoked identically on every peer, so only deterministic code is allowed.
        /// </summary>
        void OnInitializeWorld(IKlothoEngine engine);

        /// <summary>
        /// Input polling immediately before a tick.
        /// The game sends as many commands as desired via sender.
        /// If no command is sent, an EmptyCommand is automatically injected.
        /// </summary>
        void OnPollInput(int playerId, int tick, ICommandSender sender);

        /// <summary>
        /// A player entered the simulated world at its (deterministic) join tick — the late-join analog of
        /// <see cref="OnInitializeWorld"/> for a single joiner. Invoked inside the engine's participant-slot
        /// creation (create-iff-not-exists → fires once per join, rollback-safe) with the live frame, so a game
        /// can seed deterministic per-player world state (e.g. an entitlement-derived loadout) the same way
        /// OnInitializeWorld seeds tick-0 players. The engine is provided for reads (e.g. GetPlayerEntitlement);
        /// note engine.InitFrame is NOT valid here (init-only) — write via the supplied <paramref name="frame"/>.
        /// Only deterministic code is allowed (runs identically on every peer, incl. rollback re-sim).
        /// Games with no per-join world state leave this empty.
        /// </summary>
        void OnPlayerJoinedWorld(IKlothoEngine engine, ECS.Frame frame, int playerId);
    }
}
