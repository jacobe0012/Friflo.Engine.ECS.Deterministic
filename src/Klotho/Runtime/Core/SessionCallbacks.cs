using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Pair of callback sources returned by <see cref="KlothoFlowSetup.CallbacksFactory"/>.
    /// Built atomically after sim/session config are known so the game can size its callback
    /// objects against authoritative values. Authoritative semantics differ per entry point:
    /// host/replay = decided up-front; Normal guest = seed (reseeded from GameStartMessage);
    /// LateJoin / Reconnect / Spectator = server-authoritative from AcceptMessage.
    /// </summary>
    public readonly struct SessionCallbacks
    {
        public readonly ISimulationCallbacks Simulation;
        public readonly IViewCallbacks View;

        public SessionCallbacks(ISimulationCallbacks simulation, IViewCallbacks view)
        {
            Simulation = simulation;
            View = view;
        }
    }
}
