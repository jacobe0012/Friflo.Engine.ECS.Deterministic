// Minimal stand-ins for xpTURN.Klotho.Serialization so the physics/geometry sources
// compile without pulling the full Serialization + Core/Pool + ECS subsystem.
// The probe never serializes (it only calls FPPhysicsWorld.Step), so these are
// reference-only — bodies are trivial and never executed.
using System;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Physics;

namespace xpTURN.Klotho.Serialization
{
    public ref struct SpanWriter
    {
        private Span<byte> _buf;
        private int _pos;
        public SpanWriter(Span<byte> buffer) { _buf = buffer; _pos = 0; }
        public int Position => _pos;
        public void WriteBool(bool v) { }
        public void WriteByte(byte v) { }
        public void WriteInt32(int v) { }
        public void WriteUInt16(ushort v) { }
        public void WriteUInt32(uint v) { }
        public void WriteFP(FP64 v) { }
        public void WriteFP(FPVector3 v) { }
        public void WriteFPCollider(FPCollider v) { }
    }

    public ref struct SpanReader
    {
        private ReadOnlySpan<byte> _buf;
        public SpanReader(ReadOnlySpan<byte> buffer) { _buf = buffer; }
        public bool ReadBool() => default;
        public int ReadInt32() => default;
        public ushort ReadUInt16() => default;
        public uint ReadUInt32() => default;
        public FP64 ReadFP64() => default;
        public FPVector3 ReadFPVector3() => default;
        public FPCollider ReadFPCollider() => default;
        public ReadOnlySpan<byte> ReadRawBytes(int length) => default;
    }

    public sealed class SerializationBuffer : IDisposable
    {
        private readonly byte[] _data;
        private SerializationBuffer(int size) { _data = new byte[size]; }
        public static SerializationBuffer Create(int size) => new SerializationBuffer(size);
        public Span<byte> Span => _data;
        public void Dispose() { }
    }
}
