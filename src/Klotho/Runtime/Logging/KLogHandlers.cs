// Interpolated-string log handlers, one ref struct per level. The level is fixed by the handler
// type so call sites pass only the logger via [InterpolatedStringHandlerArgument("logger")].
// Every handler exposes the same overload group so any interpolation form {expr[,align][:fmt]} compiles.
using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace xpTURN.Klotho.Logging
{
    [InterpolatedStringHandler]
    public ref struct KLogHandlerTrace
    {
        public readonly bool Enabled; private CharBufferWriter _buf;
        public KLogHandlerTrace(int literalLength, int formattedCount, IKLogger logger, out bool enabled)
        { Enabled = enabled = logger != null && logger.IsEnabled(KLogLevel.Trace); _buf = enabled ? CharBufferWriter.Acquire(literalLength + formattedCount * 12) : default; }
        public void AppendLiteral(string s) => _buf.Append(s);
        public void AppendFormatted(int v) => _buf.AppendF(v);
        public void AppendFormatted(uint v) => _buf.AppendF(v);
        public void AppendFormatted(long v) => _buf.AppendF(v);
        public void AppendFormatted(ulong v) => _buf.AppendF(v);
        public void AppendFormatted(float v) => _buf.AppendF(v);
        public void AppendFormatted(double v) => _buf.AppendF(v);
        public void AppendFormatted(bool v) => _buf.Append(v ? "True" : "False");
        public void AppendFormatted(string s) => _buf.Append(s);
        public void AppendFormatted(ReadOnlySpan<char> s) => _buf.Append(s);
        public void AppendFormatted(int v, string format) => _buf.AppendF(v, format);
        public void AppendFormatted(long v, string format) => _buf.AppendF(v, format);
        public void AppendFormatted(ulong v, string format) => _buf.AppendF(v, format);
        public void AppendFormatted(float v, string format) => _buf.AppendF(v, format);
        public void AppendFormatted(double v, string format) => _buf.AppendF(v, format);
        public void AppendFormatted<T>(T value) => _buf.AppendValue(value);
        public void AppendFormatted<T>(T value, string format) => _buf.Append(value is IFormattable f ? f.ToString(format, CultureInfo.InvariantCulture) : value?.ToString());
        public void AppendFormatted<T>(T value, int alignment) => _buf.AppendValuePadded(value, alignment);
        public void AppendFormatted<T>(T value, int alignment, string format) => _buf.AppendPadded(value is IFormattable f ? f.ToString(format, CultureInfo.InvariantCulture) : value?.ToString(), alignment);
        public void AppendFormatted(string value, int alignment, string format) => _buf.AppendPadded(value, alignment);
        public string ToState() => _buf.ToStringAndReset();
    }

    [InterpolatedStringHandler]
    public ref struct KLogHandlerDebug
    {
        public readonly bool Enabled; private CharBufferWriter _buf;
        public KLogHandlerDebug(int literalLength, int formattedCount, IKLogger logger, out bool enabled)
        { Enabled = enabled = logger != null && logger.IsEnabled(KLogLevel.Debug); _buf = enabled ? CharBufferWriter.Acquire(literalLength + formattedCount * 12) : default; }
        public void AppendLiteral(string s) => _buf.Append(s);
        public void AppendFormatted(int v) => _buf.AppendF(v);
        public void AppendFormatted(uint v) => _buf.AppendF(v);
        public void AppendFormatted(long v) => _buf.AppendF(v);
        public void AppendFormatted(ulong v) => _buf.AppendF(v);
        public void AppendFormatted(float v) => _buf.AppendF(v);
        public void AppendFormatted(double v) => _buf.AppendF(v);
        public void AppendFormatted(bool v) => _buf.Append(v ? "True" : "False");
        public void AppendFormatted(string s) => _buf.Append(s);
        public void AppendFormatted(ReadOnlySpan<char> s) => _buf.Append(s);
        public void AppendFormatted(int v, string format) => _buf.AppendF(v, format);
        public void AppendFormatted(long v, string format) => _buf.AppendF(v, format);
        public void AppendFormatted(ulong v, string format) => _buf.AppendF(v, format);
        public void AppendFormatted(float v, string format) => _buf.AppendF(v, format);
        public void AppendFormatted(double v, string format) => _buf.AppendF(v, format);
        public void AppendFormatted<T>(T value) => _buf.AppendValue(value);
        public void AppendFormatted<T>(T value, string format) => _buf.Append(value is IFormattable f ? f.ToString(format, CultureInfo.InvariantCulture) : value?.ToString());
        public void AppendFormatted<T>(T value, int alignment) => _buf.AppendValuePadded(value, alignment);
        public void AppendFormatted<T>(T value, int alignment, string format) => _buf.AppendPadded(value is IFormattable f ? f.ToString(format, CultureInfo.InvariantCulture) : value?.ToString(), alignment);
        public void AppendFormatted(string value, int alignment, string format) => _buf.AppendPadded(value, alignment);
        public string ToState() => _buf.ToStringAndReset();
    }

    [InterpolatedStringHandler]
    public ref struct KLogHandlerInformation
    {
        public readonly bool Enabled; private CharBufferWriter _buf;
        public KLogHandlerInformation(int literalLength, int formattedCount, IKLogger logger, out bool enabled)
        { Enabled = enabled = logger != null && logger.IsEnabled(KLogLevel.Information); _buf = enabled ? CharBufferWriter.Acquire(literalLength + formattedCount * 12) : default; }
        public void AppendLiteral(string s) => _buf.Append(s);
        public void AppendFormatted(int v) => _buf.AppendF(v);
        public void AppendFormatted(uint v) => _buf.AppendF(v);
        public void AppendFormatted(long v) => _buf.AppendF(v);
        public void AppendFormatted(ulong v) => _buf.AppendF(v);
        public void AppendFormatted(float v) => _buf.AppendF(v);
        public void AppendFormatted(double v) => _buf.AppendF(v);
        public void AppendFormatted(bool v) => _buf.Append(v ? "True" : "False");
        public void AppendFormatted(string s) => _buf.Append(s);
        public void AppendFormatted(ReadOnlySpan<char> s) => _buf.Append(s);
        public void AppendFormatted(int v, string format) => _buf.AppendF(v, format);
        public void AppendFormatted(long v, string format) => _buf.AppendF(v, format);
        public void AppendFormatted(ulong v, string format) => _buf.AppendF(v, format);
        public void AppendFormatted(float v, string format) => _buf.AppendF(v, format);
        public void AppendFormatted(double v, string format) => _buf.AppendF(v, format);
        public void AppendFormatted<T>(T value) => _buf.AppendValue(value);
        public void AppendFormatted<T>(T value, string format) => _buf.Append(value is IFormattable f ? f.ToString(format, CultureInfo.InvariantCulture) : value?.ToString());
        public void AppendFormatted<T>(T value, int alignment) => _buf.AppendValuePadded(value, alignment);
        public void AppendFormatted<T>(T value, int alignment, string format) => _buf.AppendPadded(value is IFormattable f ? f.ToString(format, CultureInfo.InvariantCulture) : value?.ToString(), alignment);
        public void AppendFormatted(string value, int alignment, string format) => _buf.AppendPadded(value, alignment);
        public string ToState() => _buf.ToStringAndReset();
    }

    [InterpolatedStringHandler]
    public ref struct KLogHandlerWarning
    {
        public readonly bool Enabled; private CharBufferWriter _buf;
        public KLogHandlerWarning(int literalLength, int formattedCount, IKLogger logger, out bool enabled)
        { Enabled = enabled = logger != null && logger.IsEnabled(KLogLevel.Warning); _buf = enabled ? CharBufferWriter.Acquire(literalLength + formattedCount * 12) : default; }
        public void AppendLiteral(string s) => _buf.Append(s);
        public void AppendFormatted(int v) => _buf.AppendF(v);
        public void AppendFormatted(uint v) => _buf.AppendF(v);
        public void AppendFormatted(long v) => _buf.AppendF(v);
        public void AppendFormatted(ulong v) => _buf.AppendF(v);
        public void AppendFormatted(float v) => _buf.AppendF(v);
        public void AppendFormatted(double v) => _buf.AppendF(v);
        public void AppendFormatted(bool v) => _buf.Append(v ? "True" : "False");
        public void AppendFormatted(string s) => _buf.Append(s);
        public void AppendFormatted(ReadOnlySpan<char> s) => _buf.Append(s);
        public void AppendFormatted(int v, string format) => _buf.AppendF(v, format);
        public void AppendFormatted(long v, string format) => _buf.AppendF(v, format);
        public void AppendFormatted(ulong v, string format) => _buf.AppendF(v, format);
        public void AppendFormatted(float v, string format) => _buf.AppendF(v, format);
        public void AppendFormatted(double v, string format) => _buf.AppendF(v, format);
        public void AppendFormatted<T>(T value) => _buf.AppendValue(value);
        public void AppendFormatted<T>(T value, string format) => _buf.Append(value is IFormattable f ? f.ToString(format, CultureInfo.InvariantCulture) : value?.ToString());
        public void AppendFormatted<T>(T value, int alignment) => _buf.AppendValuePadded(value, alignment);
        public void AppendFormatted<T>(T value, int alignment, string format) => _buf.AppendPadded(value is IFormattable f ? f.ToString(format, CultureInfo.InvariantCulture) : value?.ToString(), alignment);
        public void AppendFormatted(string value, int alignment, string format) => _buf.AppendPadded(value, alignment);
        public string ToState() => _buf.ToStringAndReset();
    }

    [InterpolatedStringHandler]
    public ref struct KLogHandlerError
    {
        public readonly bool Enabled; private CharBufferWriter _buf;
        public KLogHandlerError(int literalLength, int formattedCount, IKLogger logger, out bool enabled)
        { Enabled = enabled = logger != null && logger.IsEnabled(KLogLevel.Error); _buf = enabled ? CharBufferWriter.Acquire(literalLength + formattedCount * 12) : default; }
        public void AppendLiteral(string s) => _buf.Append(s);
        public void AppendFormatted(int v) => _buf.AppendF(v);
        public void AppendFormatted(uint v) => _buf.AppendF(v);
        public void AppendFormatted(long v) => _buf.AppendF(v);
        public void AppendFormatted(ulong v) => _buf.AppendF(v);
        public void AppendFormatted(float v) => _buf.AppendF(v);
        public void AppendFormatted(double v) => _buf.AppendF(v);
        public void AppendFormatted(bool v) => _buf.Append(v ? "True" : "False");
        public void AppendFormatted(string s) => _buf.Append(s);
        public void AppendFormatted(ReadOnlySpan<char> s) => _buf.Append(s);
        public void AppendFormatted(int v, string format) => _buf.AppendF(v, format);
        public void AppendFormatted(long v, string format) => _buf.AppendF(v, format);
        public void AppendFormatted(ulong v, string format) => _buf.AppendF(v, format);
        public void AppendFormatted(float v, string format) => _buf.AppendF(v, format);
        public void AppendFormatted(double v, string format) => _buf.AppendF(v, format);
        public void AppendFormatted<T>(T value) => _buf.AppendValue(value);
        public void AppendFormatted<T>(T value, string format) => _buf.Append(value is IFormattable f ? f.ToString(format, CultureInfo.InvariantCulture) : value?.ToString());
        public void AppendFormatted<T>(T value, int alignment) => _buf.AppendValuePadded(value, alignment);
        public void AppendFormatted<T>(T value, int alignment, string format) => _buf.AppendPadded(value is IFormattable f ? f.ToString(format, CultureInfo.InvariantCulture) : value?.ToString(), alignment);
        public void AppendFormatted(string value, int alignment, string format) => _buf.AppendPadded(value, alignment);
        public string ToState() => _buf.ToStringAndReset();
    }

    // Runtime-level handler: the level is supplied at the call site, gated via IsEnabled(logLevel).
    [InterpolatedStringHandler]
    public ref struct KLogHandler
    {
        public readonly bool Enabled; private CharBufferWriter _buf;
        public KLogHandler(int literalLength, int formattedCount, IKLogger logger, KLogLevel logLevel, out bool enabled)
        { Enabled = enabled = logger != null && logger.IsEnabled(logLevel); _buf = enabled ? CharBufferWriter.Acquire(literalLength + formattedCount * 12) : default; }
        public void AppendLiteral(string s) => _buf.Append(s);
        public void AppendFormatted(int v) => _buf.AppendF(v);
        public void AppendFormatted(uint v) => _buf.AppendF(v);
        public void AppendFormatted(long v) => _buf.AppendF(v);
        public void AppendFormatted(ulong v) => _buf.AppendF(v);
        public void AppendFormatted(float v) => _buf.AppendF(v);
        public void AppendFormatted(double v) => _buf.AppendF(v);
        public void AppendFormatted(bool v) => _buf.Append(v ? "True" : "False");
        public void AppendFormatted(string s) => _buf.Append(s);
        public void AppendFormatted(ReadOnlySpan<char> s) => _buf.Append(s);
        public void AppendFormatted(int v, string format) => _buf.AppendF(v, format);
        public void AppendFormatted(long v, string format) => _buf.AppendF(v, format);
        public void AppendFormatted(ulong v, string format) => _buf.AppendF(v, format);
        public void AppendFormatted(float v, string format) => _buf.AppendF(v, format);
        public void AppendFormatted(double v, string format) => _buf.AppendF(v, format);
        public void AppendFormatted<T>(T value) => _buf.AppendValue(value);
        public void AppendFormatted<T>(T value, string format) => _buf.Append(value is IFormattable f ? f.ToString(format, CultureInfo.InvariantCulture) : value?.ToString());
        public void AppendFormatted<T>(T value, int alignment) => _buf.AppendValuePadded(value, alignment);
        public void AppendFormatted<T>(T value, int alignment, string format) => _buf.AppendPadded(value is IFormattable f ? f.ToString(format, CultureInfo.InvariantCulture) : value?.ToString(), alignment);
        public void AppendFormatted(string value, int alignment, string format) => _buf.AppendPadded(value, alignment);
        public string ToState() => _buf.ToStringAndReset();
    }
}
