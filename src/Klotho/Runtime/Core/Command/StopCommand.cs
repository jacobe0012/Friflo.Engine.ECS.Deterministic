using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Stop intent command. An explicit "no movement, no action" signal sent by clients during
    /// the EndGracePolicy.Pause grace window to deterministically halt all characters.
    /// Game-side ICommandSystem decides the concrete meaning (e.g., zero velocity);
    /// the engine treats it as a generic command in the deterministic stream.
    /// </summary>
    [KlothoSerializable(4)]
    public partial class StopCommand : CommandBase
    {
        public StopCommand() : base() { }
        public StopCommand(int playerId, int tick) : base(playerId, tick) { }
    }
}
