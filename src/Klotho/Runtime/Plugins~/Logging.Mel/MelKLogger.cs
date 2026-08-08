using System;

using MelILogger = Microsoft.Extensions.Logging.ILogger;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace xpTURN.Klotho.Logging.Mel
{
    /// <summary>
    /// Bridges a Microsoft.Extensions.Logging ILogger into an IKLogger so callers can
    /// inject their own standard logger. KLogLevel values map 1:1 to MEL LogLevel.
    /// </summary>
    public sealed class MelKLogger : IKLogger
    {
        private static readonly Func<string, Exception, string> Passthrough = static (s, _) => s;

        private readonly MelILogger _inner;

        public MelKLogger(MelILogger inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public bool IsEnabled(KLogLevel level) => _inner.IsEnabled((MelLogLevel)level);

        public void Log(KLogLevel level, string message, Exception exception)
            => _inner.Log((MelLogLevel)level, default, message, exception, Passthrough);
    }
}
