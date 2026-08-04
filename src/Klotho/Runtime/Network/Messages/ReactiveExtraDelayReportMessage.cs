using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Client→server/host report of the client's effective extra InputDelay (baseline + reactive, clamped).
    /// Sent only when the client's RecommendedExtraDelay changes and
    /// MIN_REPORT_INTERVAL_MS has elapsed — reactive escalate/decay is rare, so this is a low-frequency
    /// message. The server/host folds the reported value into its authoritative baseline
    /// (baseline = max(rttBased, reportedEffective)) so a client's locally-observed reactive correction
    /// (PastTick / rollback-burst) migrates to server authority. Delivered ReliableOrdered.
    ///
    /// <para>SD: client→server. P2P: guest→host (star topology, peerId 0). The host itself is excluded
    /// from reactive (IsHost) so it never sends this.</para>
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.ReactiveExtraDelayReport)]
    public partial class ReactiveExtraDelayReportMessage : NetworkMessageBase
    {
        // Effective extra InputDelay (RecommendedExtraDelay = clamped baseline + reactive). Used by the
        // server/host absorption: baseline = max(rttBased, EffectiveExtraDelay). Single field — the
        // client emits its effective value on OnExtraDelayChanged; the reactive component alone is not
        // wire-needed (server derives it as effective − lastPushedBaseline if required for diagnostics).
        [KlothoOrder] public int EffectiveExtraDelay;
    }
}
