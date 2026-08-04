using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Base class for commands.
    /// </summary>
    public abstract class CommandBase : ICommand
    {
        public abstract int CommandTypeId { get; }
        public int PlayerId { get; set; }
        public int Tick { get; set; }

        /// <summary>
        /// Whether this is a continuous-input command. If true, the last input is repeated during prediction.
        /// Commands sent every tick (such as movement) are true; one-shot commands (such as skills/spawns) are false (default).
        /// </summary>
        public virtual bool IsContinuousInput => false;

        /// <summary>
        /// Called on a predicted clone right after its Tick is assigned (prediction repeats the last
        /// <see cref="IsContinuousInput"/> command). Continuous commands that also carry one-shot
        /// intent (edge presses, attack/skill triggers) override this to clear those fields so the
        /// prediction repeats only the held/continuous portion and does not re-fire one-shots every
        /// predicted tick. Default is a no-op.
        /// </summary>
        public virtual void ClearOneShotForPrediction() { }

        protected CommandBase()
        {
        }

        protected CommandBase(int playerId, int tick)
        {
            PlayerId = playerId;
            Tick = tick;
        }

        // CommandBase header: type(4) + playerId(4) + tick(4) = 12 bytes
        public virtual int GetSerializedSize() => 12;

        public virtual void Serialize(ref SpanWriter writer)
        {
            writer.WriteInt32(CommandTypeId);
            writer.WriteInt32(PlayerId);
            writer.WriteInt32(Tick);
            SerializeData(ref writer);
        }

        public virtual void Deserialize(ref SpanReader reader)
        {
            int type = reader.ReadInt32();
            PlayerId = reader.ReadInt32();
            Tick = reader.ReadInt32();
            DeserializeData(ref reader);
        }

        protected abstract void SerializeData(ref SpanWriter writer);
        protected abstract void DeserializeData(ref SpanReader reader);
    }
}
