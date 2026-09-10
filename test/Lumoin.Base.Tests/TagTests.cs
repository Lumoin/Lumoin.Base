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
    /// A struct value stands in for the crypto-context "dynamic enum" structs (MaterialSemantics etc.):
    /// a boxed struct entry must take part in content equality by value, not by box identity.
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
        //Two independently built tags with identical content must be equal: Tag compares its entries by
        //content, whereas a record over a FrozenDictionary compares the dictionaries by reference.
        Tag a = Tag.Create(42).With("hello").With(new Kind(7));
        Tag b = Tag.Create(42).With("hello").With(new Kind(7));

        Assert.AreNotSame(a, b);
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
    [DataRow(null)]
    public void NullComparisonsAreConsistent(Tag? nullTag)
    {
        //The null comes in through a parameter (rather than a literal) so the compiler cannot prove the
        //comparisons below are dead code — the whole point of the test is exercising them at runtime.
        Tag tag = Tag.Create(42);

        Assert.IsFalse(tag.Equals(nullTag));
        Assert.IsFalse(tag == nullTag);
        Assert.IsFalse(nullTag == tag);
        Assert.IsTrue(tag != nullTag);
    }


    [TestMethod]
    public void ObjectEqualsMatchesTypedEqualityAndRejectsOtherTypes()
    {
        Tag a = Tag.Create(42).With("hello");
        Tag b = Tag.Create(42).With("hello");

        Assert.IsTrue(((object)a).Equals(b));
        Assert.IsFalse(((object)a).Equals("not a tag"));
    }


    [TestMethod]
    public void ToStringFormatsEmptyAndSingleEntryTags()
    {
        Assert.AreEqual("Tag: (empty)", Tag.Empty.ToString());
        Assert.AreEqual("Tag: [Int32=42]", Tag.Create(42).ToString());
    }


    [TestMethod]
    public void ToStringFormatsTwoEntryTagWithBracketsAndBothEntries()
    {
        //FrozenDictionary order is unspecified, so a multi-entry tag can only be asserted structurally and
        //by substring — no exact full-string comparison, unlike the single-entry case above.
        string result = Tag.Create(42).With("hello").ToString();

        Assert.IsTrue(result.StartsWith("Tag: [", StringComparison.Ordinal), $"'{result}' should start with 'Tag: ['.");
        Assert.AreEqual(']', result[^1], $"'{result}' should end with ']'.");
        Assert.Contains("Int32=42", result, "The Int32 entry must be present.");
        Assert.Contains("String=hello", result, "The String entry must be present.");
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
