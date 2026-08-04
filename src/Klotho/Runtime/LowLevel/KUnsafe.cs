using System.Runtime.CompilerServices;

namespace xpTURN.Klotho
{
    /// <summary>
    /// Internal low-level reinterpret/size helpers that replace the bundled
    /// <c>System.Runtime.CompilerServices.Unsafe.dll</c>. The bundled assembly was dropped
    /// because its identity collided with copies injected by the Unity AI Assistant package
    /// (editor <c>CS0103</c>) and by Burst in IL2CPP player builds (link-time duplicate).
    ///
    /// On Godot/.NET8 the methods delegate to the BCL <c>Unsafe</c> intrinsics (built-in, no DLL).
    /// On Unity they use pointer ops: this is safe because Unity's scripting GC (Boehm) is
    /// non-moving, so an interior pointer obtained via <c>fixed</c> does not dangle while the
    /// backing storage is rooted. (On a moving GC the BCL path is used instead.)
    /// </summary>
    internal static class KUnsafe
    {
        /// <summary>Size of an unmanaged type in bytes — equivalent to <c>Unsafe.SizeOf&lt;T&gt;()</c>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe int SizeOf<T>() where T : unmanaged
        {
#if NET8_0_OR_GREATER
            return Unsafe.SizeOf<T>();
#else
            // (T*)null + 1 lowers to the `sizeof !!T` opcode — no localloc, no memory access.
            return (int)(byte*)((T*)null + 1);
#endif
        }

        /// <summary>Reinterpret a managed reference as a reference to another unmanaged type.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe ref TTo As<TFrom, TTo>(ref TFrom source)
            where TFrom : unmanaged
            where TTo : unmanaged
        {
#if NET8_0_OR_GREATER
            return ref Unsafe.As<TFrom, TTo>(ref source);
#else
            fixed (TFrom* p = &source)
                return ref *(TTo*)p;
#endif
        }

        /// <summary>Reinterpret a readonly reference as a mutable reference (readonly strip).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe ref T AsRef<T>(in T source) where T : unmanaged
        {
#if NET8_0_OR_GREATER
            return ref Unsafe.AsRef(in source);
#else
            fixed (T* p = &source)
                return ref *p;
#endif
        }
    }
}
