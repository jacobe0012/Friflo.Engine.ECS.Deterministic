// Portable sinks (Console / RollingFile). No engine dependency, so they can be reused outside Unity.
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace xpTURN.Klotho.Logging
{
    /// <summary>Console sink. Emits a "{ts}|{short}|" prefix.</summary>
    public sealed class ConsoleSink : IKLogSink
    {
        private const string DefaultTimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";

        private readonly object _gate = new object();
        private readonly string _tsFormat;

        public ConsoleSink(string timestampFormat = DefaultTimestampFormat)
            => _tsFormat = ResolveFormat(timestampFormat);

        // A malformed custom format would otherwise throw FormatException on every write. Validate it
        // once here and fall back to the default so a bad config can never make logging throw.
        private static string ResolveFormat(string timestampFormat)
        {
            string fmt = string.IsNullOrEmpty(timestampFormat) ? DefaultTimestampFormat : timestampFormat;
            try { _ = default(DateTime).ToString(fmt, CultureInfo.InvariantCulture); }
            catch (FormatException) { fmt = DefaultTimestampFormat; }
            return fmt;
        }

        // Per-thread line buffer so the whole line is written in a single atomic call (no allocation,
        // no inter-line interleaving even with concurrent writers).
        [ThreadStatic] private static char[] t_line;

        public void Write(KLogLevel level, string message, Exception exception)
        {
            char[] buf = t_line ??= new char[256];
            int pos = 0;

            int tn;
            while (!DateTime.Now.TryFormat(buf.AsSpan(pos), out tn, _tsFormat, CultureInfo.InvariantCulture))
                buf = Grow(buf, pos + 32);
            pos += tn;

            pos = AppendChar(ref buf, pos, '|');
            pos = AppendStr(ref buf, pos, KLogLevelShort.Of(level));
            pos = AppendChar(ref buf, pos, '|');
            pos = AppendStr(ref buf, pos, message);
            if (exception != null)
            {
                pos = AppendChar(ref buf, pos, '\n');
                pos = AppendStr(ref buf, pos, exception.Message);
                pos = AppendChar(ref buf, pos, '\n');
                pos = AppendStr(ref buf, pos, exception.StackTrace);
            }
            pos = AppendStr(ref buf, pos, Environment.NewLine);
            t_line = buf;

            lock (_gate) Console.Out.Write(buf, 0, pos);
        }

        private static char[] Grow(char[] buf, int needed)
        {
            var bigger = new char[Math.Max(buf.Length * 2, needed)];
            Array.Copy(buf, bigger, buf.Length);
            return bigger;
        }

        private static int AppendChar(ref char[] buf, int pos, char c)
        {
            if (pos + 1 > buf.Length) buf = Grow(buf, pos + 1);
            buf[pos] = c;
            return pos + 1;
        }

        private static int AppendStr(ref char[] buf, int pos, string s)
        {
            if (string.IsNullOrEmpty(s)) return pos;
            if (pos + s.Length > buf.Length) buf = Grow(buf, pos + s.Length);
            s.AsSpan().CopyTo(buf.AsSpan(pos));
            return pos + s.Length;
        }

        public void Flush() { lock (_gate) Console.Out.Flush(); }
        public void Dispose() { Flush(); }
    }

    /// <summary>Flush policy for the rolling file sink. PerLine is the default and unchanged behavior.</summary>
    public enum KFlushMode { PerLine, AsyncEvent }

    /// <summary>Rolling file sink (by day and by size). Emits a "{ts}|{short}|" prefix.</summary>
    public sealed class RollingFileSink : IKLogSink
    {
        private const string DefaultTimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";

        private readonly object _gate = new object();
        private readonly string _dir;
        private readonly string _prefix;
        private readonly long _rollingSizeBytes;
        private readonly string _tsFormat;

        private StreamWriter _writer;
        private DateTime _curDate;
        private int _index;
        private long _written;

        // Final flush on process termination when callers leak the factory. Unsubscribed on Dispose.
        private readonly EventHandler _processExitHandler;

        // AsyncEvent mode only; null in PerLine mode.
        private readonly KFlushMode _flushMode;
        private readonly ManualResetEventSlim _flushSignal;
        private readonly Thread _flusher;
        private volatile bool _stopping;
        private int _terminated; // Interlocked one-shot gate for TerminateAndClose.

        public RollingFileSink(string filePrefix = "Client", int rollingSizeKB = 1024 * 1024, string dir = "Logs")
            : this(filePrefix, rollingSizeKB, dir, KFlushMode.PerLine) { }

        public RollingFileSink(string filePrefix, int rollingSizeKB, string dir, KFlushMode flushMode, string timestampFormat = null)
        {
            _prefix = filePrefix;
            _rollingSizeBytes = (long)rollingSizeKB * 1024;
            _dir = dir;
            _flushMode = flushMode;
            _tsFormat = ResolveFormat(timestampFormat);
            _processExitHandler = OnProcessExit;
            try { AppDomain.CurrentDomain.ProcessExit += _processExitHandler; } catch { }
            try { AppDomain.CurrentDomain.DomainUnload += _processExitHandler; } catch { }

            if (_flushMode == KFlushMode.AsyncEvent)
            {
                _flushSignal = new ManualResetEventSlim(false);
                // Background so a missed termination path can never block process exit. Started last,
                // after every field the loop touches is initialized, to avoid a this-escape.
                _flusher = new Thread(FlusherLoop) { IsBackground = true, Name = "RollingFileSink.Flusher" };
                _flusher.Start();
            }
        }

        // A malformed custom format would otherwise throw FormatException on every write. Validate it
        // once here and fall back to the default so a bad config can never make logging throw.
        private static string ResolveFormat(string timestampFormat)
        {
            string fmt = string.IsNullOrEmpty(timestampFormat) ? DefaultTimestampFormat : timestampFormat;
            try { _ = default(DateTime).ToString(fmt, CultureInfo.InvariantCulture); }
            catch (FormatException) { fmt = DefaultTimestampFormat; }
            return fmt;
        }

        private void OnProcessExit(object sender, EventArgs e)
        {
            try { TerminateAndClose(); } catch { }
        }

        public void Write(KLogLevel level, string message, Exception exception)
        {
            Span<char> ts = stackalloc char[32];
            lock (_gate)
            {
                // Once termination has begun, drop the line instead of reopening a writer that would
                // never be flushed or closed. A late write racing with Dispose/ProcessExit is lost.
                if (_stopping) return;

                var now = DateTime.Now;
                EnsureWriter(now);

                // Fast path formats into the stack buffer; a custom format longer than the buffer
                // falls back to a heap string so the timestamp is never silently truncated.
                int n;
                string tsFallback = null;
                if (!now.TryFormat(ts, out n, _tsFormat, CultureInfo.InvariantCulture))
                {
                    tsFallback = now.ToString(_tsFormat, CultureInfo.InvariantCulture);
                    n = tsFallback.Length;
                }

                string shortLevel = KLogLevelShort.Of(level);
                if (tsFallback != null) _writer.Write(tsFallback); else _writer.Write(ts.Slice(0, n));
                _writer.Write('|'); _writer.Write(shortLevel); _writer.Write('|'); _writer.Write(message);
                long len = n + 2 + shortLevel.Length + (message?.Length ?? 0);
                if (exception != null)
                {
                    _writer.Write('\n'); _writer.Write(exception.Message); _writer.Write('\n'); _writer.Write(exception.StackTrace);
                    len += 2 + (exception.Message?.Length ?? 0) + (exception.StackTrace?.Length ?? 0);
                }
                _writer.Write(Environment.NewLine);
                _written += len + Environment.NewLine.Length;

                if (_written >= _rollingSizeBytes)
                {
                    RollNext(now); // CloseWriter flushes the old file; OpenWriter resets counters
                }
                else if (_flushMode == KFlushMode.PerLine)
                {
                    // Flush each line so logs are immediately visible and a process crash loses at most
                    // the current line in flight. Flush pushes to the OS file cache, which survives the
                    // process. One syscall per line; negligible at typical game logging volumes.
                    try { _writer.Flush(); } catch { }
                }
                else
                {
                    // AsyncEvent: no flush on the hot path. Wake the background flusher; a boolean event
                    // coalesces a burst of writes into a single flush (natural batching).
                    try { _flushSignal.Set(); } catch (ObjectDisposedException) { }
                }
            }
        }

        private void EnsureWriter(DateTime now)
        {
            if (_writer != null && now.Date == _curDate) return;
            CloseWriter();
            _curDate = now.Date;
            _index = 0;
            OpenWriter(now);
        }

        private void RollNext(DateTime now)
        {
            CloseWriter();
            _index++;
            OpenWriter(now);
        }

        private void OpenWriter(DateTime now)
        {
            Directory.CreateDirectory(_dir);
            string path = Path.Combine(_dir, $"{_prefix}_{now:yyyy-MM-dd-HH-mm-ss-fff}_{_index:000}.log");
            _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), Encoding.UTF8) { AutoFlush = false };
            _written = 0;
        }

        private void CloseWriter()
        {
            if (_writer == null) return;
            try { _writer.Flush(); _writer.Dispose(); } catch { }
            _writer = null;
        }

        // AsyncEvent consumer: sleeps until signalled, then flushes whatever the producers buffered.
        private void FlusherLoop()
        {
            while (!_stopping)
            {
                try
                {
                    _flushSignal.Wait();
                    _flushSignal.Reset(); // Reset before Flush: a Set during Flush re-wakes the next iteration.
                }
                catch (ObjectDisposedException) { return; }
                if (_stopping) break;
                lock (_gate) { try { _writer?.Flush(); } catch { } }
            }
            // Drain: flush any line that arrived after the stop flag was observed.
            lock (_gate) { try { _writer?.Flush(); } catch { } }
        }

        // Unified graceful shutdown for Dispose / ProcessExit / DomainUnload. One-shot via Interlocked;
        // later callers only re-run the idempotent CloseWriter.
        private void TerminateAndClose()
        {
            if (Interlocked.Exchange(ref _terminated, 1) != 0)
            {
                lock (_gate) CloseWriter();
                return;
            }

            _stopping = true;
            if (_flushSignal != null) { try { _flushSignal.Set(); } catch (ObjectDisposedException) { } }
            if (_flusher != null) { try { _flusher.Join(TimeSpan.FromSeconds(1)); } catch { } }
            lock (_gate) CloseWriter(); // synchronous final flush; safety net even if Join timed out
            if (_flushSignal != null) { try { _flushSignal.Dispose(); } catch { } } // after Join only
        }

        public void Flush() { lock (_gate) { try { _writer?.Flush(); } catch { } } }
        public void Dispose()
        {
            try { AppDomain.CurrentDomain.ProcessExit -= _processExitHandler; } catch { }
            try { AppDomain.CurrentDomain.DomainUnload -= _processExitHandler; } catch { }
            TerminateAndClose();
        }
    }
}
