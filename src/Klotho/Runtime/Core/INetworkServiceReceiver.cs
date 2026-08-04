using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Opt-in marker interface for <see cref="ISimulationCallbacks"/> implementations that
    /// need the session's <see cref="IKlothoNetworkService"/> handle on host/guest entry.
    ///
    /// When the simulation callbacks instance implements this interface, <see cref="KlothoSessionFlow"/>
    /// invokes <see cref="SetNetworkService"/> exactly once per session, on host/guest entry and
    /// before the <c>IKlothoSessionObserver.OnSessionCreated(session, kind)</c> callback. Replay and
    /// spectator paths skip the call (no live network service).
    ///
    /// Implementations should treat <see cref="SetNetworkService"/> as a one-shot handle assignment;
    /// the framework never calls it twice for the same session.
    /// </summary>
    public interface INetworkServiceReceiver
    {
        void SetNetworkService(IKlothoNetworkService service);
    }
}
