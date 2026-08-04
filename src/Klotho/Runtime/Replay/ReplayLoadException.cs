using System;
using xpTURN.Klotho.Core;   // For ISimulationConfig / KlothoSessionFlow cref resolution

namespace xpTURN.Klotho.Replay
{
    /// <summary>
    /// Thrown by <see cref="ReplaySystem.LoadFromFile"/> and <see cref="KlothoSessionFlow.StartReplayFromFile"/>
    /// for any replay-load failure: file-not-found, file-read I/O, missing/invalid RPLY header,
    /// malformed payload, or metadata-derived <see cref="ISimulationConfig"/> validation failure.
    /// The inner exception (when present) carries the underlying cause.
    /// </summary>
    public sealed class ReplayLoadException : Exception
    {
        public ReplayLoadException(string message) : base(message) { }
        public ReplayLoadException(string message, Exception inner) : base(message, inner) { }
    }
}
