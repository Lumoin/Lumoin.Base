using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.Metrics;
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
    public void InternSingleSegmentSequenceAvoidsTheScratchBufferCopy()
    {
        CountingMemoryPool countingPool = new();
        using Utf8StringPool pool = new(countingPool);
        int rentsAfterConstruction = countingPool.RentCount;

        byte[] bytes = Encoding.UTF8.GetBytes("http://example.org/resource");
        Utf8String interned = pool.Intern(new ReadOnlySequence<byte>(bytes));

        Assert.AreEqual(rentsAfterConstruction, countingPool.RentCount,
            "A single-segment sequence must take the direct-span fast path without renting a scratch buffer.");
        Assert.AreEqual("http://example.org/resource", interned.ToString());
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
    [DataRow(AllocationKind.Managed, "Managed")]
    [DataRow(AllocationKind.Pinned, "Pinned")]
    public void InterningRoutesThroughTheChosenAllocationKind(AllocationKind allocationKind, string expectedTag)
    {
        //The BaseMemoryPool ActivitySource is process-wide; only activities under this test's own trace count.
        Activity.Current = null;

        using var testRoot = new Activity(nameof(InterningRoutesThroughTheChosenAllocationKind));
        testRoot.Start();
        var testTraceId = testRoot.TraceId;

        var activities = new List<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "BaseMemoryPool",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity =>
            {
                if(activity.TraceId == testTraceId)
                {
                    activities.Add(activity);
                }
            }
        };

        ActivitySource.AddActivityListener(activityListener);

        using var meter = new Meter("Test.Utf8StringPool.AllocationKindRouting", "1.0.0");
        using var basePool = new BaseMemoryPool(meter);
        using var pool = new Utf8StringPool(basePool, allocationKind: allocationKind);

        pool.Intern("term"u8);

        testRoot.Stop();

        //Constructing the pool rents its first arena slab through RentBuffer, which is where the
        //chosen AllocationKind is threaded into the underlying BaseMemoryPool.Rent(size, kind) call.
        var slabRent = activities.FirstOrDefault(a => a.OperationName == "Rent");
        Assert.IsNotNull(slabRent, "Constructing the pool must rent its first arena slab.");
        Assert.AreEqual(expectedTag, slabRent.GetTagItem("allocationKind")?.ToString(),
            $"The arena slab must be rented with allocationKind {expectedTag}.");
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


    [TestMethod]
    public void ResetReturnsEverySlabWhenMoreThanOneWasRented()
    {
        CountingMemoryPool countingPool = new();
        using Utf8StringPool pool = new(countingPool, slabSize: 8);

        //A small slab forces a second arena slab once packing overflows it.
        pool.Intern("abc"u8);
        pool.Intern("defg"u8);
        pool.Intern("hi"u8);
        Assert.IsGreaterThanOrEqualTo(2, countingPool.RentCount, "Packing past the slab size must rent a second arena slab.");

        pool.Reset();

        //Reset disposes every slab rented up to that point, then rents exactly one fresh slab for reuse.
        Assert.AreEqual(countingPool.RentCount - 1, countingPool.DisposeCount,
            "Reset must return every previously rented slab, not just the active one.");
    }


    [TestMethod]
    public void CountAfterDisposeThrows()
    {
        Utf8StringPool pool = new();
        pool.Intern("term"u8);
        pool.Dispose();

        //Count answered on a disposed pool would report a cleared table as a healthy zero; it throws like
        //every other member instead, so a use-after-dispose surfaces where it happens.
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = pool.Count);
    }


    [TestMethod]
    public void DisposedPoolPublishesNoObservableMeasurements()
    {
        using Meter meter = new("Test.Utf8StringPool.DisposedMetrics");
        int measurements = 0;
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if(instrument.Meter == meter)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((_, _, _, _) => measurements++);
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => measurements++);
        listener.Start();

        Utf8StringPool pool = new(BaseMemoryPool.Shared, meter: meter);
        pool.Intern("term"u8);
        listener.RecordObservableInstruments();
        Assert.IsGreaterThanOrEqualTo(1, measurements, "A live pool must publish its observable instruments.");

        pool.Dispose();
        measurements = 0;
        listener.RecordObservableInstruments();

        //The callbacks are registered on a meter the pool does not own, so they outlive it and cannot be
        //unregistered; publishing nothing keeps a dead pool from reporting a healthy zero forever.
        Assert.AreEqual(0, measurements, "A disposed pool must publish no measurement.");
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


    /// <summary>
    /// A <see cref="MemoryPool{T}"/> stand-in that counts rents and returns, so a test can prove whether a
    /// code path rented an extra (scratch or arena) buffer, or that every previously rented buffer was
    /// actually released.
    /// </summary>
    private sealed class CountingMemoryPool: MemoryPool<byte>
    {
        public int RentCount { get; private set; }

        public int DisposeCount { get; private set; }

        public override int MaxBufferSize => int.MaxValue;

        public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
        {
            RentCount++;
            int size = minBufferSize < 0 ? 4096 : minBufferSize;

            return new CountingOwner(new byte[size], this);
        }

        protected override void Dispose(bool disposing)
        {
        }


        private sealed class CountingOwner(byte[] buffer, CountingMemoryPool pool): IMemoryOwner<byte>
        {
            public Memory<byte> Memory => buffer;

            public void Dispose() => pool.DisposeCount++;
        }
    }
}
