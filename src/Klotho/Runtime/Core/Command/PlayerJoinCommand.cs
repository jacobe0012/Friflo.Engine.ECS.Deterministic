using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// System command for a player joining.
    /// </summary>
    [KlothoSerializable(10)]
    public partial class PlayerJoinCommand : CommandBase, ISystemCommand
    {
        public int JoinedPlayerId;

        // Sorts before same-tick gameplay reliable commands (OrderKey 0): a join must establish the
        // player's participant slot and any join-time world setup before that tick's other commands run.
        // The negative base keeps joins ahead of gameplay; +JoinedPlayerId is a stable tiebreak for
        // simultaneous joins. OrderKey is sort-only (CommandOrdering.Compare), never serialized.
        private const int JoinOrderBase = -1_000_000;
        public int OrderKey => JoinOrderBase + JoinedPlayerId;

        public override int GetSerializedSize() => base.GetSerializedSize() + 4;

        protected override void SerializeData(ref SpanWriter writer)
        {
            writer.WriteInt32(JoinedPlayerId);
        }

        protected override void DeserializeData(ref SpanReader reader)
        {
            JoinedPlayerId = reader.ReadInt32();
        }
    }
}
