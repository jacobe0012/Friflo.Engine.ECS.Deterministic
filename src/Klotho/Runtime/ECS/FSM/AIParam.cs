using System;
using System.Diagnostics;

namespace xpTURN.Klotho.ECS.FSM
{
    /// <summary>
    /// Non-generic probe over an <see cref="AIParam{T}"/> for build-time validation. Lets
    /// <c>HFSMBuilder</c> inspect assignment / source of a closed-generic param without knowing
    /// <c>T</c>. Explicitly implemented so it never appears on the public surface, and so the
    /// per-tick <c>Resolve</c> path is unaffected.
    /// </summary>
    internal interface IAIParamProbe
    {
        /// <summary>False on <c>default(AIParam&lt;T&gt;)</c> (never wired via Const/From).</summary>
        bool IsAssigned { get; }
        /// <summary>The <see cref="AIFunction{T}"/> source, or null for a constant/unassigned param.</summary>
        object SourceOrNull { get; }
    }

    /// <summary>
    /// A value that resolves either to a build-time constant or to a runtime source
    /// (<see cref="AIFunction{T}"/>: config asset / ECS component / computed). Lets a
    /// Decision/Action field switch its value source without touching the Decision logic.
    /// GC-free: the source is a build-time singleton, so Resolve is only a null check plus a
    /// virtual call. Generic, so value types resolve without boxing.
    /// </summary>
    public readonly struct AIParam<T> : IAIParamProbe
    {
        readonly T _const;
        readonly AIFunction<T> _source;   // null (+ _set) => use _const (Manual/Constants)
        readonly bool _set;               // false on default(struct) => unassigned guard

        AIParam(T constant, AIFunction<T> source)
        {
            _const  = constant;
            _source = source;
            _set    = true;
        }

        /// <summary>Constant value fixed at build time (Manual/Constants source).</summary>
        public static AIParam<T> Const(T value) => new AIParam<T>(value, null);

        /// <summary>Runtime source (config asset / ECS component / computed).</summary>
        public static AIParam<T> From(AIFunction<T> source)
            => new AIParam<T>(default, source ?? throw new ArgumentNullException(nameof(source)));

        /// <summary>Resolves the value. Allocation-free on every path.</summary>
        public T Resolve(ref AIContext context)
        {
            Debug.Assert(_set, "AIParam resolved before assignment (default struct). Use AIParam.Const/From.");
            return _source == null ? _const : _source.Resolve(ref context);
        }

        // Build-time validation probe (explicit impl — not on the public surface, Resolve unaffected).
        bool IAIParamProbe.IsAssigned => _set;
        object IAIParamProbe.SourceOrNull => _source;
    }

    /// <summary>
    /// A computed value source for <see cref="AIParam{T}"/>. May hold sub-<see cref="AIParam{T}"/>
    /// fields for chaining. Reads only from the context (Frame/Entity/services) and
    /// constructor-captured config.
    /// MUST be deterministic (FP64 only; no float/DateTime/random) and MUST NOT carry mutable
    /// state across Resolve calls: the instance is a singleton shared by every entity using the
    /// graph and is not rollback-tracked, so persisted state desyncs.
    /// </summary>
    public abstract class AIFunction<T>
    {
        public abstract T Resolve(ref AIContext context);
    }

    /// <summary>Non-generic entry points so the type argument is inferred: <c>AIParam.Const(fp)</c>.</summary>
    public static class AIParam
    {
        public static AIParam<T> Const<T>(T value)            => AIParam<T>.Const(value);
        public static AIParam<T> From<T>(AIFunction<T> source) => AIParam<T>.From(source);
    }
}
