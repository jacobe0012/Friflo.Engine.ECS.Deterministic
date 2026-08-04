using System;
using System.Collections.Generic;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.Network
{
    public enum SpectatorState
    {
        Idle,
        Connecting,
        Synchronizing,
        Watching,
        Disconnected
    }

    /// <summary>
    /// Mutable spectator-side player record. Implements <see cref="IPlayerInfo"/> so the spectator
    /// roster unifies with the player-session surface (<see cref="ILockstepSession.Players"/>) — the
    /// sample/UI reads a single mode-agnostic list. Class (reference) so identity/ready can be updated
    /// in place from incremental notifications.
    /// </summary>
    /// <remarks>
    /// <see cref="Ping"/> is always 0 and <see cref="ConnectionState"/> is seeded from the roster but not
    /// live-updated (no spectator handler for PlayerStateNotificationMessage) — spectators use this surface
    /// for name/ready display.
    /// </remarks>
    public class SpectatorPlayerInfo : IPlayerInfo
    {
        public int PlayerId { get; set; }
        public string DisplayName { get; set; } = "";
        public string Account { get; set; } = "";
        public bool IsReady { get; set; }
        public int Ping { get; set; }
        public PlayerConnectionState ConnectionState { get; set; }
    }

    public class SpectatorStartInfo
    {
        public int RandomSeed;
        public int TickInterval;
        public int PlayerCount;
        public List<int> PlayerIds;
        public List<SpectatorPlayerInfo> Players;
    }

    public interface ISpectatorService
    {
        SpectatorState State { get; }

        void Initialize(INetworkTransport transport, ICommandFactory commandFactory, IKlothoEngine engine, IKLogger logger);

        void Connect(string hostAddress, int port, int roomId = -1);

        void Disconnect();

        void Update();

        int DelayFrames { get; }

        int LatestReceivedTick { get; }

        /// <summary>
        /// Live player count. Seeded from <see cref="SpectatorAcceptMessage.Roster"/> on bootstrap,
        /// then mutated by lobby join/leave notifications (pre-game) and <c>LateJoinNotificationMessage</c>
        /// arrivals (mid-match).
        /// </summary>
        int PlayerCount { get; }

        /// <summary>
        /// Live player roster (identity included). Seeded from <see cref="SpectatorAcceptMessage.Roster"/> on
        /// bootstrap, then mutated by lobby join/leave/ready notifications. Use <see cref="OnRosterChanged"/>
        /// to refresh; <see cref="OnPlayerCountChanged"/> still fires on count changes for back-compat.
        /// Unified as <see cref="IPlayerInfo"/> so callers share one surface with the player session.
        /// </summary>
        IReadOnlyList<IPlayerInfo> Players { get; }

        event Action<SpectatorStartInfo> OnSpectatorStarted;

        event Action<int, ICommand> OnConfirmedInputReceived;

        event Action<string> OnSpectatorStopped;

        event Action<int, byte[], long, FullStateKind> OnFullStateReceived;

        event Action<ISessionConfig> OnSessionConfigReceived;

        /// <summary>
        /// Fired when <see cref="PlayerCount"/> changes. Argument is the new count.
        /// </summary>
        event Action<int> OnPlayerCountChanged;

        /// <summary>
        /// Fired when the roster content changes (membership, name/account, or ready state).
        /// Subscribers re-read <see cref="Players"/>.
        /// </summary>
        event Action OnRosterChanged;
    }
}
