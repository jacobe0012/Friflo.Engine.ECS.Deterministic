using System.Collections.Generic;
using System.Text;

namespace xpTURN.Klotho.Serialization
{
    /// <summary>
    /// Runtime size helpers for generated GetSerializedSize() of variable-element collections
    /// that cannot be expressed as a fixed Count*ElementSize (e.g. List&lt;string&gt;). GC-free (no LINQ).
    /// </summary>
    public static class SerializationSize
    {
        /// <summary>
        /// Serialized byte size of a List&lt;string&gt; field: 4-byte count prefix +
        /// per-element (4-byte length prefix + UTF8 byte count). Mirrors
        /// SpanWriter.WriteInt32(Count) followed by per-element WriteString.
        /// </summary>
        public static int StringList(List<string> list)
        {
            int size = 4; // count prefix
            if (list == null) return size;
            for (int i = 0; i < list.Count; i++)
                size += 4 + Encoding.UTF8.GetByteCount(list[i] ?? string.Empty);
            return size;
        }

        /// <summary>
        /// Serialized byte size of a string[] field: 4-byte count prefix +
        /// per-element (4-byte length prefix + UTF8 byte count). Mirrors
        /// SpanWriter.WriteInt32(Length) followed by per-element WriteString.
        /// </summary>
        public static int StringArray(string[] arr)
        {
            int size = 4; // count prefix
            if (arr == null) return size;
            for (int i = 0; i < arr.Length; i++)
                size += 4 + Encoding.UTF8.GetByteCount(arr[i] ?? string.Empty);
            return size;
        }
    }
}
