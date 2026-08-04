using xpTURN.Klotho.Deterministic.Navigation;

namespace xpTURN.Klotho.ECS
{
    /// <summary>
    /// Optional callback-side surface. Implement on ISimulationCallbacks to expose the simulation's
    /// navigation mesh + query to editor diagnostics. Returning null is valid — diagnostics skip registration.
    /// </summary>
    public interface INavMeshProvider
    {
        FPNavMesh NavMesh { get; }
        FPNavMeshQuery NavQuery { get; }
    }
}
