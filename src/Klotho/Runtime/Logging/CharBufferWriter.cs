using System;
using System.Collections.Generic;
using System.Globalization;

namespace xpTURN.Klotho.Logging
{
    /// <summary>
    /// Formatting buffer for the interpolated-string log handlers. A struct that rents a reusable
    /// [ThreadStatic] char[] so no per-call allocation occurs on the common path. Only acquired when
    /// the target level is enabled; a single string is produced once via ToStringAndReset, which also
    /// returns the buffer to the pool.
    /// Re-entrant logging on the same thread (logging while formatting a log) is safe: the nested
    /// call finds the pooled buffer already rented and allocates its own, so the outer buffer is
    /// never corrupted. The pool self-heals if a buffer is not returned (e.g. an exception mid-format).
    /// </summary>
    internal struct CharBufferWriter
    {
        // The idle reusable buffer for this thread; null while rented by an active writer.
        [ThreadStatic] private static char[] t_pooled;

        private char[] _buf;
        private int _pos;

        public static CharBufferWriter Acquire(int hint)
        {
            char[] buf = t_pooled;
            if (buf == null || buf.Length < hint)
                buf = new char[Math.Max(256, hint)];
            t_pooled = null; // rent: a re-entrant Acquire on this thread allocates its own buffer
            return new CharBufferWriter { _buf = buf, _pos = 0 };
        }

        private void Grow(int needed)
        {
            int cap = Math.Max(_buf.Length * 2, _pos + needed);
            var bigger = new char[cap];
            Array.Copy(_buf, bigger, _pos);
            _buf = bigger;
        }

        public void Append(string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            if (_pos + s.Length > _buf.Length) Grow(s.Length);
            s.AsSpan().CopyTo(_buf.AsSpan(_pos));
            _pos += s.Length;
        }

        public void Append(ReadOnlySpan<char> s)
        {
            if (s.Length == 0) return;
            if (_pos + s.Length > _buf.Length) Grow(s.Length);
            s.CopyTo(_buf.AsSpan(_pos));
            _pos += s.Length;
        }

        // Primitive overloads format directly via TryFormat (no boxing). Grow and retry on overflow.
        public void AppendF(int v)    { int w; while (!v.TryFormat(_buf.AsSpan(_pos), out w, default, CultureInfo.InvariantCulture)) Grow(32); _pos += w; }
        public void AppendF(uint v)   { int w; while (!v.TryFormat(_buf.AsSpan(_pos), out w, default, CultureInfo.InvariantCulture)) Grow(32); _pos += w; }
        public void AppendF(long v)   { int w; while (!v.TryFormat(_buf.AsSpan(_pos), out w, default, CultureInfo.InvariantCulture)) Grow(32); _pos += w; }
        public void AppendF(ulong v)  { int w; while (!v.TryFormat(_buf.AsSpan(_pos), out w, default, CultureInfo.InvariantCulture)) Grow(32); _pos += w; }
        public void AppendF(float v)  { int w; while (!v.TryFormat(_buf.AsSpan(_pos), out w, default, CultureInfo.InvariantCulture)) Grow(32); _pos += w; }
        public void AppendF(double v) { int w; while (!v.TryFormat(_buf.AsSpan(_pos), out w, default, CultureInfo.InvariantCulture)) Grow(32); _pos += w; }

        public void AppendF(int v, string format)    { int w; while (!v.TryFormat(_buf.AsSpan(_pos), out w, format.AsSpan(), CultureInfo.InvariantCulture)) Grow(32); _pos += w; }
        public void AppendF(long v, string format)   { int w; while (!v.TryFormat(_buf.AsSpan(_pos), out w, format.AsSpan(), CultureInfo.InvariantCulture)) Grow(32); _pos += w; }
        public void AppendF(ulong v, string format)  { int w; while (!v.TryFormat(_buf.AsSpan(_pos), out w, format.AsSpan(), CultureInfo.InvariantCulture)) Grow(32); _pos += w; }
        public void AppendF(float v, string format)  { int w; while (!v.TryFormat(_buf.AsSpan(_pos), out w, format.AsSpan(), CultureInfo.InvariantCulture)) Grow(32); _pos += w; }
        public void AppendF(double v, string format) { int w; while (!v.TryFormat(_buf.AsSpan(_pos), out w, format.AsSpan(), CultureInfo.InvariantCulture)) Grow(32); _pos += w; }

        // Generic fallback. Enums resolve to a cached name (no allocation); other value types box once via ToString.
        public void AppendValue<T>(T value)
        {
            if (EnumNames<T>.IsEnum) { Append(EnumNames<T>.GetName(value)); return; }
            Append(value?.ToString());
        }

        public void AppendValuePadded<T>(T value, int alignment)
        {
            string s = EnumNames<T>.IsEnum ? EnumNames<T>.GetName(value) : value?.ToString();
            AppendPadded(s, alignment);
        }

        public void AppendPadded(string value, int alignment)
        {
            value ??= "";
            int pad = Math.Abs(alignment) - value.Length;
            if (alignment > 0 && pad > 0) AppendSpaces(pad);
            Append(value);
            if (alignment < 0 && pad > 0) AppendSpaces(pad);
        }

        private void AppendSpaces(int n)
        {
            if (_pos + n > _buf.Length) Grow(n);
            for (int i = 0; i < n; i++) _buf[_pos + i] = ' ';
            _pos += n;
        }

        public string ToStringAndReset()
        {
            string r = new string(_buf, 0, _pos);
            _pos = 0;
            t_pooled = _buf; // return the buffer (possibly grown) to the pool for reuse
            return r;
        }
    }

    /// <summary>
    /// Per-T cache of enum names. Defined enum values return a cached name string (no per-call
    /// allocation). Matching relies on EqualityComparer&lt;T&gt;.Default, which avoids boxing for enums.
    /// </summary>
    internal static class EnumNames<T>
    {
        public static readonly bool IsEnum = typeof(T).IsEnum;
        private static readonly T[] _values = IsEnum ? (T[])Enum.GetValues(typeof(T)) : Array.Empty<T>();
        private static readonly string[] _names = IsEnum ? Enum.GetNames(typeof(T)) : Array.Empty<string>();

        public static string GetName(T value)
        {
            var cmp = EqualityComparer<T>.Default;
            for (int i = 0; i < _values.Length; i++)
                if (cmp.Equals(value, _values[i])) return _names[i];
            return value.ToString(); // flags combinations / undefined values
        }
    }
}
