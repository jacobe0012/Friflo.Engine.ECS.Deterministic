using System.Runtime.InteropServices;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Per-player roster entry. Replaces the index-parallel lists
    /// (PlayerIds / PlayerConnectionStates / ReadyStates / Accounts / DisplayNames) previously
    /// carried by LateJoinAccept / ReconnectAccept / SyncComplete and the in-memory ConnectionResult.
    /// ReadyState is meaningful only on the normal-join path; LateJoin / Reconnect leave it 0.
    /// </summary>
    [KlothoSerializableStruct]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct RosterEntry
    {
        public int PlayerId;
        public byte ConnectionState;          // PlayerConnectionState
        public byte ReadyState;               // 0/1 (unused on LateJoin / Reconnect → 0)
        public FixedString64 Account;
        public FixedString64 DisplayName;

        /// <summary>
        /// Converts an authoritative name string to <see cref="FixedString64"/> at the roster write
        /// boundary (host). FixedString64 holds 62 UTF-8 bytes; longer inputs are truncated at a char
        /// boundary by <see cref="FixedString64.FromString"/> (valid UTF-8 preserved). Truncation is
        /// realistically not expected, so it is surfaced — a warning log on every build and a
        /// <see cref="System.Diagnostics.Debug.Assert"/> in development builds — rather than silently lost.
        /// </summary>
        public static FixedString64 ToFixedName(string s, IKLogger logger, string field, int playerId)
        {
            int byteCount = string.IsNullOrEmpty(s) ? 0 : System.Text.Encoding.UTF8.GetByteCount(s);
            if (byteCount > 62)
            {
                logger?.KWarning($"[Roster] {field} truncated: player={playerId}, {byteCount}B > 62B");
                System.Diagnostics.Debug.Assert(false, $"[Roster] {field} exceeds FixedString64 (62B) for player {playerId}");
            }
            return FixedString64.FromString(s);
        }

        /// <summary>
        /// Truncates an untrusted claimed display name to the <see cref="FixedString64"/> capacity
        /// (62 UTF-8 bytes) at a char boundary, reusing <see cref="FixedString64.FromString"/>'s rule.
        /// Applied at the join trust boundary so over-length input never reaches <see cref="ToFixedName"/>,
        /// whose development-build assert is meant to flag authoritative (internal) names only.
        /// </summary>
        public static string ClampClaimedName(string s)
        {
            if (string.IsNullOrEmpty(s) || System.Text.Encoding.UTF8.GetByteCount(s) <= 62)
                return s ?? string.Empty;
            return FixedString64.FromString(s).ToString();
        }

        /// <summary>
        /// True when a (non-empty) verified account exceeds the <see cref="FixedString64"/> capacity
        /// (62 UTF-8 bytes). Such an account would be silently truncated by <see cref="ToFixedName"/> on
        /// the roster write — colliding distinct identities (and tripping its development-build assert).
        /// The join trust boundary uses this to reject an over-length validator-supplied account rather
        /// than truncate it. An empty account is NOT over-bound (the Accept contract permits empty).
        /// </summary>
        public static bool IsAccountOverBound(string account)
            => !string.IsNullOrEmpty(account) && System.Text.Encoding.UTF8.GetByteCount(account) > 62;

        /// <summary>
        /// Builds a roster entry from a player, converting Account/DisplayName at the write boundary via
        /// <see cref="ToFixedName"/>. <paramref name="connectionState"/> is supplied by the caller (the
        /// Reconnect path derives it from disconnected state); <paramref name="readyState"/> defaults to 0
        /// (LateJoin / Reconnect leave it unset).
        /// </summary>
        public static RosterEntry FromPlayer(IPlayerInfo player, IKLogger logger, byte connectionState, byte readyState = 0)
        {
            return new RosterEntry
            {
                PlayerId        = player.PlayerId,
                ConnectionState = connectionState,
                ReadyState      = readyState,
                Account         = ToFixedName(player.Account, logger, "Account", player.PlayerId),
                DisplayName     = ToFixedName(player.DisplayName, logger, "DisplayName", player.PlayerId),
            };
        }
    }
}
