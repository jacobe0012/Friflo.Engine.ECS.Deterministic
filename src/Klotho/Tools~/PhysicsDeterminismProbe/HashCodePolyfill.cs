#if NETFRAMEWORK
// Minimal System.HashCode polyfill so the Math sources compile under net472 (Unity Mono).
// Only referenced from GetHashCode() — never exercised by the determinism probe, so the
// exact mixing does not matter; this just needs to compile.
namespace System
{
    internal struct HashCode
    {
        private int _acc;
        public void Add<T>(T value) { unchecked { _acc = _acc * 31 + (value?.GetHashCode() ?? 0); } }
        public int ToHashCode() => _acc;

        public static int Combine<T1>(T1 a) => Mix(a);
        public static int Combine<T1, T2>(T1 a, T2 b) => Mix(a, b);
        public static int Combine<T1, T2, T3>(T1 a, T2 b, T3 c) => Mix(a, b, c);
        public static int Combine<T1, T2, T3, T4>(T1 a, T2 b, T3 c, T4 d) => Mix(a, b, c, d);
        public static int Combine<T1, T2, T3, T4, T5>(T1 a, T2 b, T3 c, T4 d, T5 e) => Mix(a, b, c, d, e);

        private static int Mix(params object[] values)
        {
            unchecked
            {
                int h = 17;
                foreach (var v in values) h = h * 31 + (v?.GetHashCode() ?? 0);
                return h;
            }
        }
    }
}
#endif
