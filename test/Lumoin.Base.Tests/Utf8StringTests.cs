using System.Buffers;
using System.Globalization;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lumoin.Base.Tests;

/// <summary>
/// Tests for <see cref="Utf8String"/>: a non-owning byte view whose equality and ordering are defined
/// over its UTF-8 bytes and are independent of how (or whether) the hash was precomputed.
/// </summary>
[TestClass]
public sealed class Utf8StringTests
{
    [TestMethod]
    public void EqualInstancesProduceSameHashCode()
    {
        byte[] bytes = "http://example.org/test"u8.ToArray();
        Utf8String a = new(bytes);
        Utf8String b = new(bytes.AsMemory());

        Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
    }


    [TestMethod]
    public void EqualInstancesAreEqual()
    {
        Utf8String a = new("http://example.org/test"u8.ToArray());
        Utf8String b = new("http://example.org/test"u8.ToArray());

        Assert.IsTrue(a == b);
        Assert.IsTrue(a.Equals(b));
        Assert.IsFalse(a != b);
    }


    [TestMethod]
    public void DifferentInstancesAreNotEqual()
    {
        Utf8String a = new("alpha"u8.ToArray());
        Utf8String b = new("beta"u8.ToArray());

        Assert.IsTrue(a != b);
        Assert.IsFalse(a.Equals(b));
    }


    [TestMethod]
    public void CompareToAndOperatorsGiveUnsignedLexicographicOrder()
    {
        Utf8String a = new("aaa"u8.ToArray());
        Utf8String b = new("bbb"u8.ToArray());

        Assert.IsLessThan(0, a.CompareTo(b));
        Assert.IsGreaterThan(0, b.CompareTo(a));
        Assert.AreEqual(0, a.CompareTo(a));

        Assert.IsTrue(a < b);
        Assert.IsTrue(a <= b);
        Assert.IsTrue(b > a);
        Assert.IsTrue(b >= a);
    }


    [TestMethod]
    public void ToStringDecodesUtf8()
    {
        Utf8String s = new("héllo"u8.ToArray());

        Assert.AreEqual("héllo", s.ToString());
    }


    [TestMethod]
    public void EmptyAndDefaultAgree()
    {
        Utf8String empty = Utf8String.Empty;
        Utf8String def = default;
        Utf8String overEmptyMemory = new(ReadOnlyMemory<byte>.Empty);

        Assert.IsTrue(empty.IsEmpty);
        Assert.AreEqual(0, empty.Length);
        Assert.IsFalse(empty.HasPrecomputedHash);
        Assert.AreEqual(empty, def);
        Assert.AreEqual(empty, overEmptyMemory);
        Assert.AreEqual(empty.GetHashCode(), overEmptyMemory.GetHashCode());
    }


    [TestMethod]
    public void LengthReflectsByteCount()
    {
        //The é character is two bytes in UTF-8.
        Utf8String s = new("café"u8.ToArray());

        Assert.AreEqual(5, s.Length);
    }


    [TestMethod]
    public void EagerDeferredAndSliceCompareAndHashEqual()
    {
        byte[] bytes = "predicate"u8.ToArray();
        Utf8String eager = new(bytes);
        Utf8String deferred = Utf8String.WithoutPrecomputedHash(bytes);

        //A slice of "Xpredicate" over the same trailing bytes.
        byte[] prefixed = "Xpredicate"u8.ToArray();
        Utf8String slice = new Utf8String(prefixed).Slice(1);

        Assert.IsTrue(eager.Equals(deferred));
        Assert.IsTrue(eager.Equals(slice));
        Assert.AreEqual(eager.GetHashCode(), deferred.GetHashCode());
        Assert.AreEqual(eager.GetHashCode(), slice.GetHashCode());
    }


    [TestMethod]
    public void WithoutPrecomputedHashIsDeferredButHashesEqually()
    {
        byte[] bytes = "deferred"u8.ToArray();
        Utf8String eager = new(bytes);
        Utf8String deferred = Utf8String.WithoutPrecomputedHash(bytes);

        Assert.IsTrue(eager.HasPrecomputedHash);
        Assert.IsFalse(deferred.HasPrecomputedHash);
        Assert.AreEqual(eager.GetHashCode(), deferred.GetHashCode());
    }


    [TestMethod]
    public void NormalizeHashRemapsZeroAndComputeNeverReturnsZero()
    {
        Assert.AreEqual(1, Utf8String.NormalizeHash(0));
        Assert.AreEqual(42, Utf8String.NormalizeHash(42));
        Assert.AreEqual(-7, Utf8String.NormalizeHash(-7));

        //ComputeHashCode normalizes, so it can never collide with the deferred sentinel.
        Assert.AreNotEqual(0, Utf8String.ComputeHashCode("anything"u8));
        Assert.AreNotEqual(0, Utf8String.ComputeHashCode(ReadOnlySpan<byte>.Empty));
    }


    [TestMethod]
    public void StampedZeroHashReadsAsPrecomputedOne()
    {
        //The internal stamping ctor must apply the same 0->1 remap.
        Utf8String stamped = new("x"u8.ToArray(), precomputedHash: 0);

        Assert.IsTrue(stamped.HasPrecomputedHash);
        Assert.AreEqual(1, stamped.GetHashCode());
    }


    [TestMethod]
    public void SliceIsZeroCopyViewOverSameBacking()
    {
        byte[] bytes = "namespace:local"u8.ToArray();
        Utf8String full = new(bytes);

        int colon = full.IndexOf((byte)':');
        Utf8String prefix = full.Slice(0, colon);
        Utf8String local = full[(colon + 1)..];

        Assert.AreEqual("namespace", prefix.ToString());
        Assert.AreEqual("local", local.ToString());
        Assert.IsFalse(prefix.HasPrecomputedHash);
        Assert.IsTrue(prefix.Equals(new Utf8String("namespace"u8.ToArray())));

        //Zero-copy: the slice's memory aliases the original backing array.
        Assert.IsTrue(full.Memory.Slice(0, colon).Span == prefix.Span);
    }


    [TestMethod]
    public void ByteSearchHelpers()
    {
        Utf8String s = new("http://example.org/path"u8.ToArray());

        Assert.IsTrue(s.SequenceEqual("http://example.org/path"u8));
        Assert.IsTrue(s.StartsWith("http://"u8));
        Assert.IsTrue(s.EndsWith("/path"u8));
        Assert.IsTrue(s.Contains((byte)':'));
        Assert.IsTrue(s.Contains("example"u8));
        Assert.AreEqual(4, s.IndexOf((byte)':'));
        Assert.AreEqual(7, s.IndexOf("example"u8));

        SearchValues<byte> delimiters = SearchValues.Create("/:#"u8);
        Assert.AreEqual(4, s.IndexOfAny(delimiters));
    }


    [TestMethod]
    public void TryFromUtf8ValidatesMemory()
    {
        Assert.IsTrue(Utf8String.TryFromUtf8("valid"u8.ToArray().AsMemory(), out Utf8String valid));
        Assert.AreEqual("valid", valid.ToString());

        byte[] invalid = [0xC3, 0x28];
        Assert.IsFalse(Utf8String.TryFromUtf8(invalid.AsMemory(), out Utf8String result));
        Assert.IsTrue(result.IsEmpty);
    }


    [TestMethod]
    public void Utf8SpanFormattableCopiesBytes()
    {
        Utf8String s = new("token"u8.ToArray());

        Span<byte> destination = stackalloc byte[8];
        Assert.IsTrue(s.TryFormat(destination, out int bytesWritten, provider: CultureInfo.InvariantCulture));
        Assert.AreEqual(5, bytesWritten);
        Assert.IsTrue(destination[..bytesWritten].SequenceEqual("token"u8));

        Span<byte> tooSmall = stackalloc byte[2];
        Assert.IsFalse(s.TryFormat(tooSmall, out int none, provider: CultureInfo.InvariantCulture));
        Assert.AreEqual(0, none);
    }


    [TestMethod]
    public void SpanFormattableDecodesChars()
    {
        Utf8String s = new("héllo"u8.ToArray());

        Span<char> destination = stackalloc char[8];
        Assert.IsTrue(s.TryFormat(destination, out int charsWritten, provider: CultureInfo.InvariantCulture));
        Assert.AreEqual("héllo", new string(destination[..charsWritten]));

        Span<char> tooSmall = stackalloc char[1];
        Assert.IsFalse(s.TryFormat(tooSmall, out _, provider: CultureInfo.InvariantCulture));
    }


    [TestMethod]
    public void ImplicitConversionToReadOnlySpan()
    {
        Utf8String s = new("bytes"u8.ToArray());

        ReadOnlySpan<byte> span = s;
        Assert.IsTrue(span.SequenceEqual("bytes"u8));
    }
}
