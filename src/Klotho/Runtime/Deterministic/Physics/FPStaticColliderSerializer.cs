using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;

namespace xpTURN.Klotho.Deterministic.Physics
{
    /// <summary>
    /// Binary serialization/deserialization of static collider arrays.
    /// </summary>
    public static class FPStaticColliderSerializer
    {
        const uint Magic = 0x46505343;  // "FPSC"
        const ushort Version = 1;
        const int HeaderSize = 8;

        // Called from the Editor — write directly via SpanWriter, then File.WriteAllBytes
        public static void Save(FPStaticCollider[] colliders, string assetPath)
        {
            int size = HeaderSize;
            foreach (var c in colliders) size += c.GetSerializedSize();

            var buf = new byte[size];
            var writer = new SpanWriter(buf);

            writer.WriteUInt32(Magic);
            writer.WriteUInt16(Version);
            writer.WriteUInt16((ushort)colliders.Length);

            foreach (var c in colliders) c.Serialize(ref writer);

            File.WriteAllBytes(assetPath, buf);
        }

        // Called at runtime
        public static List<FPStaticCollider> Load(string path)
        {
            var buffer = System.IO.File.ReadAllBytes(path);
            return Load(buffer);
        }

        // Called at runtime (after TextAsset.bytes or Addressables load)
        public static List<FPStaticCollider> Load(ReadOnlySpan<byte> buffer)
        {
            var reader = new SpanReader(buffer);

            uint magic = reader.ReadUInt32();
            ushort version = reader.ReadUInt16();
            int count = reader.ReadUInt16();

            if (magic != Magic)
                throw new InvalidOperationException($"Invalid FPSC magic: 0x{magic:X8}");

            var result = new List<FPStaticCollider>(count);
            for (int i = 0; i < count; i++)
                result.Add(FPStaticCollider.Deserialize(ref reader));

            return result;
        }

        // Debug/inspection JSON sidecar. Engine-agnostic — shared by the exporters.
        // Export-time only (allocates); not on the simulation hot path.
        public static string ToJson(IList<FPStaticCollider> list)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[");
            for (int i = 0; i < list.Count; i++)
            {
                var sc = list[i];
                var c = sc.collider;
                sb.Append("  {");
                sb.Append($"\"id\":{sc.id}");
                sb.Append($",\"isTrigger\":{(sc.isTrigger ? "true" : "false")}");
                sb.Append($",\"restitution\":{sc.restitution.ToFloat()}");
                sb.Append($",\"friction\":{sc.friction.ToFloat()}");
                sb.Append($",\"shape\":\"{c.type}\"");

                switch (c.type)
                {
                    case ShapeType.Sphere:
                        sb.Append($",\"position\":{Vec3(c.sphere.position)}");
                        sb.Append($",\"radius\":{c.sphere.radius.ToFloat()}");
                        break;
                    case ShapeType.Box:
                        sb.Append($",\"position\":{Vec3(c.box.position)}");
                        sb.Append($",\"rotation\":{Quat(c.box.rotation)}");
                        sb.Append($",\"halfExtents\":{Vec3(c.box.halfExtents)}");
                        break;
                    case ShapeType.Capsule:
                        sb.Append($",\"position\":{Vec3(c.capsule.position)}");
                        sb.Append($",\"rotation\":{Quat(c.capsule.rotation)}");
                        sb.Append($",\"halfHeight\":{c.capsule.halfHeight.ToFloat()}");
                        sb.Append($",\"radius\":{c.capsule.radius.ToFloat()}");
                        break;
                    case ShapeType.Mesh:
                        sb.Append($",\"position\":{Vec3(c.mesh.position)}");
                        sb.Append($",\"rotation\":{Quat(c.mesh.rotation)}");
                        if (sc.meshData != null)
                        {
                            sb.Append(",\"vertices\":[");
                            for (int v = 0; v < sc.meshData.vertices.Length; v++)
                            {
                                if (v > 0) sb.Append(',');
                                sb.Append(Vec3(sc.meshData.vertices[v]));
                            }
                            sb.Append(']');
                            sb.Append(",\"indices\":[");
                            for (int idx = 0; idx < sc.meshData.indices.Length; idx++)
                            {
                                if (idx > 0) sb.Append(',');
                                sb.Append(sc.meshData.indices[idx]);
                            }
                            sb.Append(']');
                        }
                        break;
                }

                sb.Append('}');
                if (i < list.Count - 1) sb.Append(',');
                sb.AppendLine();
            }
            sb.Append(']');
            return sb.ToString();
        }

        static string Vec3(FPVector3 v)
            => $"[{v.x.ToFloat()},{v.y.ToFloat()},{v.z.ToFloat()}]";

        static string Quat(FPQuaternion q)
            => $"[{q.x.ToFloat()},{q.y.ToFloat()},{q.z.ToFloat()},{q.w.ToFloat()}]";
    }
}
