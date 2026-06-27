using System.Collections.Frozen;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lumoin.Base.Tests;

/// <summary>
/// Tests for <see cref="Utf8StringComparer"/>: its two hashing faces must agree for equal bytes (so the
/// alternate-by-span lookup is correct), <see cref="Utf8StringComparer.Create(Utf8HashFunction)"/> must
/// honor a custom hash, and span-keyed insertion via <c>Create</c> must be refused.
/// </summary>
[TestClass]
public sealed class Utf8StringComparerTests
{
    [TestMethod]
    public void OrdinalEqualityAndHashOverValues()
    {
        Utf8String a = new("term"u8.ToArray());
        Utf8String b = new("term"u8.ToArray());
        Utf8String c = new("other"u8.ToArray());

        Assert.IsTrue(Utf8StringComparer.Ordinal.Equals(a, b));
        Assert.IsFalse(Utf8StringComparer.Ordinal.Equals(a, c));
        Assert.AreEqual(Utf8StringComparer.Ordinal.GetHashCode(a), Utf8StringComparer.Ordinal.GetHashCode(b));
    }


    [TestMethod]
    public void HashFacesAgreeAndUnifyWithStruct()
    {
        byte[] bytes = "http://example.org/predicate"u8.ToArray();
        Utf8String value = new(bytes);

        int fromValue = Utf8StringComparer.Ordinal.GetHashCode(value);
        int fromSpan = Utf8StringComparer.Ordinal.GetHashCode(bytes.AsSpan());

        Assert.AreEqual(fromValue, fromSpan);
        Assert.AreEqual(Utf8String.ComputeHashCode(bytes), fromSpan);
        Assert.AreEqual(value.GetHashCode(), fromSpan);
    }


    [TestMethod]
    public void AlternateEqualsComparesSpanToValue()
    {
        Utf8String value = new("scope"u8.ToArray());

        Assert.IsTrue(Utf8StringComparer.Ordinal.Equals("scope"u8, value));
        Assert.IsFalse(Utf8StringComparer.Ordinal.Equals("other"u8, value));
    }


    [TestMethod]
    public void CompareOrdersByBytes()
    {
        Utf8String a = new("a"u8.ToArray());
        Utf8String b = new("b"u8.ToArray());

        Assert.IsLessThan(0, Utf8StringComparer.Ordinal.Compare(a, b));
        Assert.IsGreaterThan(0, Utf8StringComparer.Ordinal.Compare(b, a));
        Assert.AreEqual(0, Utf8StringComparer.Ordinal.Compare(a, a));
    }


    [TestMethod]
    public void CreateThrowsToRefuseSpanKeyedInsertion()
    {
        Assert.ThrowsExactly<NotSupportedException>(() => Utf8StringComparer.Ordinal.Create("x"u8));
    }


    [TestMethod]
    public void CreateRejectsNullHashFunction()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => Utf8StringComparer.Create(null!));
    }


    [TestMethod]
    public void CustomHashFunctionDrivesBothFacesAndIsNormalized()
    {
        //A deterministic function (here a constant) makes the bucketing stable and process-independent.
        Utf8StringComparer comparer = Utf8StringComparer.Create(static _ => 12345);
        Utf8String value = new("anything"u8.ToArray());

        Assert.AreEqual(12345, comparer.GetHashCode("anything"u8));
        Assert.AreEqual(12345, comparer.GetHashCode(value));

        //A function returning zero is normalized to one, matching the deferred-sentinel remap.
        Utf8StringComparer zeroHash = Utf8StringComparer.Create(static _ => 0);
        Assert.AreEqual(1, zeroHash.GetHashCode("anything"u8));

        //Equality is always over the bytes, regardless of the hash function.
        Assert.IsTrue(comparer.Equals(value, new Utf8String("anything"u8.ToArray())));
    }


    [TestMethod]
    public void EnablesZeroAllocFrozenSetProbingBySpan()
    {
        FrozenSet<Utf8String> set = new[]
        {
            new Utf8String("openid"u8.ToArray()),
            new Utf8String("profile"u8.ToArray()),
            new Utf8String("email"u8.ToArray())
        }.ToFrozenSet(Utf8StringComparer.Ordinal);

        FrozenSet<Utf8String>.AlternateLookup<ReadOnlySpan<byte>> byBytes =
            set.GetAlternateLookup<ReadOnlySpan<byte>>();

        Assert.IsTrue(byBytes.TryGetValue("profile"u8, out Utf8String found));
        Assert.AreEqual("profile", found.ToString());
        Assert.IsFalse(byBytes.TryGetValue("address"u8, out _));
    }
}
