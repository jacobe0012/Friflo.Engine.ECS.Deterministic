namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Client-side provider of the lobby-issued identity credential (ticket) carried in the join
    /// handshake (PlayerJoinMessage.Ticket). The ticket is opaque to the core — its content
    /// (account / displayName / signature) is defined and validated by the game's lobby integration
    /// layer. Mirrors <see cref="IDeviceIdProvider"/>; the game supplies the
    /// implementation. When no lobby is used, this provider is left unset and the ticket is empty.
    /// </summary>
    public interface IPlayerIdentityProvider
    {
        /// <summary>
        /// Returns the lobby-issued ticket (e.g. base64url-encoded signed payload), or null/empty
        /// when none. Called once per connect; implementations may fetch/refresh lazily.
        /// </summary>
        string GetTicket();
    }
}
