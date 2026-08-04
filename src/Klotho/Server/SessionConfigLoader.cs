using System;
using xpTURN.Klotho.Logging;
using System.IO;
using Newtonsoft.Json;


namespace xpTURN.Klotho.Core
{
    public static class SessionConfigLoader
    {
        private const string FileName = "sessionconfig.json";

        private static readonly JsonSerializerSettings s_settings = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
        };

        public static SessionConfig Load(string[] args, IKLogger logger)
        {
            string path = ConfigPathResolver.Resolve(FileName, args);

            if (path == null)
            {
                logger.KWarning(
                    $"[SessionConfigLoader] {FileName} not found, using defaults.");
                return new SessionConfig();
            }

            logger.KInformation(
                $"[SessionConfigLoader] Loading from: {path}");

            try
            {
                string json = File.ReadAllText(path);
                var config = JsonConvert.DeserializeObject<SessionConfig>(json, s_settings)
                             ?? new SessionConfig();

                int clampedMinPlayers = Math.Clamp(config.MinPlayers, 1, config.MaxPlayers);
                if (clampedMinPlayers != config.MinPlayers)
                {
                    logger.KWarning(
                        $"[SessionConfigLoader] MinPlayers clamped: {config.MinPlayers} -> {clampedMinPlayers} (range: 1..{config.MaxPlayers})");
                    config.MinPlayers = clampedMinPlayers;
                }

                logger.KInformation(
                    $"[SessionConfigLoader] AllowLateJoin={config.AllowLateJoin}, ReconnectTimeoutMs={config.ReconnectTimeoutMs}, MinPlayers={config.MinPlayers}, MaxPlayers={config.MaxPlayers}");

                return config;
            }
            catch (JsonException ex)
            {
                logger.KError(
                    $"[SessionConfigLoader] Failed to parse '{path}': {ex.Message}");
                throw;
            }
        }
    }
}
