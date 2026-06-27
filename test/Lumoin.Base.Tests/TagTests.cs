using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lumoin.Base.Tests;

/// <summary>
/// Tests for <see cref="Tag"/>: a type-keyed metadata set whose equality is by content and
/// order-independent, with values written and read only through their type.
/// </summary>
[TestClass]
public sealed class TagTests
{
    /// <summary>
    /// A struct value stands in for the crypto-context "dynamic enum" structs (MaterialSemantics etc.),
    /// the case the old record-over-dictionary equality got wrong.
    /// </summary>
    private readonly record struct Kind(int Code);


    [TestMethod]
    public void CreateAndGetRoundTrip()
    {
        Tag tag = Tag.Create(42);

        Assert.AreEqual(42, tag.Get<int>());
        Assert.AreEqual(1, tag.Count);
    }


    [TestMethod]
    public void WithAddsAndReplaces()
    {
        Tag tag = Tag.Create(42).With("hello").With(new Kind(7));

        Assert.AreEqual(42, tag.Get<int>());
        Assert.AreEqual("hello", tag.Get<string>());
        Assert.AreEqual(new Kind(7), tag.Get<Kind>());
        Assert.AreEqual(3, tag.Count);

        //A second value of the same type replaces the first.
        Tag replaced = tag.With(99);
        Assert.AreEqual(99, replaced.Get<int>());
        Assert.AreEqual(3, replaced.Count);
    }


    [TestMethod]
    public void WithoutRemovesAndReturnsSameWhenAbsentAndEmptyWhenLast()
    {
        Tag tag = Tag.Create(42).With("hello");

        Tag withoutInt = tag.Without<int>();
        Assert.IsFalse(withoutInt.Contains<int>());
        Assert.IsTrue(withoutInt.Contains<string>());

        //Removing an absent key returns the same instance.
        Assert.AreSame(tag, tag.Without<bool>());

        //Removing the last entry yields the shared Empty.
        Assert.AreSame(Tag.Empty, Tag.Create(42).Without<int>());
    }


    [TestMethod]
    public void TryGetAndContainsReportPresence()
    {
        Tag tag = Tag.Create("value");

        Assert.IsTrue(tag.TryGet(out string? present));
        Assert.AreEqual("value", present);
        Assert.IsTrue(tag.Contains<string>());

        Assert.IsFalse(tag.TryGet(out int _));
        Assert.IsFalse(tag.Contains<int>());
    }


    [TestMethod]
    public void GetThrowsWhenAbsent()
    {
        Tag tag = Tag.Empty;

        Assert.ThrowsExactly<KeyNotFoundException>(() => tag.Get<int>());
    }


    [TestMethod]
    public void EmptyHasNoEntries()
    {
        Assert.AreEqual(0, Tag.Empty.Count);
        Assert.IsEmpty(Tag.Empty.Entries);
    }


    [TestMethod]
    public void EqualityIsByContentAcrossDistinctInstances()
    {
        //The regression that motivated the bespoke type: a record over a FrozenDictionary compared the
        //dictionaries by reference, so these would have been UNEQUAL despite identical content.
        Tag a = Tag.Create(42).With("hello").With(new Kind(7));
        Tag b = Tag.Create(42).With("hello").With(new Kind(7));

        Assert.IsFalse(ReferenceEquals(a, b));
        Assert.AreEqual(a, b);
        Assert.IsTrue(a == b);
        Assert.IsFalse(a != b);
        Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
    }


    [TestMethod]
    public void EqualityAndHashAreOrderIndependent()
    {
        Tag built1 = Tag.Create(42).With("hello").With(new Kind(7));
        Tag built2 = Tag.Create(new Kind(7)).With(42).With("hello");

        Assert.AreEqual(built1, built2);
        Assert.AreEqual(built1.GetHashCode(), built2.GetHashCode());
    }


    [TestMethod]
    public void DifferentContentIsNotEqual()
    {
        Tag a = Tag.Create(42).With("hello");
        Tag b = Tag.Create(42).With("world");
        Tag c = Tag.Create(42);

        Assert.AreNotEqual(a, b);
        Assert.AreNotEqual(a, c);
        Assert.IsTrue(a != b);
    }


    [TestMethod]
    public void EntriesProjectsTypeKeyedPairs()
    {
        Tag tag = Tag.Create(42).With("hello");

        HashSet<Type> keys = tag.Entries.Select(entry => entry.Key).ToHashSet();

        Assert.HasCount(2, tag.Entries);
        Assert.Contains(typeof(int), keys);
        Assert.Contains(typeof(string), keys);
    }


    [TestMethod]
    public void EmptyTagsAreEqual()
    {
        Assert.AreEqual(Tag.Empty, Tag.Create(1).Without<int>());
        Assert.IsTrue(Tag.Empty == Tag.Create(1).Without<int>());
    }
}
