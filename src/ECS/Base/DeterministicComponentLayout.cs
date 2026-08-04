// Copyright (c) Ullrich Praetz - https://github.com/friflo. All rights reserved.
// See LICENSE file in the project root for full license information.

// Additive file supporting the deterministic rollback bridge (Klotho.Friflo.Bridge).
// Describes how to reduce a component value to a platform-stable sequence of "leaves" so it can
// be hashed and serialized reproducibly.
//
// Most components are unmanaged and take the fast path: their raw bytes ARE their state, so they
// are hashed and serialized by bulk byte access (see StructHeap<T> in DeterministicAccess.cs).
//
// The exception that matters is a component holding an Entity - which every ILinkRelation must,
// since Entity is its relation key. Entity is 16 bytes: an EntityStore reference, then Id and
// Revision. The reference is a heap address that differs per process and per peer, so hashing
// those bytes would report a desync between two peers in identical states. Only (Id, Revision)
// is meaningful, and on restore the reference must be re-bound to the local store.
//
// This file builds, once per component type, a plan that walks the type's fields and emits one
// leaf per deterministic scalar - contributing (Id, Revision) for an Entity and skipping its
// store reference. Components holding any other reference type are rejected: their state cannot
// be reproduced on another peer.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

// ReSharper disable once CheckNamespace
namespace Friflo.Engine.ECS;

/// <summary>Writes the deterministic leaves of <paramref name="value"/> into <paramref name="dst"/>.</summary>
internal delegate void WriteLeaves<T>(in T value, ulong[] dst, int offset);

/// <summary>Restores <paramref name="value"/> from leaves, re-binding entities to <paramref name="store"/>.</summary>
internal delegate void ReadLeaves<T>(ref T value, ulong[] src, int offset, EntityStore store);

/// <summary>
/// Per-component-type plan describing how to hash and serialize values of <typeparamref name="T"/>
/// in a way that is reproducible across processes and machines.
/// </summary>
/// <remarks>
/// Built once per type on first use. Uses expression trees, consistent with the reflection already
/// used to construct component indexes and relations (see <c>ComponentIndexUtils</c>).
/// </remarks>
[RequiresDynamicCode("Builds a per-component hashing plan with expression trees.")]
internal static class DeterministicLayout<T> where T : struct
{
    /// <summary>True when <typeparamref name="T"/> holds references and needs the leaf path.</summary>
    internal static readonly bool HasReferences =
        System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences<T>();

    /// <summary>Number of deterministic scalars a value of <typeparamref name="T"/> reduces to.</summary>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> holds a non-entity reference.</exception>
    internal static int LeafCount {
        get {
            AssertSupported();
            return leafCount;
        }
    }

    private static readonly int leafCount;

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

    static DeterministicLayout()
    {
        if (!HasReferences) {
            return;     // fast path: raw bytes are used instead, no plan needed
        }
        List<DeterministicLeaf> plan;
        try {
            plan = DeterministicLayoutBuilder.BuildPlan(typeof(T));
        }
        catch (NotSupportedException exception) {
            unsupported = exception;
            return;
        }
        leafCount = plan.Count;
        hash      = DeterministicLayoutBuilder.CompileHash<T>(plan);
        write     = DeterministicLayoutBuilder.CompileWrite<T>(plan);
        read      = DeterministicLayoutBuilder.CompileRead<T>(plan);
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
        return hash(value);
    }

    /// <summary>Write the leaves of <paramref name="value"/>.</summary>
    internal static void Write(in T value, ulong[] dst, int offset)
    {
        AssertSupported();
        write(value, dst, offset);
    }

    /// <summary>Restore <paramref name="value"/>, binding entities to <paramref name="store"/>.</summary>
    internal static void Read(ref T value, ulong[] src, int offset, EntityStore store)
    {
        AssertSupported();
        read(ref value, src, offset, store);
    }
}

/// <summary>A single deterministic scalar reached by a chain of fields from the component root.</summary>
internal sealed class DeterministicLeaf
{
    /// <summary>Field chain from the component root to the field holding this leaf.</summary>
    internal FieldInfo[] Path;

    /// <summary>The leaf's own type: a primitive/enum, or <see cref="Entity"/> for an entity reference.</summary>
    internal Type Type;

    /// <summary>For an <see cref="Entity"/> leaf: false for its id, true for its revision.</summary>
    internal bool IsRevision;
}

/// <summary>Builds and compiles <see cref="DeterministicLayout{T}"/> plans.</summary>
[RequiresDynamicCode("Builds per-component hashing plans with expression trees.")]
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
        typeof(DeterministicLayoutBuilder).GetMethod(nameof(Mix), BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

    // ------------------------------------------------------------------ plan

    /// <summary>Enumerate the deterministic leaves of <paramref name="type"/> in declaration order.</summary>
    [UnconditionalSuppressMessage("TrimAnalysis", "IL2070", Justification = "Component types are rooted by the schema.")]
    internal static List<DeterministicLeaf> BuildPlan(Type type)
    {
        var leaves = new List<DeterministicLeaf>();
        Walk(type, new List<FieldInfo>(), leaves, type);
        return leaves;
    }

    [UnconditionalSuppressMessage("TrimAnalysis", "IL2070", Justification = "Component types are rooted by the schema.")]
    private static void Walk(Type type, List<FieldInfo> path, List<DeterministicLeaf> leaves, Type rootType)
    {
        // Declaration order is stable for a given build, and both writer and reader use this same
        // plan, so leaf order only has to be self-consistent.
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
                $"Component '{rootType.Name}' has field '{field.Name}' of reference type '{fieldType.Name}', " +
                 "so its state cannot be reproduced on another peer. Simulation components must contain only " +
                 "value types (an Entity reference is allowed).");
        }
    }

    // ------------------------------------------------------------------ compile

    /// <summary>Compile a hash function mixing every leaf of <typeparamref name="T"/>.</summary>
    internal static Func<T, ulong> CompileHash<T>(List<DeterministicLeaf> leaves)
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
    internal static WriteLeaves<T> CompileWrite<T>(List<DeterministicLeaf> leaves)
    {
        var value  = Expression.Parameter(typeof(T).MakeByRefType(), "value");
        var dst    = Expression.Parameter(typeof(ulong[]), "dst");
        var offset = Expression.Parameter(typeof(int), "offset");

        var body = new List<Expression>();
        for (int i = 0; i < leaves.Count; i++) {
            var slot = Expression.ArrayAccess(dst, Expression.Add(offset, Expression.Constant(i)));
            body.Add(Expression.Assign(slot, LeafAsUInt64(value, leaves[i])));
        }
        if (body.Count == 0) {
            body.Add(Expression.Empty());
        }
        return Expression.Lambda<WriteLeaves<T>>(Expression.Block(body), value, dst, offset).Compile();
    }

    /// <summary>Compile a reader restoring each leaf, re-binding entities to the target store.</summary>
    internal static ReadLeaves<T> CompileRead<T>(List<DeterministicLeaf> leaves)
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
        for (int i = 0; i < leaves.Count; i++) {
            var leaf = leaves[i];
            var slot = Expression.ArrayIndex(src, Expression.Add(offset, Expression.Constant(i)));
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

    /// <summary>Expression navigating from the component root to the field holding a leaf.</summary>
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
