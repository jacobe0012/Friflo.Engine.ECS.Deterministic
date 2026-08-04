// Copyright (c) Ullrich Praetz - https://github.com/friflo. All rights reserved.
// See LICENSE file in the project root for full license information.

// Additive file supporting the deterministic rollback bridge (Klotho.Friflo.Bridge).
// Describes how to reduce a value to a platform-stable sequence of "leaves" so it can be hashed
// and serialized reproducibly.
//
// Most components are unmanaged and take the fast path: their raw bytes ARE their state, so they
// are hashed and serialized by bulk byte access (see StructHeap<T> in DeterministicAccess.cs).
//
// Two shapes need the leaf path instead:
//   - a component holding an Entity - which every ILinkRelation must, since Entity is its
//     relation key. Entity is 16 bytes: an EntityStore reference, then Id and Revision. The
//     reference is a heap address that differs per process and per peer, so hashing those bytes
//     would report a desync between two peers in identical states. Only (Id, Revision) is
//     meaningful, and on restore the reference must be re-bound to the local store.
//   - a Script, which is a class: its fields are the entity state a rollback has to restore.
//
// Execution has two paths. Where the runtime can emit code, the plan is compiled to delegates.
// Under NativeAOT (RuntimeFeature.IsDynamicCodeSupported == false) it is interpreted through
// reflection instead - slower, but it keeps relations and scripts working in AOT builds rather
// than throwing at the first hash.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

// ReSharper disable once CheckNamespace
namespace Friflo.Engine.ECS;

/// <summary>Writes the deterministic leaves of <paramref name="value"/> into <paramref name="dst"/>.</summary>
internal delegate void WriteLeaves<T>(in T value, ulong[] dst, int offset);

/// <summary>Restores <paramref name="value"/> from leaves, re-binding entities to <paramref name="store"/>.</summary>
internal delegate void ReadLeaves<T>(ref T value, ulong[] src, int offset, EntityStore store);

/// <summary>A single deterministic scalar reached by a chain of fields from the value root.</summary>
internal sealed class DeterministicLeaf
{
    /// <summary>Field chain from the root to the field holding this leaf. The last entry holds the value.</summary>
    internal FieldInfo[] Path;

    /// <summary>The leaf's own type: a primitive/enum, or <see cref="Entity"/> for an entity reference.</summary>
    internal Type Type;

    /// <summary>For an <see cref="Entity"/> leaf: false for its id, true for its revision.</summary>
    internal bool IsRevision;
}

/// <summary>
/// The deterministic leaves of one type, plus a reflection-based interpreter for them.
/// </summary>
/// <remarks>
/// The interpreter is the NativeAOT path and the path used for <see cref="Script"/>s. It boxes
/// intermediate values, so it is slower than the compiled path; it runs only for types that
/// actually hold an <see cref="Entity"/> or are scripts, never for plain unmanaged components.
/// </remarks>
internal sealed class DeterministicPlan
{
    private const ulong FnvOffset = 14695981039346656037;
    private const ulong FnvPrime  =        1099511628211;

    internal readonly DeterministicLeaf[] Leaves;

    /// <summary>Scratch for <see cref="SetNested"/>. The simulation is single-threaded by contract.</summary>
    private readonly object[] owners;

    private DeterministicPlan(DeterministicLeaf[] leaves)
    {
        Leaves = leaves;
        int depth = 1;
        foreach (var leaf in leaves) {
            if (leaf.Path.Length > depth) depth = leaf.Path.Length;
        }
        owners = new object[depth];
    }

    // ------------------------------------------------------------------ build

    private static readonly Dictionary<Type, DeterministicPlan> Cache = new();

    /// <summary>Build (or reuse) the plan for <paramref name="type"/>.</summary>
    /// <exception cref="NotSupportedException">The type holds a non-entity reference.</exception>
    internal static DeterministicPlan Get(Type type)
    {
        lock (Cache) {
            if (Cache.TryGetValue(type, out var cached)) {
                return cached;
            }
            var leaves = new List<DeterministicLeaf>();
            Walk(type, new List<FieldInfo>(), leaves, type);
            return Cache[type] = new DeterministicPlan(leaves.ToArray());
        }
    }

    [UnconditionalSuppressMessage("TrimAnalysis", "IL2070",
        Justification = "Component and script types are rooted by the entity schema, which registers them explicitly.")]
    private static void Walk(Type type, List<FieldInfo> path, List<DeterministicLeaf> leaves, Type rootType)
    {
        // Declaration order is stable for a given build, and writer and reader share this plan,
        // so leaf order only has to be self-consistent.
        var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (var field in fields) {
            var fieldType = field.FieldType;

            if (fieldType == typeof(Entity)) {
                // Contribute identity only; the store reference is process-local.
                path.Add(field);
                leaves.Add(new DeterministicLeaf { Path = path.ToArray(), Type = typeof(Entity), IsRevision = false });
                leaves.Add(new DeterministicLeaf { Path = path.ToArray(), Type = typeof(Entity), IsRevision = true  });
                path.RemoveAt(path.Count - 1);
                continue;
            }
            if (fieldType.IsPrimitive || fieldType.IsEnum) {
                path.Add(field);
                leaves.Add(new DeterministicLeaf { Path = path.ToArray(), Type = fieldType });
                path.RemoveAt(path.Count - 1);
                continue;
            }
            if (fieldType.IsValueType) {
                path.Add(field);
                Walk(fieldType, path, leaves, rootType);
                path.RemoveAt(path.Count - 1);
                continue;
            }
            throw new NotSupportedException(
                $"'{rootType.Name}' has field '{field.Name}' of reference type '{fieldType.Name}', " +
                 "so its state cannot be reproduced on another peer. Simulation components and scripts " +
                 "must contain only value types (an Entity reference is allowed).");
        }
    }

    // ------------------------------------------------------------------ interpret

    /// <summary>Hash <paramref name="root"/> by mixing every leaf.</summary>
    internal ulong HashObject(object root)
    {
        ulong hash = FnvOffset;
        foreach (var leaf in Leaves) {
            hash ^= ReadLeaf(root, leaf);
            hash *= FnvPrime;
        }
        return hash;
    }

    /// <summary>Store each leaf of <paramref name="root"/> as a <c>ulong</c>.</summary>
    internal void WriteObject(object root, ulong[] dst, int offset)
    {
        for (int i = 0; i < Leaves.Length; i++) {
            dst[offset + i] = ReadLeaf(root, Leaves[i]);
        }
    }

    /// <summary>Restore each leaf into <paramref name="root"/>, binding entities to <paramref name="store"/>.</summary>
    internal void ReadObject(ref object root, ulong[] src, int offset, EntityStore store)
    {
        for (int i = 0; i < Leaves.Length; i++) {
            var leaf = Leaves[i];
            if (leaf.Type == typeof(Entity)) {
                if (leaf.IsRevision) {
                    continue;   // consumed together with the id leaf
                }
                var entity = new Entity(store, (int)src[offset + i], (short)src[offset + i + 1]);
                SetNested(ref root, leaf.Path, entity);
                continue;
            }
            SetNested(ref root, leaf.Path, FromUInt64(src[offset + i], leaf.Type));
        }
    }

    private static ulong ReadLeaf(object root, DeterministicLeaf leaf)
    {
        object value = root;
        foreach (var field in leaf.Path) {
            value = field.GetValue(value);
        }
        if (leaf.Type == typeof(Entity)) {
            var entity = (Entity)value;
            return leaf.IsRevision ? (ulong)(uint)entity.Revision : (ulong)(uint)entity.Id;
        }
        return ToUInt64(value, leaf.Type);
    }

    /// <summary>
    /// Assign <paramref name="leafValue"/> through a field chain, writing intermediate structs
    /// back up the chain (boxing a struct field yields a copy, so the copy has to be re-assigned).
    /// </summary>
    private void SetNested(ref object root, FieldInfo[] path, object leafValue)
    {
        int depth = path.Length;
        if (depth == 1) {
            path[0].SetValue(root, leafValue);
            return;
        }
        owners[0] = root;
        for (int i = 0; i < depth - 1; i++) {
            owners[i + 1] = path[i].GetValue(owners[i]);
        }
        path[depth - 1].SetValue(owners[depth - 1], leafValue);
        for (int i = depth - 2; i >= 0; i--) {
            path[i].SetValue(owners[i], owners[i + 1]);
        }
        root = owners[0];
    }

    // ------------------------------------------------------------------ scalar conversion

    /// <summary>Reinterpret a scalar as <c>ulong</c>, preserving its exact bit pattern.</summary>
    private static ulong ToUInt64(object value, Type type)
    {
        if (type.IsEnum) {
            type  = Enum.GetUnderlyingType(type);
            value = Convert.ChangeType(value, type);
        }
        return value switch {
            bool   v => v ? 1UL : 0UL,
            // Float bits rather than value: two NaNs compare unequal but must hash identically.
            float  v => (ulong)(uint)BitConverter.SingleToInt32Bits(v),
            double v => (ulong)BitConverter.DoubleToInt64Bits(v),
            sbyte  v => unchecked((ulong)(long)v),
            short  v => unchecked((ulong)(long)v),
            int    v => unchecked((ulong)(long)v),
            long   v => unchecked((ulong)v),
            byte   v => v,
            ushort v => v,
            uint   v => v,
            ulong  v => v,
            char   v => v,
            _        => throw new NotSupportedException($"cannot hash scalar of type {type.Name}"),
        };
    }

    /// <summary>Inverse of <see cref="ToUInt64"/>.</summary>
    private static object FromUInt64(ulong raw, Type type)
    {
        if (type.IsEnum) {
            return Enum.ToObject(type, FromUInt64(raw, Enum.GetUnderlyingType(type)));
        }
        if (type == typeof(bool))   return raw != 0;
        if (type == typeof(float))  return BitConverter.Int32BitsToSingle((int)(uint)raw);
        if (type == typeof(double)) return BitConverter.Int64BitsToDouble((long)raw);
        if (type == typeof(sbyte))  return (sbyte)(long)raw;
        if (type == typeof(short))  return (short)(long)raw;
        if (type == typeof(int))    return (int)(long)raw;
        if (type == typeof(long))   return (long)raw;
        if (type == typeof(byte))   return (byte)raw;
        if (type == typeof(ushort)) return (ushort)raw;
        if (type == typeof(uint))   return (uint)raw;
        if (type == typeof(ulong))  return raw;
        if (type == typeof(char))   return (char)raw;
        throw new NotSupportedException($"cannot restore scalar of type {type.Name}");
    }
}

/// <summary>
/// Per-type plan describing how to hash and serialize values of <typeparamref name="T"/> in a way
/// that is reproducible across processes and machines.
/// </summary>
/// <remarks>
/// Built once per type on first use. Uses expression trees where the runtime can emit code, and
/// falls back to reflection under NativeAOT — consistent with the reflection Friflo already uses
/// to construct component indexes and relations (see <c>ComponentIndexUtils</c>).
/// </remarks>
internal static class DeterministicLayout<T> where T : struct
{
    /// <summary>True when <typeparamref name="T"/> holds references and needs the leaf path.</summary>
    internal static readonly bool HasReferences = RuntimeHelpers.IsReferenceOrContainsReferences<T>();

    /// <summary>Number of deterministic scalars a value of <typeparamref name="T"/> reduces to.</summary>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> holds a non-entity reference.</exception>
    internal static int LeafCount {
        get {
            AssertSupported();
            return leafCount;
        }
    }

    private static readonly int             leafCount;
    private static readonly DeterministicPlan plan;

    // Compiled fast path. Null under NativeAOT, where `plan` is interpreted instead.
    private static readonly Func<T, ulong>  hash;
    private static readonly WriteLeaves<T>  write;
    private static readonly ReadLeaves<T>   read;

    /// <summary>
    /// Why <typeparamref name="T"/> cannot be used in a deterministic simulation, or null when it can.
    /// </summary>
    /// <remarks>
    /// The failure is recorded rather than thrown from the static constructor: a throwing cctor
    /// surfaces as <see cref="TypeInitializationException"/> with the real explanation buried in
    /// an inner exception, and every later use of the type reports the same wrapper. Storing it
    /// lets each entry point throw the actionable message directly.
    /// </remarks>
    private static readonly NotSupportedException unsupported;

    [UnconditionalSuppressMessage("AotAnalysis", "IL3050",
        Justification = "Expression trees are only compiled when RuntimeFeature.IsDynamicCodeSupported; " +
                        "otherwise the plan is interpreted through reflection.")]
    static DeterministicLayout()
    {
        if (!HasReferences) {
            return;     // fast path: raw bytes are used instead, no plan needed
        }
        try {
            plan = DeterministicPlan.Get(typeof(T));
        }
        catch (NotSupportedException exception) {
            unsupported = exception;
            return;
        }
        leafCount = plan.Leaves.Length;

        if (RuntimeFeature.IsDynamicCodeSupported) {
            hash  = DeterministicLayoutBuilder.CompileHash<T>(plan.Leaves);
            write = DeterministicLayoutBuilder.CompileWrite<T>(plan.Leaves);
            read  = DeterministicLayoutBuilder.CompileRead<T>(plan.Leaves);
        }
    }

    /// <exception cref="NotSupportedException"><typeparamref name="T"/> holds a non-entity reference.</exception>
    private static void AssertSupported()
    {
        if (unsupported != null) {
            throw new NotSupportedException(unsupported.Message, unsupported);
        }
    }

    /// <summary>Deterministic hash of <paramref name="value"/>.</summary>
    internal static ulong Hash(in T value)
    {
        AssertSupported();
        return hash != null ? hash(value) : plan.HashObject(value);
    }

    /// <summary>Write the leaves of <paramref name="value"/>.</summary>
    internal static void Write(in T value, ulong[] dst, int offset)
    {
        AssertSupported();
        if (write != null) {
            write(value, dst, offset);
        } else {
            plan.WriteObject(value, dst, offset);
        }
    }

    /// <summary>Restore <paramref name="value"/>, binding entities to <paramref name="store"/>.</summary>
    internal static void Read(ref T value, ulong[] src, int offset, EntityStore store)
    {
        AssertSupported();
        if (read != null) {
            read(ref value, src, offset, store);
            return;
        }
        object boxed = value;
        plan.ReadObject(ref boxed, src, offset, store);
        value = (T)boxed;
    }
}

/// <summary>Compiles <see cref="DeterministicPlan"/> leaves into delegates. Requires dynamic code.</summary>
internal static class DeterministicLayoutBuilder
{
    private const ulong FnvOffset = 14695981039346656037;
    private const ulong FnvPrime  =        1099511628211;

    /// <summary>Mix one leaf into a running FNV-1a hash.</summary>
    internal static ulong Mix(ulong hash, ulong value)
    {
        hash ^= value;
        hash *= FnvPrime;
        return hash;
    }

    private static readonly MethodInfo MixMethod =
        typeof(DeterministicLayoutBuilder).GetMethod(nameof(Mix), BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException($"{nameof(Mix)} not found");

    /// <summary>Compile a hash function mixing every leaf of <typeparamref name="T"/>.</summary>
    [RequiresDynamicCode("Compiles an expression tree.")]
    internal static Func<T, ulong> CompileHash<T>(DeterministicLeaf[] leaves)
    {
        var value = Expression.Parameter(typeof(T), "value");
        var hash  = Expression.Variable(typeof(ulong), "hash");

        var body = new List<Expression> { Expression.Assign(hash, Expression.Constant(FnvOffset)) };
        foreach (var leaf in leaves) {
            body.Add(Expression.Assign(hash, Expression.Call(MixMethod, hash, LeafAsUInt64(value, leaf))));
        }
        body.Add(hash);
        return Expression.Lambda<Func<T, ulong>>(Expression.Block(new[] { hash }, body), value).Compile();
    }

    /// <summary>Compile a writer storing each leaf of <typeparamref name="T"/> as a <c>ulong</c>.</summary>
    [RequiresDynamicCode("Compiles an expression tree.")]
    internal static WriteLeaves<T> CompileWrite<T>(DeterministicLeaf[] leaves)
    {
        var value  = Expression.Parameter(typeof(T).MakeByRefType(), "value");
        var dst    = Expression.Parameter(typeof(ulong[]), "dst");
        var offset = Expression.Parameter(typeof(int), "offset");

        var body = new List<Expression>();
        for (int i = 0; i < leaves.Length; i++) {
            var slot = Expression.ArrayAccess(dst, Expression.Add(offset, Expression.Constant(i)));
            body.Add(Expression.Assign(slot, LeafAsUInt64(value, leaves[i])));
        }
        if (body.Count == 0) {
            body.Add(Expression.Empty());
        }
        return Expression.Lambda<WriteLeaves<T>>(Expression.Block(body), value, dst, offset).Compile();
    }

    /// <summary>Compile a reader restoring each leaf, re-binding entities to the target store.</summary>
    [RequiresDynamicCode("Compiles an expression tree.")]
    internal static ReadLeaves<T> CompileRead<T>(DeterministicLeaf[] leaves)
    {
        var value  = Expression.Parameter(typeof(T).MakeByRefType(), "value");
        var src    = Expression.Parameter(typeof(ulong[]), "src");
        var offset = Expression.Parameter(typeof(int), "offset");
        var store  = Expression.Parameter(typeof(EntityStore), "store");

        var entityCtor = typeof(Entity).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new[] { typeof(EntityStore), typeof(int), typeof(short) },
            null);

        var body = new List<Expression>();
        for (int i = 0; i < leaves.Length; i++) {
            var leaf   = leaves[i];
            var slot   = Expression.ArrayIndex(src, Expression.Add(offset, Expression.Constant(i)));
            var target = Navigate(value, leaf.Path);

            if (leaf.Type == typeof(Entity)) {
                if (leaf.IsRevision) {
                    continue;   // consumed together with the id leaf below
                }
                // The id leaf is followed by the revision leaf; rebuild the entity from both,
                // bound to the store being restored into.
                var revSlot  = Expression.ArrayIndex(src, Expression.Add(offset, Expression.Constant(i + 1)));
                var id       = Expression.Convert(slot,    typeof(int));
                var revision = Expression.Convert(revSlot, typeof(short));
                body.Add(Expression.Assign(target, Expression.New(entityCtor!, store, id, revision)));
                continue;
            }
            body.Add(Expression.Assign(target, FromUInt64(slot, leaf.Type)));
        }
        if (body.Count == 0) {
            body.Add(Expression.Empty());
        }
        return Expression.Lambda<ReadLeaves<T>>(Expression.Block(body), value, src, offset, store).Compile();
    }

    // ------------------------------------------------------------------ leaf conversion

    /// <summary>Expression navigating from the root to the field holding a leaf.</summary>
    private static Expression Navigate(Expression root, FieldInfo[] path)
    {
        var expr = root;
        foreach (var field in path) {
            expr = Expression.Field(expr, field);
        }
        return expr;
    }

    /// <summary>Read a leaf and widen it to <c>ulong</c> without losing bits.</summary>
    private static Expression LeafAsUInt64(Expression root, DeterministicLeaf leaf)
    {
        var field = Navigate(root, leaf.Path);

        if (leaf.Type == typeof(Entity)) {
            var member = leaf.IsRevision ? nameof(Entity.Revision) : nameof(Entity.Id);
            return ToUInt64(Expression.Field(field, member));
        }
        return ToUInt64(field);
    }

    /// <summary>Reinterpret a scalar as <c>ulong</c>, preserving its exact bit pattern.</summary>
    private static Expression ToUInt64(Expression scalar)
    {
        var type = scalar.Type;
        if (type.IsEnum) {
            type   = Enum.GetUnderlyingType(type);
            scalar = Expression.Convert(scalar, type);
        }
        if (type == typeof(bool)) {
            return Expression.Condition(scalar, Expression.Constant(1UL), Expression.Constant(0UL));
        }
        // Float bits rather than value: two NaNs compare unequal but must hash identically, and
        // the simulation contract keeps floats out of state anyway.
        if (type == typeof(float)) {
            var bits = Expression.Call(typeof(BitConverter).GetMethod(nameof(BitConverter.SingleToInt32Bits))!, scalar);
            return Expression.Convert(Expression.Convert(bits, typeof(uint)), typeof(ulong));
        }
        if (type == typeof(double)) {
            var bits = Expression.Call(typeof(BitConverter).GetMethod(nameof(BitConverter.DoubleToInt64Bits))!, scalar);
            return Expression.Convert(Expression.Convert(bits, typeof(long)), typeof(ulong));
        }
        // Signed values go via long so sign extension is well defined, then reinterpret unchecked.
        if (type == typeof(sbyte) || type == typeof(short) || type == typeof(int) || type == typeof(long)) {
            return Expression.Convert(Expression.Convert(scalar, typeof(long)), typeof(ulong));
        }
        return Expression.Convert(scalar, typeof(ulong));
    }

    /// <summary>Inverse of <see cref="ToUInt64"/> for restoring a leaf.</summary>
    private static Expression FromUInt64(Expression raw, Type type)
    {
        if (type.IsEnum) {
            var underlying = Enum.GetUnderlyingType(type);
            return Expression.Convert(FromUInt64(raw, underlying), type);
        }
        if (type == typeof(bool)) {
            return Expression.NotEqual(raw, Expression.Constant(0UL));
        }
        if (type == typeof(float)) {
            var bits = Expression.Convert(Expression.Convert(raw, typeof(uint)), typeof(int));
            return Expression.Call(typeof(BitConverter).GetMethod(nameof(BitConverter.Int32BitsToSingle))!, bits);
        }
        if (type == typeof(double)) {
            var bits = Expression.Convert(raw, typeof(long));
            return Expression.Call(typeof(BitConverter).GetMethod(nameof(BitConverter.Int64BitsToDouble))!, bits);
        }
        if (type == typeof(sbyte) || type == typeof(short) || type == typeof(int) || type == typeof(long)) {
            return Expression.Convert(Expression.Convert(raw, typeof(long)), type);
        }
        return Expression.Convert(raw, type);
    }
}
