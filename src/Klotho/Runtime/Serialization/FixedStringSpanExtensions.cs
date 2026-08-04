using System;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Serialization
{
    /// <summary>
    /// SpanWriter/SpanReader codec for the fixed-width string structs (<see cref="FixedString32"/>,
    /// <see cref="FixedString64"/>). Wire format is the full buffer (30/62 bytes) followed by the
    /// int16 length — a constant 32/64 bytes. Methods are <c>unsafe</c> to reach the fixed buffer but
    /// expose pointer-free signatures, so the generated (non-unsafe) host codecs can call them.
    /// The struct is passed by value (a stack-local copy = fixed variable), so no <c>fixed</c>
    /// statement is needed; do not change to <c>in</c>.
    /// </summary>
    public static class FixedStringSpanExtensions
    {
        public static unsafe void WriteFixedString32(ref this SpanWriter writer, FixedString32 value)
        {
            writer.WriteRawBytes(new ReadOnlySpan<byte>(value.Bytes, 30));
            writer.WriteInt16(value.Length);
        }

        public static unsafe FixedString32 ReadFixedString32(ref this SpanReader reader)
        {
            var v = new FixedString32();
            reader.ReadRawBytes(30).CopyTo(new Span<byte>(v.Bytes, 30));
            v.Length = (short)Math.Clamp((int)reader.ReadInt16(), 0, 30);
            return v;
        }

        public static unsafe void WriteFixedString64(ref this SpanWriter writer, FixedString64 value)
        {
            writer.WriteRawBytes(new ReadOnlySpan<byte>(value.Bytes, 62));
            writer.WriteInt16(value.Length);
        }

        public static unsafe FixedString64 ReadFixedString64(ref this SpanReader reader)
        {
            var v = new FixedString64();
            reader.ReadRawBytes(62).CopyTo(new Span<byte>(v.Bytes, 62));
            v.Length = (short)Math.Clamp((int)reader.ReadInt16(), 0, 62);
            return v;
        }
    }
}
