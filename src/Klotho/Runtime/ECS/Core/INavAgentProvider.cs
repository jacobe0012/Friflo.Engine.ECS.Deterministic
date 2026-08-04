namespace xpTURN.Klotho.ECS
{
    /// <summary>
    /// Optional callback-side surface — exposes the simulation's NavAgent snapshot provider to
    /// editor diagnostics. Distinct from INavAgentSnapshotProvider itself: the actual provider
    /// often lives inside a system; this interface lets the callbacks object hand it out without
    /// exposing the concrete system type.
    /// </summary>
    public interface INavAgentProvider
    {
        INavAgentSnapshotProvider NavAgentSnapshotProvider { get; }
    }
}
