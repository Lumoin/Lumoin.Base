using System.Buffers;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lumoin.Base.Tests;

/// <summary>
/// Tests for <see cref="Utf8StringPool"/>: it deduplicates by bytes, probes by raw span without
/// allocating, packs values into bulk-freed arena slabs, honors the chosen <see cref="AllocationKind"/>
/// and validation policy, and invalidates everything on disposal.
/// </summary>
[TestClass]
public sealed class Utf8StringPoolTests
{
    [TestMethod]
    public void InternReturnsSameValueForDuplicateBytes()
    {
        using Utf8StringPool pool = new();

        Utf8String first = pool.Intern("http://example.org/resource"u8);
        Utf8String second = pool.Intern("http://example.org/resource"u8);

        Assert.AreEqual(first, second);
        Assert.AreEqual(1, pool.Count);
    }


    [TestMethod]
    public void InternDistinguishesDifferentValues()
    {
        using Utf8StringPool pool = new();

        Utf8String a = pool.Intern("alpha"u8);
        Utf8String b = pool.Intern("beta"u8);

        Assert.AreNotEqual(a, b);
        Assert.AreEqual(2, pool.Count);
    }


    [TestMethod]
    public void InternStringEncodesAsUtf8()
    {
        using Utf8StringPool pool = new();

        Utf8String interned = pool.Intern("hello");

        Assert.AreEqual("hello", interned.ToString());
        Assert.AreEqual(5, interned.Length);
    }


    [TestMethod]
    public void InternStringDeduplicatesWithByteVersion()
    {
        using Utf8StringPool pool = new();

        Utf8String fromBytes = pool.Intern("test"u8);
        Utf8String fromString = pool.Intern("test");

        Assert.AreEqual(fromBytes, fromString);
        Assert.AreEqual(1, pool.Count);
    }


    [TestMethod]
    public void InternLongStringFallsBackToPoolBuffer()
    {
        using Utf8StringPool pool = new();

        //Longer than the 256-byte stackalloc gate, forcing the pooled-buffer encode path.
        string longValue = new('a', 500);
        Utf8String interned = pool.Intern(longValue);

        Assert.AreEqual(longValue, interned.ToString());
        Assert.AreEqual(500, interned.Length);
        Assert.AreEqual(interned, pool.Intern(Encoding.UTF8.GetBytes(longValue)));
        Assert.AreEqual(1, pool.Count);
    }


    [TestMethod]
    public void DisposePreventsFurtherUse()
    {
        Utf8StringPool pool = new();
        pool.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => pool.Intern("test"u8));
        Assert.ThrowsExactly<ObjectDisposedException>(() => pool.Intern("test"));
        Assert.ThrowsExactly<ObjectDisposedException>(() => pool.TryGet("test"u8, out _));
        Assert.ThrowsExactly<ObjectDisposedException>(() => pool.RentScratch(8));
    }


    [TestMethod]
    public void DoubleDisposeDoesNotThrow()
    {
        Utf8StringPool pool = new();
        pool.Dispose();
        pool.Dispose();
    }


    [TestMethod]
    public void ArenaPacksThenGrowsAcrossSlabs()
    {
        //A small slab forces a second slab once packing overflows it.
        using Utf8StringPool pool = new(slabSize: 8);

        Utf8String a = pool.Intern("abc"u8);
        Utf8String b = pool.Intern("defg"u8);
        Utf8String c = pool.Intern("hi"u8);

        Assert.AreEqual("abc", a.ToString());
        Assert.AreEqual("defg", b.ToString());
        Assert.AreEqual("hi", c.ToString());
        Assert.AreEqual(3, pool.Count);
        Assert.IsGreaterThanOrEqualTo(2, pool.SlabCount, "Packing past the slab size must rent another slab.");
    }


    [TestMethod]
    public void OversizeValueGetsOwnBufferWithoutDisturbingCursor()
    {
        using Utf8StringPool pool = new(slabSize: 16);

        Utf8String oversize = pool.Intern("this-is-a-longer-string-than-the-buffer"u8);
        int slabsAfterOversize = pool.SlabCount;

        //A normal value after the oversize one must still pack into the active slab and round-trip.
        Utf8String normal = pool.Intern("ok"u8);

        Assert.AreEqual("this-is-a-longer-string-than-the-buffer", oversize.ToString());
        Assert.AreEqual("ok", normal.ToString());
        Assert.AreEqual(2, pool.Count);
        Assert.IsGreaterThanOrEqualTo(2, slabsAfterOversize, "The oversize value must rent its own dedicated buffer.");
    }


    [TestMethod]
    public void InternSingleSegmentSequenceMatchesSpan()
    {
        using Utf8StringPool pool = new();
        byte[] bytes = Encoding.UTF8.GetBytes("http://example.org/resource");

        Utf8String fromSpan = pool.Intern(bytes);
        Utf8String fromSequence = pool.Intern(new ReadOnlySequence<byte>(bytes));

        Assert.AreEqual(fromSpan, fromSequence);
        Assert.AreEqual(1, pool.Count);
    }


    [TestMethod]
    public void InternMultiSegmentSequenceDeduplicatesWithSpan()
    {
        using Utf8StringPool pool = new();
        byte[] bytes = Encoding.UTF8.GetBytes("http://example.org/resource");

        Utf8String fromSpan = pool.Intern(bytes);
        Utf8String fromSegments = pool.Intern(MultiSegment(bytes, chunkSize: 4));

        Assert.AreEqual(fromSpan, fromSegments);
        Assert.AreEqual(1, pool.Count);
    }


    [TestMethod]
    public void InternMultiSegmentSequencePreservesContent()
    {
        using Utf8StringPool pool = new();
        byte[] bytes = Encoding.UTF8.GetBytes("a value that is split across several buffer segments");

        Utf8String interned = pool.Intern(MultiSegment(bytes, chunkSize: 7));

        Assert.AreEqual("a value that is split across several buffer segments", interned.ToString());
    }


    [TestMethod]
    public void RentScratchGuardsLength()
    {
        using Utf8StringPool pool = new();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => pool.RentScratch(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => pool.RentScratch(-1));
    }


    [TestMethod]
    public void RentScratchReturnsRequestedLength()
    {
        using Utf8StringPool pool = new();

        using IMemoryOwner<byte> scratch = pool.RentScratch(32);
        Assert.IsGreaterThanOrEqualTo(32, scratch.Memory.Length);
    }


    [TestMethod]
    public void TryGetProbesWithoutGrowingTheArena()
    {
        using Utf8StringPool pool = new();

        Assert.IsFalse(pool.TryGet("absent"u8, out _));
        Assert.AreEqual(0, pool.Count);
        Assert.AreEqual(0, pool.TotalBytesInterned);

        Utf8String interned = pool.Intern("present"u8);
        Assert.IsTrue(pool.TryGet("present"u8, out Utf8String found));
        Assert.AreEqual(interned, found);
        Assert.AreEqual(1, pool.Count);
        Assert.AreEqual(7, pool.TotalBytesInterned);
    }


    [TestMethod]
    public void ValidationIsOnByDefaultAndCanBeOptedOut()
    {
        //Validation is on by default, so malformed UTF-8 is rejected.
        using Utf8StringPool validating = new();
        Assert.ThrowsExactly<ArgumentException>(() => validating.Intern([0xC3, 0x28]));

        //Opting out interns opaque bytes without validating.
        using Utf8StringPool lenient = new(validateOnIntern: false);
        Utf8String interned = lenient.Intern([0xC3, 0x28]);
        Assert.AreEqual(2, interned.Length);
    }


    [TestMethod]
    public void PinnedAllocationKindInternsAndRoundTrips()
    {
        using Utf8StringPool pool = new(allocationKind: AllocationKind.Pinned);

        Utf8String interned = pool.Intern("pinned-term"u8);

        Assert.AreEqual("pinned-term", interned.ToString());
        Assert.AreEqual(interned, pool.Intern("pinned-term"u8));
    }


    [TestMethod]
    public void NonManagedAllocationKindRequiresBaseMemoryPool()
    {
        //MemoryPool<byte>.Shared is not a BaseMemoryPool, so it cannot honor a protected kind.
        Assert.ThrowsExactly<ArgumentException>(
            () => new Utf8StringPool(MemoryPool<byte>.Shared, allocationKind: AllocationKind.Pinned));
    }


    [TestMethod]
    public void InjectedPoolIsNotDisposedByTheInterner()
    {
        Utf8StringPool interner = new(BaseMemoryPool.Shared);
        interner.Intern("term"u8);
        interner.Dispose();

        //The injected shared pool must remain usable after the interner is disposed.
        using IMemoryOwner<byte> rental = BaseMemoryPool.Shared.Rent(16);
        Assert.IsGreaterThanOrEqualTo(16, rental.Memory.Length);
    }


    [TestMethod]
    public void CustomComparerGivesDeterministicStampedHash()
    {
        //A deterministic hash makes interned values' hashes stable across processes.
        Utf8StringComparer deterministic = Utf8StringComparer.Create(Fnv1a32);
        using Utf8StringPool pool = new(comparer: deterministic);

        Utf8String interned = pool.Intern("stable"u8);

        Assert.AreEqual(Utf8String.NormalizeHash(Fnv1a32("stable"u8)), interned.GetHashCode());
        //Dedup still works under the custom regime.
        Assert.AreEqual(interned, pool.Intern("stable"u8));
        Assert.AreEqual(1, pool.Count);
    }


    [TestMethod]
    public void OrdinalStoredHashEqualsBucketingHash()
    {
        using Utf8StringPool pool = new();

        Utf8String interned = pool.Intern("bucketed"u8);

        Assert.AreEqual(Utf8StringComparer.Ordinal.GetHashCode("bucketed"u8), interned.GetHashCode());
    }


    [TestMethod]
    public void ResetReclaimsAndAllowsReuse()
    {
        using Utf8StringPool pool = new();

        pool.Intern("first"u8);
        pool.Intern("second"u8);
        Assert.AreEqual(2, pool.Count);

        pool.Reset();

        Assert.AreEqual(0, pool.Count);
        Assert.AreEqual(0, pool.TotalBytesInterned);

        Utf8String reused = pool.Intern("third"u8);
        Assert.AreEqual("third", reused.ToString());
        Assert.AreEqual(1, pool.Count);
    }


    [TestMethod]
    public void ResetPreservesConfiguration()
    {
        using Utf8StringPool pool = new(validateOnIntern: false);

        pool.Reset();

        //Validation-off configuration must survive the reset.
        Utf8String interned = pool.Intern([0xC3, 0x28]);
        Assert.AreEqual(2, interned.Length);
    }


    [TestMethod]
    public void ResetCanBeCalledRepeatedly()
    {
        using Utf8StringPool pool = new();
        pool.Intern("x"u8);

        pool.Reset();
        pool.Reset();

        Assert.AreEqual(0, pool.Count);
        Assert.AreEqual("y", pool.Intern("y"u8).ToString());
    }


    [TestMethod]
    public void ResetAfterDisposeThrows()
    {
        Utf8StringPool pool = new();
        pool.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => pool.Reset());
    }


    [TestMethod]
    public void ResetReturnsRentalsToInjectedPoolWithoutDisposingIt()
    {
        Utf8StringPool interner = new(BaseMemoryPool.Shared);
        interner.Intern("term"u8);

        interner.Reset();

        //The injected shared pool must remain usable after the interner resets its slabs.
        using IMemoryOwner<byte> rental = BaseMemoryPool.Shared.Rent(16);
        Assert.IsGreaterThanOrEqualTo(16, rental.Memory.Length);

        //And the reset pool itself still works.
        Assert.AreEqual("after", interner.Intern("after"u8).ToString());
        interner.Dispose();
    }


    /// <summary>
    /// A small, fixed-seed, dependency-free deterministic hash standing in for any cross-process-stable
    /// function a distributed caller would supply.
    /// </summary>
    private static int Fnv1a32(ReadOnlySpan<byte> bytes)
    {
        const uint OffsetBasis = 2166136261;
        const uint Prime = 16777619;

        uint hash = OffsetBasis;
        foreach(byte b in bytes)
        {
            hash = (hash ^ b) * Prime;
        }

        return unchecked((int)hash);
    }


    private static ReadOnlySequence<byte> MultiSegment(ReadOnlyMemory<byte> data, int chunkSize)
    {
        BufferSegment first = new(data.Slice(0, Math.Min(chunkSize, data.Length)));
        BufferSegment last = first;
        for(int offset = chunkSize; offset < data.Length; offset += chunkSize)
        {
            int length = Math.Min(chunkSize, data.Length - offset);
            last = last.Append(data.Slice(offset, length));
        }

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }


    private sealed class BufferSegment: ReadOnlySequenceSegment<byte>
    {
        public BufferSegment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }


        public BufferSegment Append(ReadOnlyMemory<byte> memory)
        {
            BufferSegment next = new(memory)
            {
                RunningIndex = RunningIndex + Memory.Length
            };

            Next = next;

            return next;
        }
    }
}
