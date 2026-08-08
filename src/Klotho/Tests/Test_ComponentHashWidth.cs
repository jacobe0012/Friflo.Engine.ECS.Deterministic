using System;
using System.Runtime.InteropServices;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using Klotho.Friflo;
using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;

// ReSharper disable once CheckNamespace
namespace Klotho.Friflo.Tests;

/// <summary>
/// Guards the raw-bytes component hash, which reads a component's memory a machine word at a time
/// rather than a byte at a time.
/// </summary>
/// <remarks>
/// Hashing a component byte by byte costs about a nanosecond per byte and, measured on this
/// bridge, accounted for roughly four fifths of a server tick — the hash ran longer than the
/// simulation it was hashing. Reading whole words instead is worth doing, but it moves the hash
/// from "walks a byte sequence" to "reinterprets memory", and three things that were previously
/// impossible become possible:
/// <list type="bullet">
///   <item><b>Padding.</b> A struct whose fields do not fill its size has padding bytes between or
///         after them. The C# language does not define their contents. A byte loop over
///         <c>sizeof(T)</c> reads them just the same as a word loop does, so this risk is not new
///         — but a word loop makes it easy to introduce a tail path that treats the final partial
///         word differently from the bytes preceding it, which would make the hash depend on where
///         a value sits rather than on what it is.</item>
///   <item><b>Trailing bytes.</b> Any component whose size is not a multiple of eight has a tail.
///         If the tail were skipped, or read past, two values differing only in those bytes would
///         collide — and a collision here is a desync that no peer detects, because the hash is
///         the detector.</item>
///   <item><b>Alignment.</b> Components live in <c>T[]</c> at whatever offsets the runtime picks.
///         A word read must not assume any particular alignment.</item>
/// </list>
/// So these tests pin the property that matters and not the implementation: <b>two components hash
/// equal exactly when their bytes are equal.</b> Each covers a size class that exercises a
/// different tail length.
/// </remarks>
public static class Test_ComponentHashWidth
{
    #region components of assorted widths

    /// <summary>8 bytes: exactly one word, no tail.</summary>
    private struct W8 : IComponent { public long a; }

    /// <summary>4 bytes: shorter than a word — all tail, no whole word at all.</summary>
    private struct W4 : IComponent { public int a; }

    /// <summary>12 bytes: one whole word plus a 4-byte tail.</summary>
    private struct W12 : IComponent { public long a; public int b; }

    /// <summary>1 byte: the shortest possible component.</summary>
    private struct W1 : IComponent { public byte a; }

    /// <summary>
    /// 3 bytes of payload. Whatever the compiler chooses for its layout, the same value must hash
    /// the same way every time it is asked.
    /// </summary>
    private struct W3 : IComponent { public byte a, b, c; }

    /// <summary>
    /// A struct with interior padding: a byte, then a long, which the runtime must align.
    /// </summary>
    /// <remarks>
    /// The gap between <c>flag</c> and <c>value</c> is not written by any field assignment, so its
    /// contents are whatever the memory held. That is precisely the case where a hash over raw
    /// memory can see something the value does not contain.
    /// </remarks>
    private struct Padded : IComponent { public byte flag; public long value; }

    /// <summary>
    /// Explicitly padded to a size that is not a multiple of eight, with a field at the far end.
    /// </summary>
    /// <remarks>
    /// The last field sits past several whole words, so a tail path that dropped the remainder
    /// would fail to see it while every other test still passed.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Size = 20)]
    private struct FarField : IComponent { public int head; public int tail; }

    /// <summary>The simulation's own numeric type, in a plausible shape.</summary>
    private struct Position : IComponent { public FP64 x, y, z; }

    #endregion

    #region helpers

    private static long HashOf<T>(T value) where T : struct, IComponent
    {
        var store   = new EntityStore();
        var systems = new SystemRoot(store);
        store.CreateEntity(value);
        return new FrifloSimulation(store, systems).GetStateHash();
    }

    /// <summary>
    /// Assert that two values of the same component type hash differently.
    /// </summary>
    /// <remarks>
    /// Stated as "must differ" rather than "must be some value": the hash is a comparison device,
    /// so what has to hold is that a change in the data reaches it.
    /// </remarks>
    private static void AssertDistinguishes<T>(T left, T right, string what) where T : struct, IComponent
    {
        Assert.AreNotEqual(HashOf(left), HashOf(right),
            $"the state hash cannot tell two {typeof(T).Name} values apart, differing in {what} - " +
            "a divergence in those bytes would go unreported by every peer");
    }

    #endregion

    #region tests

    /// <summary>
    /// A difference in any byte of a component changes the hash, at every width.
    /// </summary>
    /// <remarks>
    /// The widths are chosen for their tails: 8 has none, 4 and 12 have a four-byte one, 1 and 3
    /// are shorter than a single word. A tail handled wrongly typically shows up in exactly one of
    /// these classes, which is why the case is not made with one representative width.
    /// </remarks>
    [Test]
    public static void EveryByteOfAComponentReachesTheHash()
    {
        AssertDistinguishes(new W8  { a = 1 },            new W8  { a = 2 },            "its only field");
        AssertDistinguishes(new W4  { a = 1 },            new W4  { a = 2 },            "its only field");
        AssertDistinguishes(new W12 { a = 1, b = 7 },     new W12 { a = 2, b = 7 },     "the leading word");
        AssertDistinguishes(new W12 { a = 1, b = 7 },     new W12 { a = 1, b = 8 },     "the trailing tail");
        AssertDistinguishes(new W1  { a = 1 },            new W1  { a = 2 },            "its single byte");
        AssertDistinguishes(new W3  { a = 1, b = 2, c = 3 }, new W3 { a = 1, b = 2, c = 4 }, "its last byte");

        // The far field sits past whole words, in the part a mishandled tail would drop.
        AssertDistinguishes(new FarField { head = 1, tail = 5 }, new FarField { head = 1, tail = 6 },
                            "a field beyond the last whole word");

        // First, middle and last field of a multi-field struct: a read that lost track of its
        // offset typically still sees one end of the value correctly.
        AssertDistinguishes(new Position { x = FP64.FromInt(1) }, new Position { x = FP64.FromInt(2) }, "x");
        AssertDistinguishes(new Position { y = FP64.FromInt(1) }, new Position { y = FP64.FromInt(2) }, "y");
        AssertDistinguishes(new Position { z = FP64.FromInt(1) }, new Position { z = FP64.FromInt(2) }, "z");
    }

    /// <summary>
    /// The same component value hashes the same however its entity got there.
    /// </summary>
    /// <remarks>
    /// This is what makes the hash a state hash rather than a memory hash. Two entities holding
    /// equal values may sit at different slots, in stores of different sizes, reached by different
    /// routes — and one of them here is placed after a deletion, so it lands on memory a previous
    /// component occupied. If the hash reads anything the value does not define, this is where it
    /// shows: the padding bytes of the recycled slot still hold the old component's data.
    /// </remarks>
    [Test]
    public static void EqualValuesHashEqualRegardlessOfWhereTheyLive()
    {
        long clean = HashOf(new Padded { flag = 3, value = 42 });

        // The same entity-allocation metadata, but the component slot held a different value first.
        // Do not create/delete an extra entity here: entity revisions and the recycle stack are
        // deterministic state in their own right and are intentionally covered by the whole-state hash.
        var store   = new EntityStore();
        var systems = new SystemRoot(store);
        var victim  = store.CreateEntity(new Padded { flag = 200, value = -1 });
        victim.RemoveComponent<Padded>();
        victim.AddComponent(new Padded { flag = 3, value = 42 });
        long recycled = new FrifloSimulation(store, systems).GetStateHash();

        Assert.AreEqual(clean, recycled,
            "the same component value hashed differently after landing on a recycled slot - the hash " +
            "is reading bytes the value does not define, so two peers whose allocation history " +
            "differs would report a desync that is not one");
    }

    /// <summary>
    /// A component's hash does not depend on how many entities are hashed alongside it.
    /// </summary>
    /// <remarks>
    /// Guards against a word-at-a-time read running off the end of one component into the next:
    /// components of one type are stored back to back in a single array, so an overrun reads the
    /// neighbour rather than faulting, and the result would be correct for a lone entity and wrong
    /// for a populated store — passing every single-entity test.
    /// </remarks>
    [Test]
    public static void AComponentHashDoesNotDependOnItsNeighbours()
    {
        // Both stores finish with the same two live entity identities. In B, the second entity
        // temporarily occupies the adjacent component slot; removing only its component preserves
        // allocator/revision metadata while leaving stale neighbour bytes behind.
        var storeA   = new EntityStore();
        storeA.CreateEntity(new W12 { a = 5, b = 6 });
        storeA.CreateEntity();
        var hashA = new FrifloSimulation(storeA, new SystemRoot(storeA)).GetStateHash();

        var storeB   = new EntityStore();
        storeB.CreateEntity(new W12 { a = 5, b = 6 });
        var extra = storeB.CreateEntity();
        extra.AddComponent(new W12 { a = long.MaxValue, b = int.MinValue });
        var hashB = new FrifloSimulation(storeB, new SystemRoot(storeB)).GetStateHash();

        // Removing the neighbour must bring the two back into agreement. Asserted this way round
        // rather than on the totals, which legitimately differ while the second entity exists.
        extra.RemoveComponent<W12>();
        var hashBAfter = new FrifloSimulation(storeB, new SystemRoot(storeB)).GetStateHash();

        Assert.AreNotEqual(hashA, hashB, "the second entity did not affect the hash at all - the test proves nothing");
        Assert.AreEqual(hashA, hashBAfter,
            "an entity's contribution to the hash changed when a neighbouring component was removed - " +
            "the per-component read is crossing into adjacent storage");
    }

    /// <summary>
    /// The tail is read in the same byte order as the body.
    /// </summary>
    /// <remarks>
    /// The byte-at-a-time hash this replaced was endian-agnostic by construction — it consumed
    /// bytes in address order, the same sequence on every machine. Reading words is not: it has to
    /// name a byte order, and the tail has to use the one the body uses. A tail assembled the other
    /// way round hashes identical state differently depending on where in the struct it sits.
    /// <para>
    /// <b>Stated as an equality, not as "can it tell values apart".</b> That distinction is the
    /// point of this test: reversing the tail is still a bijection, so every value still maps to a
    /// distinct hash and every "these two must differ" assertion continues to pass. An earlier
    /// version of this test asked only that question and was confirmed by re-injection to miss the
    /// defect entirely. What actually pins the byte order is comparing the <i>same eight bytes</i>
    /// hashed once as a whole word and once as a tail — done directly against
    /// <c>StructHeap.HashComponent</c>, because that equality is not observable through a state
    /// hash, where the two values necessarily belong to different component types.
    /// </para>
    /// </remarks>
    [Test]
    public static void TheTailIsReadInTheSameByteOrderAsTheBody()
    {
        // FNV relies on wrap-around, which this project compiles as checked by default.
        unchecked {
            const ulong prime = 1099511628211;
            const ulong seed  = 14695981039346656037;

            // Four payload bytes 01 02 03 04, at offset 0 of a 4-byte component: the whole value is
            // tail, so this is the tail path assembling them alone.
            ulong asTail = HashComponentOf(new W4 { a = 0x04030201 });

            // Little-endian assembly puts byte 0 in the low position, giving the same word the
            // value itself is. Any other order (reversed, or big-endian) produces a different word
            // while remaining perfectly injective - which is why "can it tell values apart" cannot
            // see it.
            Assert.AreEqual((seed ^ 0x04030201UL) * prime, asTail,
                "the tail path does not assemble its bytes little-endian - the same bytes then hash " +
                "differently depending on where in a struct they sit, and peers of differing " +
                "endianness disagree on identical state");

            // The whole-word path, on the same byte pattern promoted to a full word.
            ulong asBody = HashComponentOf(new Word8 { payload = 0x0807060504030201 });
            Assert.AreEqual((seed ^ 0x0807060504030201UL) * prime, asBody,
                "the whole-word path is not reading little-endian - big-endian peers would disagree " +
                "with little-endian ones on identical state");

            // Both paths in one value: 12 bytes is one whole word plus a 4-byte tail. Pinning the
            // combined result is what catches a tail that is self-consistent but disagrees with the
            // body it is mixed after.
            ulong mixed    = HashComponentOf(new W12 { a = 0x0807060504030201, b = 0x0C0B0A09 });
            ulong expected = (((seed ^ 0x0807060504030201UL) * prime) ^ 0x0C0B0A09UL) * prime;
            Assert.AreEqual(expected, mixed,
                "body and tail disagree on byte order within a single component");
        }
    }

    /// <summary>Hash one component value through the same routine the state hash uses.</summary>
    /// <remarks>
    /// Reaches <c>StructHeap.HashComponent</c> directly. The equality this test needs holds between
    /// two different component <i>types</i>, and a state hash folds the type in — so the property
    /// is simply not visible from the outside.
    /// </remarks>
    private static ulong HashComponentOf<T>(T value) where T : struct, IComponent
    {
        var store = new EntityStore();
        store.CreateEntity(value);
        var archetype = store.GetArchetypeAt(store.ArchsCount - 1);
        return archetype.HashHeapComponent(0, 0);
    }

    /// <summary>8 bytes: exactly one whole word.</summary>
    private struct Word8 : IComponent { public ulong payload; }

    #endregion
}
