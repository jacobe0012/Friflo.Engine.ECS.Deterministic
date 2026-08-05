using System.Collections.Generic;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using NUnit.Framework;
using xpTURN.Klotho.Core;

// ReSharper disable once CheckNamespace
namespace Klotho.Friflo.Tests;

/// <summary>
/// Pins the tag bits down to the whole <c>Tags</c> bit set rather than its first word.
/// </summary>
/// <remarks>
/// A tag's index is allocated per schema, across every assembly the schema sees, so the 65th tag
/// declared anywhere lands in the second word. Tags are part of an entity's identity — the query
/// system dispatches on them — so two peers differing only in a high-word tag are genuinely
/// diverged, and the state hash exists to say so.
///
/// The tags below are declared purely to push past the 64-bit boundary; the schema is built once
/// per process from every <see cref="ITag"/> it can see, so their indices depend on the whole test
/// assembly rather than on this file. The test therefore locates a high-word tag by asking the
/// schema instead of assuming any particular index.
/// </remarks>
public static class Test_TagBits
{
    // 72 tags, enough that some index lands at 64 or above whatever else the assembly declares.
    private struct Tag00 : ITag { } private struct Tag01 : ITag { } private struct Tag02 : ITag { }
    private struct Tag03 : ITag { } private struct Tag04 : ITag { } private struct Tag05 : ITag { }
    private struct Tag06 : ITag { } private struct Tag07 : ITag { } private struct Tag08 : ITag { }
    private struct Tag09 : ITag { } private struct Tag10 : ITag { } private struct Tag11 : ITag { }
    private struct Tag12 : ITag { } private struct Tag13 : ITag { } private struct Tag14 : ITag { }
    private struct Tag15 : ITag { } private struct Tag16 : ITag { } private struct Tag17 : ITag { }
    private struct Tag18 : ITag { } private struct Tag19 : ITag { } private struct Tag20 : ITag { }
    private struct Tag21 : ITag { } private struct Tag22 : ITag { } private struct Tag23 : ITag { }
    private struct Tag24 : ITag { } private struct Tag25 : ITag { } private struct Tag26 : ITag { }
    private struct Tag27 : ITag { } private struct Tag28 : ITag { } private struct Tag29 : ITag { }
    private struct Tag30 : ITag { } private struct Tag31 : ITag { } private struct Tag32 : ITag { }
    private struct Tag33 : ITag { } private struct Tag34 : ITag { } private struct Tag35 : ITag { }
    private struct Tag36 : ITag { } private struct Tag37 : ITag { } private struct Tag38 : ITag { }
    private struct Tag39 : ITag { } private struct Tag40 : ITag { } private struct Tag41 : ITag { }
    private struct Tag42 : ITag { } private struct Tag43 : ITag { } private struct Tag44 : ITag { }
    private struct Tag45 : ITag { } private struct Tag46 : ITag { } private struct Tag47 : ITag { }
    private struct Tag48 : ITag { } private struct Tag49 : ITag { } private struct Tag50 : ITag { }
    private struct Tag51 : ITag { } private struct Tag52 : ITag { } private struct Tag53 : ITag { }
    private struct Tag54 : ITag { } private struct Tag55 : ITag { } private struct Tag56 : ITag { }
    private struct Tag57 : ITag { } private struct Tag58 : ITag { } private struct Tag59 : ITag { }
    private struct Tag60 : ITag { } private struct Tag61 : ITag { } private struct Tag62 : ITag { }
    private struct Tag63 : ITag { } private struct Tag64 : ITag { } private struct Tag65 : ITag { }
    private struct Tag66 : ITag { } private struct Tag67 : ITag { } private struct Tag68 : ITag { }
    private struct Tag69 : ITag { } private struct Tag70 : ITag { } private struct Tag71 : ITag { }

    private struct Value : IComponent { public int n; }

    /// <summary>A tag whose schema index is 64 or above, i.e. outside the first bit-set word.</summary>
    /// <remarks>
    /// Touching a tag type first: the schema is built lazily, and reading it before anything has
    /// referenced a tag yields a schema with none in it.
    /// </remarks>
    private static TagType HighWordTag()
    {
        _ = TagInfoIndex<Tag71>();
        // Tags[0] is null by schema convention: tag indices start at 1.
        foreach (var tagType in EntityStore.GetEntitySchema().Tags) {
            if (tagType != null && tagType.TagIndex >= 64) {
                return tagType;
            }
        }
        Assert.Fail("no tag with index >= 64 — the test assembly declares too few tags to reach the second word");
        return null;
    }

    /// <summary>Force the schema to include <typeparamref name="T"/>, returning its tag index.</summary>
    private static int TagInfoIndex<T>() where T : struct, ITag
    {
        var store  = new EntityStore();
        var entity = store.CreateEntity();
        entity.AddTag<T>();
        return entity.Tags.Count;
    }

    /// <summary>A simulation over an empty store with no systems: this exercises state, not behaviour.</summary>
    private static FrifloSimulation NewSimulation(out EntityStore store)
    {
        store = new EntityStore();
        return new FrifloSimulation(store, new SystemRoot(store), ApplyNothing);
    }

    private static void ApplyNothing(EntityStore store, List<ICommand> commands, int tick) { }

    /// <summary>
    /// Two stores identical except for a high-word tag must hash differently.
    /// </summary>
    /// <remarks>
    /// The tag is part of the entity's identity: queries select on it, so systems take different
    /// branches. If the hash cannot see it, two peers that disagree about it agree on the hash and
    /// the desync is never reported — it just silently keeps diverging.
    /// </remarks>
    [Test]
    public static void StateHash_SeesTagsBeyondTheFirstBitSetWord()
    {
        var tagType = HighWordTag();

        long HashWith(bool withTag)
        {
            var simulation = NewSimulation(out var store);
            var entity     = store.CreateEntity();
            entity.AddComponent(new Value { n = 1 });
            if (withTag) {
                entity.AddTags(new Tags(tagType));
            }
            return simulation.GetStateHash();
        }

        Assert.AreNotEqual(HashWith(false), HashWith(true),
            $"state hash ignores tag '{tagType.TagName}' (index {tagType.TagIndex}): two peers " +
            "disagreeing about it would never report a desync");
    }

    /// <summary>
    /// A full state must carry high-word tags, or the receiver rebuilds the entity untagged.
    /// </summary>
    /// <remarks>
    /// This is the server-driven bootstrap path: the client's whole world comes from this payload.
    /// Losing a tag there does not merely change a hash, it changes which queries match the
    /// entity, so the client simulates a different game from the first tick.
    /// </remarks>
    [Test]
    public static void FullState_CarriesTagsBeyondTheFirstBitSetWord()
    {
        var tagType = HighWordTag();
        var tags    = new Tags(tagType);

        var source = NewSimulation(out var sourceStore);
        var entity = sourceStore.CreateEntity();
        entity.AddComponent(new Value { n = 7 });
        entity.AddTags(tags);

        var target = NewSimulation(out var targetStore);
        target.RestoreFromFullState(source.SerializeFullState());

        var restored = new Entity(targetStore, entity.Id, entity.Revision);
        Assert.IsTrue(restored.Tags.HasAll(tags),
            $"tag '{tagType.TagName}' (index {tagType.TagIndex}) was dropped by the full state; " +
            "a server-driven client would build a world where this entity matches different queries");
    }
}
