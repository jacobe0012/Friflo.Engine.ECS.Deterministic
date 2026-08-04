using System;
using System.Collections.Generic;

namespace xpTURN.Klotho.Logging
{
    /// <summary>Receives the level, exception and finished message. Message formatting is already done by the handler.</summary>
    public interface IKLogSink : IDisposable
    {
        void Write(KLogLevel level, string message, Exception exception);
        void Flush();
    }

    public sealed partial class KLoggerFactory : IKLoggerFactory
    {
        private readonly KLogLevel _min;
        private readonly List<IKLogSink> _sinks;

        public KLoggerFactory(KLogLevel minimumLevel, IReadOnlyList<IKLogSink> sinks)
        {
            _min = minimumLevel;
            _sinks = new List<IKLogSink>(sinks);
        }

        public IKLogger CreateLogger(string category) => new KLogger(category, _min, _sinks);

        public void Dispose()
        {
            for (int i = 0; i < _sinks.Count; i++)
            {
                try { _sinks[i].Flush(); _sinks[i].Dispose(); }
                catch { /* a failing sink must not block the others */ }
            }
        }
    }

    internal sealed class KLogger : IKLogger
    {
        private readonly string _category;
        private readonly KLogLevel _min;
        private readonly List<IKLogSink> _sinks;

        public KLogger(string category, KLogLevel min, List<IKLogSink> sinks)
        {
            _category = category; _min = min; _sinks = sinks;
        }

        public bool IsEnabled(KLogLevel level) => level >= _min && level != KLogLevel.None;

        public void Log(KLogLevel level, string message, Exception exception)
        {
            if (!IsEnabled(level)) return;
            var sinks = _sinks;
            for (int i = 0; i < sinks.Count; i++) sinks[i].Write(level, message, exception);
        }
    }

    /// <summary>Short, fixed-width level token used by the file/console sinks.</summary>
    internal static class KLogLevelShort
    {
        public static string Of(KLogLevel level) => level switch
        {
            KLogLevel.Trace => "TRCE",
            KLogLevel.Debug => "DBUG",
            KLogLevel.Information => "INFO",
            KLogLevel.Warning => "WARN",
            KLogLevel.Error => "FAIL",
            KLogLevel.Critical => "CRIT",
            _ => "NONE",
        };
    }
}
