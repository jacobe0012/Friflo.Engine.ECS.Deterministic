using System;

namespace xpTURN.Klotho.Logging
{
    /// <summary>Severity levels. Integer values are ordered so that higher = more severe and a minimum-level gate is a simple comparison.</summary>
    public enum KLogLevel
    {
        Trace = 0,
        Debug = 1,
        Information = 2,
        Warning = 3,
        Error = 4,
        Critical = 5,
        None = 6,
    }

    /// <summary>
    /// Minimal logging contract: a level gate plus emitting a finished message.
    /// Message formatting is done by the call-site handler before reaching here.
    /// </summary>
    public interface IKLogger
    {
        bool IsEnabled(KLogLevel level);
        void Log(KLogLevel level, string message, Exception exception);
    }

    public interface IKLoggerFactory : IDisposable
    {
        IKLogger CreateLogger(string category);
    }
}
