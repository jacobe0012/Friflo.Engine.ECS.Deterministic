using System.Collections.Generic;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Message from the host notifying all clients of game start. Contains the random seed, tick interval, and participant list.
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.GameStart)]
    public partial class GameStartMessage : NetworkMessageBase
    {
        [KlothoOrder] public long StartTime; // Absolute game start time in SharedNow units
        [KlothoOrder] public List<int> PlayerIds = new List<int>();

        // --- SessionConfig (canonical order matches ISessionConfig) ---

        // Determinism
        [KlothoOrder] public int RandomSeed;
        // Membership
        [KlothoOrder] public int MaxPlayers;
        [KlothoOrder] public int MinPlayers;
        [KlothoOrder] public int MaxSpectators;
        // LateJoin / Reconnect Policy
        [KlothoOrder] public bool AllowLateJoin;
        [KlothoOrder] public int LateJoinDelayTicks;
        [KlothoOrder] public int ReconnectTimeoutMs;
        [KlothoOrder] public int ReconnectMaxRetries;
        // LateJoin / Reconnect Tuning
        [KlothoOrder] public int LateJoinDelaySafety;
        [KlothoOrder] public int RttSanityMaxMs;
        // Chain-Stall Watchdog
        [KlothoOrder] public int MinStallAbortTicks;
        // Match Start Countdown
        [KlothoOrder] public int CountdownDurationMs;
        // Match End Grace
        [KlothoOrder] public int AbortGraceMs;
        [KlothoOrder] public int EndGracePolicy; // EndGracePolicy enum as int
        [KlothoOrder] public int EndGraceMs;
        [KlothoOrder] public int ClientShutdownGraceMs;
    }
}
