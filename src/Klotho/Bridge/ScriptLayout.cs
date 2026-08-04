using System;
using Friflo.Engine.ECS;

// ReSharper disable once CheckNamespace
namespace Klotho.Friflo;

/// <summary>
/// Captures, restores and hashes the deterministic fields of a <see cref="Script"/>.
/// </summary>
/// <remarks>
/// A script is a class, so unlike a component it cannot be bulk-copied: the snapshot keeps the
/// instance and moves its field values in and out. Which fields count as state, and how each is
/// reduced to a stable scalar, is decided once per script type by the same layout plan used for
/// components — including the rule that an <see cref="Entity"/> contributes only its
/// <c>(Id, Revision)</c> and is re-bound to the local store on restore.
///
/// Scripts hold behaviour as well as data. Only fields are captured; anything a script derives at
/// run time from non-field sources is outside the snapshot, exactly as for components.
/// </remarks>
internal static class ScriptLayout
{
    /// <summary>Write the deterministic field values of <paramref name="script"/> into <paramref name="leaves"/>.</summary>
    internal static void Capture(Script script, ref ulong[] leaves)
    {
        var plan  = DeterministicPlan.Get(script.GetType());
        int count = plan.Leaves.Length;
        if (leaves == null || leaves.Length < count) {
            leaves = new ulong[Math.Max(count, 4)];
        }
        plan.WriteObject(script, leaves, 0);
    }

    /// <summary>Write <paramref name="leaves"/> back into <paramref name="script"/>.</summary>
    internal static void Restore(Script script, ulong[] leaves, EntityStore store)
    {
        var    plan  = DeterministicPlan.Get(script.GetType());
        object boxed = script;   // a class: boxing is a reference copy, so fields land on the instance
        plan.ReadObject(ref boxed, leaves, 0, store);
    }

    /// <summary>Deterministic hash of the field values of <paramref name="script"/>.</summary>
    internal static ulong Hash(Script script) => DeterministicPlan.Get(script.GetType()).HashObject(script);
}
