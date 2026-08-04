using System.Collections.Generic;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Per-room match config resolved at room-creation time: the stage selector (<see cref="StageId"/>)
    /// and the opaque game-defined match-config payload (<see cref="MatchConfigData"/>). The core carries
    /// both as opaque values — meaning is owned by the game.
    /// </summary>
    public readonly struct MatchConfigContext
    {
        public readonly int RoomId;
        public readonly int StageId;
        public readonly byte[] MatchConfigData;

        public MatchConfigContext(int roomId, int stageId, byte[] matchConfigData)
        {
            RoomId = roomId;
            StageId = stageId;
            MatchConfigData = matchConfigData;
        }

        /// <summary>Default single-stage context for a room: StageId 0, no payload.</summary>
        public static MatchConfigContext Default(int roomId) => new MatchConfigContext(roomId, 0, null);
    }

    /// <summary>
    /// Resolves the match config for a room at creation time.
    /// <para>
    /// Called synchronously on the room-creation (main) thread, so implementations MUST NOT block on I/O
    /// (e.g. a lobby round-trip) — resolve from an already-populated local table. Return <c>false</c> to
    /// refuse room creation (the peer is disconnected with RoomNotFound); used to forbid rooms the server
    /// has no config/reservation for.
    /// </para>
    /// </summary>
    public interface IMatchConfigSource
    {
        /// <summary>
        /// Resolves the config for <paramref name="roomId"/>. Returns false to refuse the room.
        /// </summary>
        bool TryResolve(int roomId, out MatchConfigContext cfg);
    }

    /// <summary>
    /// Lobbyless <see cref="IMatchConfigSource"/> backed by a static roomId → (stageId, payload) table.
    /// Unmapped rooms resolve to <c>false</c> (refused) — the server hosts only the configured rooms.
    /// Populate once at startup; not mutated on the room-creation path.
    /// </summary>
    public sealed class StaticMatchConfigSource : IMatchConfigSource
    {
        private readonly Dictionary<int, MatchConfigContext> _table = new Dictionary<int, MatchConfigContext>();

        /// <summary>Maps <paramref name="roomId"/> to a stage + optional payload. Fluent (returns this).</summary>
        public StaticMatchConfigSource Add(int roomId, int stageId, byte[] matchConfigData = null)
        {
            _table[roomId] = new MatchConfigContext(roomId, stageId, matchConfigData);
            return this;
        }

        public bool TryResolve(int roomId, out MatchConfigContext cfg) => _table.TryGetValue(roomId, out cfg);
    }
}
