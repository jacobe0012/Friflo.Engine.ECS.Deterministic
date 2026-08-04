using xpTURN.Klotho.Logging;

using LiteNetLib;

namespace xpTURN.Klotho.LiteNetLib
{
    /// <summary>
    /// INetLogger → IKLogger bridge adapter.
    /// </summary>
    public class LiteNetLibNetLogger : INetLogger
    {
        IKLogger _logger = null;
        int _levelMask;

        public LiteNetLibNetLogger(IKLogger logger, NetLogLevel[] levels = null )
        {
            _logger = logger;
            if (levels != null)
            {
                for (int i = 0; i < levels.Length; i++)
                    _levelMask |= 1 << (int)levels[i];
            }
        }

        public void WriteNet(NetLogLevel level, string str, params object[] args)
        {
            if (_levelMask != 0 && (_levelMask & (1 << (int)level)) == 0)
                return;

            string msg = args.Length > 0 ? string.Format(str, args) : str;
            msg = $"[LiteNetLib] {msg}";
            switch (level)
            {
                case NetLogLevel.Warning:
                    _logger?.KWarning($"{msg}");
                    break;
                case NetLogLevel.Error:
                    _logger?.KError($"{msg}");
                    break;
                case NetLogLevel.Trace:
                    _logger?.KTrace($"{msg}");
                    break;
                case NetLogLevel.Info:
                default:
                    _logger?.KInformation($"{msg}");
                    break;
            }
        }
    }
}
