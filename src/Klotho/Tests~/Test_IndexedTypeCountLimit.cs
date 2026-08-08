using System;
using System.Collections.Generic;
using System.Reflection;
using Friflo.Engine.ECS;
using NUnit.Framework;

// ReSharper disable once CheckNamespace
namespace Klotho.Friflo.Tests;

/// <summary>
/// Records the point at which Friflo's own indexed-component bookkeeping stops working, so the
/// bridge's determinism claims are stated against a boundary that has been measured rather than
/// assumed.
/// </summary>
/// <remarks>
/// An entity records which indexed components and relations it owns in <c>EntityNode.isOwner</c>,
/// an <c>int</c>, whose bits are set from <c>indexBit</c> / <c>relationBit</c> — each computed as
/// <c>(int)ComponentTypes.bitSet.l0</c>, that is, a 256-bit set narrowed to 32 bits. Everything
/// keyed off that field therefore has a hard ceiling at struct index 31, far below the 256 the
/// bit set itself supports and below the 64 the previous round treated as the boundary:
///
/// <list type="bullet">
///   <item>index 31 sets the sign bit, so widening the <c>int</c> back to <c>long</c> sign-extends
///         and every high bit reads as set — <c>DeleteEntity</c> then walks component types that
///         are not there and throws.</item>
///   <item>index 32 and above narrow to zero, so the entity is recorded as owning nothing and
///         <c>DeleteEntity</c> skips index removal entirely — the deleted entity stays in the
///         index and later queries return it.</item>
/// </list>
///
/// <para>This is Friflo's, not the bridge's: it reproduces with no simulation involved, and fixing
/// it means widening <c>isOwner</c> / <c>isLinked</c> to a full <c>BitSet</c> across the engine's
/// delete, index and relation paths — well outside an integration whose premise is leaving the
/// engine alone. What the bridge can do is state the limit precisely and fail loudly if it moves,
/// which is what these tests are for.</para>
///
/// <para>The practical reading: a deterministic simulation may declare at most <b>32</b> indexed
/// component and relation types in total, counting every type in every assembly the schema sees —
/// test fixtures included, which is why this repository already sits past the line. Beyond it,
/// entity deletion corrupts the lookup tables, and it does so <i>without</i> changing the state
/// hash, so peers diverge through the branches their systems take rather than through anything
/// the desync detector can see.</para>
///
/// <para>These tests deliberately assert the <i>broken</i> behaviour. They are a tripwire: if a
/// future Friflo version widens the field, they fail, and the limit documented above — along with
/// the bridge's rollback rebuild, which already handles all 256 bits — should be revisited.</para>
/// </remarks>
public static class Test_IndexedTypeCountLimit
{
    /// <summary>The bit index above which <c>isOwner</c> can no longer represent a component.</summary>
    private const int LastRepresentableStructIndex = 30;

    /// <summary>An indexed component and the operations needed to exercise its index.</summary>
    private readonly struct Indexed
    {
        internal readonly int                         StructIndex;
        internal readonly string                      Name;
        internal readonly Action<Entity, int>         Add;
        internal readonly Func<EntityStore, int, int> CountWithValue;

        internal Indexed(int structIndex, string name, Action<Entity, int> add, Func<EntityStore, int, int> count)
        {
            StructIndex    = structIndex;
            Name           = name;
            Add            = add;
            CountWithValue = count;
        }
    }

    /// <summary>
    /// The indexed padding components of <see cref="Test_IndexRebuildWidth"/>, paired with the
    /// struct index the schema gave each one.
    /// </summary>
    /// <remarks>
    /// Reusing that fixture's table rather than declaring more indexed types is deliberate: every
    /// indexed type added anywhere in this assembly pushes every later one further up, so a second
    /// bank of padding would shift the very indices these tests reason about.
    /// </remarks>
    private static List<Indexed> IndexedPads()
    {
        var padsField = typeof(Test_IndexRebuildWidth).GetField("Pads", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(padsField, "Test_IndexRebuildWidth.Pads is gone - this fixture borrows it");
        var pads   = (Array)padsField.GetValue(null);
        var schema = EntityStore.GetEntitySchema();
        var result = new List<Indexed>();

        foreach (var pad in pads) {
            var padType = pad.GetType();
            var type    = (Type)                       Field(padType, "Type",           pad);
            var add     = (Action<Entity, int>)        Field(padType, "Add",            pad);
            var count   = (Func<EntityStore, int, int>)Field(padType, "CountWithValue", pad);

            foreach (var componentType in schema.Components) {
                if (componentType != null && componentType.Type == type) {
                    result.Add(new Indexed(componentType.StructIndex, type.Name, add, count));
                    break;
                }
            }
        }
        Assert.IsNotEmpty(result, "no indexed padding types were resolved");
        return result;
    }

    private static object Field(Type type, string name, object instance) =>
        type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance);

    /// <summary>Deleting an entity clears its index entry — but only below the boundary.</summary>
    [Test]
    public static void DeleteEntity_ClearsTheIndex_OnlyBelowTheBitWidthBoundary()
    {
        var below = new List<string>();
        var above = new List<string>();

        foreach (var pad in IndexedPads()) {
            var store  = new EntityStore();
            var entity = store.CreateEntity();
            pad.Add(entity, 7);
            Assert.AreEqual(1, pad.CountWithValue(store, 7),
                $"{pad.Name} was not indexed on add - this fixture assumes it was");

            bool cleared;
            try {
                entity.DeleteEntity();
                cleared = pad.CountWithValue(store, 7) == 0;
            } catch (NullReferenceException) {
                // The sign-extension case: the widened mask names component types that do not
                // exist, so the delete path dereferences a missing index.
                cleared = false;
            }
            (cleared ? below : above).Add($"{pad.Name}@{pad.StructIndex}");

            if (pad.StructIndex <= LastRepresentableStructIndex) {
                Assert.IsTrue(cleared,
                    $"{pad.Name} (struct index {pad.StructIndex}) is within the representable range " +
                     "but its index entry survived deletion");
            }
        }

        Assert.IsNotEmpty(below, "no indexed type was below the boundary - the fixture proves nothing");
        Assert.IsNotEmpty(above,
            "every indexed type cleared correctly on delete. Either the schema no longer reaches " +
            "struct index 31, or Friflo widened EntityNode.isOwner beyond 32 bits - in which case " +
            "the 32-type limit documented on this fixture is obsolete and should be re-measured.");
    }

    /// <summary>
    /// The narrowing that causes it, asserted directly on the arithmetic rather than through its
    /// symptoms.
    /// </summary>
    /// <remarks>
    /// Stated separately from the behavioural test above because it does not depend on how many
    /// indexed types this assembly happens to declare: it is a property of the field width alone,
    /// so it keeps holding the line if the padding types are ever removed.
    /// </remarks>
    [Test]
    public static void OwnershipBits_CannotRepresentStructIndicesBeyondThirtyOne()
    {
        Assert.AreEqual(4, sizeof(int), "sanity");

        for (int structIndex = 0; structIndex <= LastRepresentableStructIndex; structIndex++) {
            Assert.AreNotEqual(0, OwnershipBit(structIndex),
                $"struct index {structIndex} should still be representable in a 32-bit isOwner");
        }

        // 31 survives as a bit pattern but widens to a mask with every high bit set, so an
        // intersection against the schema's 64-bit index mask names types the entity never had.
        Assert.AreNotEqual(0, OwnershipBit(31));
        Assert.IsTrue((long)OwnershipBit(31) < 0,
            "struct index 31 no longer sign-extends - the delete path may have been fixed");

        foreach (int structIndex in new[] { 32, 63, 64, 128, 255 }) {
            Assert.AreEqual(0, OwnershipBit(structIndex),
                $"struct index {structIndex} is now representable in isOwner - Friflo widened the " +
                 "field, so the 32-type limit on this fixture should be re-measured");
        }
    }

    /// <summary>The bit an entity would record for owning the component at <paramref name="structIndex"/>.</summary>
    private static int OwnershipBit(int structIndex)
    {
        var types = new ComponentTypes();
        types.bitSet.SetBit(structIndex);
        return (int)types.bitSet.l0;   // exactly how indexBit / relationBit are derived
    }
}
