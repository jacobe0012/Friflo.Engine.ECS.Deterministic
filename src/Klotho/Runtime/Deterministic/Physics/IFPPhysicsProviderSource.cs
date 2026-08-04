namespace xpTURN.Klotho.Deterministic.Physics
{
    /// <summary>
    /// Optional callback-side surface — exposes the simulation's physics world provider to editor
    /// diagnostics (e.g. FPPhysicsWorldVisualizer). Returning null is valid — diagnostics skip wiring.
    /// </summary>
    public interface IFPPhysicsProviderSource
    {
        IFPPhysicsWorldProvider PhysicsProvider { get; }
    }
}
