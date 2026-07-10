using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lumoin.Base.Tests;

/// <summary>
/// Behavioral tests for the protected-slab <see cref="AllocationKind.Native"/> tier
/// (<see cref="NativeRentMode.ProtectedSlab"/>): one shared backing region per slab subdivided into
/// canary-bracketed segments, covering exact-size rents, zeroize-and-recycle on return,
/// canary-violation detection and segment retirement, <c>TrimExcess</c> reclamation, and the
/// associated telemetry.
/// </summary>
[TestClass]
public sealed class ProtectedSlabTests
{
    public TestContext TestContext { get; set; } = null!;


    //A stand-in backing region owner: a managed array of exactly the requested size, that records
    //whether it was fully zeroed at the moment it was disposed (before this test clears it further).
    private sealed class InstrumentedRegionOwner(int size): IMemoryOwner<byte>
    {
        public byte[] Buffer { get; } = new byte[size];

        public bool IsDisposed { get; private set; }

        public bool WasZeroedAtDispose { get; private set; }

        public Memory<byte> Memory => Buffer;

        public void Dispose()
        {
            if(!IsDisposed)
            {
                WasZeroedAtDispose = Buffer.AsSpan().IndexOfAnyExcept((byte)0) < 0;
                Array.Clear(Buffer);
                IsDisposed = true;
            }
        }
    }


    //A native backing that records every region it creates plus how many times it was called, so
    //tests can inspect the raw backing bytes (to stomp a canary) and assert region-allocation counts.
    private sealed class InstrumentedBacking
    {
        private readonly List<InstrumentedRegionOwner> owners = new();

        private int callCount;

        public int CallCount => callCount;

        public List<InstrumentedRegionOwner> Owners => owners;

        public InstrumentedRegionOwner Allocate(int size)
        {
            Interlocked.Increment(ref callCount);

            var owner = new InstrumentedRegionOwner(size);

            lock(owners)
            {
                owners.Add(owner);
            }

            return owner;
        }
    }


    //Fills the rented segment with a position-dependent pattern (1..length, no zeros) and locates
    //it inside the raw backing region, so a canary can be stomped without hard-coding geometry. A
    //CONSTANT fill is ambiguous here: a random canary byte adjacent to the segment can equal the
    //marker and shift the located run by a byte, so the "canary stomp" lands on data instead — a
    //1-in-256 flake per random canary. A position-dependent pattern cannot be shifted or extended
    //that way (a shifted overlap contradicts itself byte-by-byte).
    private static int LocateSegmentData(byte[] regionBuffer, IMemoryOwner<byte> owner)
    {
        var span = owner.Memory.Span;
        for(int i = 0; i < span.Length; i++)
        {
            span[i] = (byte)((i % 255) + 1);
        }

        return regionBuffer.AsSpan().IndexOf(span);
    }


    [TestMethod]
    public void ProtectedSlabReusesOneRegionUntilCapacity()
    {
        var backing = new InstrumentedBacking();

        using var meter = new Meter("Test", "1.0.0");
        using var pool = new BaseMemoryPool(
            meter,
            capacityStrategy: _ => 4,
            nativeBacking: backing.Allocate,
            nativeRentMode: NativeRentMode.ProtectedSlab);

        const int bufferSize = 32;
        const int canarySize = 16;

        //Region length is (two 16-byte canaries + data) per segment, times slab capacity.
        int expectedRegionLength = ((canarySize * 2) + bufferSize) * 4;

        var rentals = new List<IMemoryOwner<byte>>();

        for(int i = 0; i < 4; i++)
        {
            rentals.Add(pool.Rent(bufferSize, AllocationKind.Native));
        }

        Assert.AreEqual(1, backing.CallCount, "Four rents within one slab's capacity should share a single backing region.");
        Assert.HasCount(expectedRegionLength, backing.Owners[0].Buffer,
            $"The backing region should be exactly {expectedRegionLength} bytes for capacity 4.");

        rentals.Add(pool.Rent(bufferSize, AllocationKind.Native));

        Assert.AreEqual(2, backing.CallCount, "A fifth rent beyond capacity should allocate a second backing region.");

        foreach(var rental in rentals)
        {
            rental.Dispose();
        }
    }


    [TestMethod]
    public void ProtectedSlabRentIsExactSizeAndUsable()
    {
        var backing = new InstrumentedBacking();

        using var meter = new Meter("Test", "1.0.0");
        using var pool = new BaseMemoryPool(
            meter,
            nativeBacking: backing.Allocate,
            nativeRentMode: NativeRentMode.ProtectedSlab);

        int[] sizes = [1, 16, 31, 32, 33, 64, 1024];

        foreach(int size in sizes)
        {
            using var owner = pool.Rent(size, AllocationKind.Native);
            Assert.AreEqual(size, owner.Memory.Length, $"Protected-slab rent of size {size} should return exactly {size} bytes.");

            //Fill rather than write two distinct bytes: for size 1 the first and last byte are
            //the same byte, so distinct sentinel values would overwrite each other.
            owner.Memory.Span.Fill(0xAB);

            Assert.AreEqual<byte>(0xAB, owner.Memory.Span[0], "First byte should be writable and readable.");
            Assert.AreEqual<byte>(0xAB, owner.Memory.Span[size - 1], "Last byte should be writable and readable.");
        }
    }


    [TestMethod]
    public void ProtectedSlabZeroesOnReturnAndReusesSegment()
    {
        var backing = new InstrumentedBacking();

        using var meter = new Meter("Test", "1.0.0");
        using var pool = new BaseMemoryPool(
            meter,
            capacityStrategy: _ => 1,
            nativeBacking: backing.Allocate,
            nativeRentMode: NativeRentMode.ProtectedSlab);

        var first = pool.Rent(32, AllocationKind.Native);
        first.Memory.Span.Fill(0xDE);
        first.Dispose();

        //With capacity 1, the second rent must reuse the segment just returned.
        using var second = pool.Rent(32, AllocationKind.Native);
        foreach(byte b in second.Memory.Span)
        {
            Assert.AreEqual(0, b, "Returned protected-slab memory must be zeroed for security.");
        }
    }


    [TestMethod]
    public void CanaryViolationIsDetectedZeroedAndThrown()
    {
        var backing = new InstrumentedBacking();

        using var meter = new Meter("Test", "1.0.0");
        using var pool = new BaseMemoryPool(
            meter,
            capacityStrategy: _ => 1,
            nativeBacking: backing.Allocate,
            nativeRentMode: NativeRentMode.ProtectedSlab);

        const int bufferSize = 64;

        var owner = pool.Rent(bufferSize, AllocationKind.Native);

        //Find the rented data span in the raw backing region, then stomp the first byte of the
        //trailing canary that immediately follows it.
        byte[] region = backing.Owners[0].Buffer;
        int segmentStart = LocateSegmentData(region, owner);
        Assert.IsGreaterThanOrEqualTo(0, segmentStart, "The fill pattern should be present in the backing region.");

        region[segmentStart + bufferSize] ^= 0xFF;

        Assert.ThrowsExactly<CanaryViolationException>(() => owner.Dispose());

        //Despite the violation, the data must have been zeroed on the way out.
        bool dataIsZero = region.AsSpan(segmentStart, bufferSize).IndexOfAnyExcept((byte)0) < 0;
        Assert.IsTrue(dataIsZero, "The segment data must be zeroed even when a canary violation is detected.");
    }


    [TestMethod]
    public void ViolatedSegmentIsRetiredNotRecycled()
    {
        var backing = new InstrumentedBacking();

        using var meter = new Meter("Test", "1.0.0");
        using var pool = new BaseMemoryPool(
            meter,
            capacityStrategy: _ => 1,
            nativeBacking: backing.Allocate,
            nativeRentMode: NativeRentMode.ProtectedSlab);

        const int bufferSize = 64;

        var owner = pool.Rent(bufferSize, AllocationKind.Native);

        byte[] region = backing.Owners[0].Buffer;
        int segmentStart = LocateSegmentData(region, owner);
        Assert.IsGreaterThanOrEqualTo(0, segmentStart, "The fill pattern should be present in the backing region.");

        region[segmentStart + bufferSize] ^= 0xFF;

        Assert.ThrowsExactly<CanaryViolationException>(() => owner.Dispose());

        //The 1-capacity slab now holds only a retired segment; a new rent of the same size must
        //allocate a fresh region rather than hand out the retired one.
        using var next = pool.Rent(bufferSize, AllocationKind.Native);
        Assert.AreEqual(bufferSize, next.Memory.Length, "The new rent should succeed with a fresh region.");
        Assert.AreEqual(2, backing.CallCount, "A retired segment must not be handed out again; the pool allocates a new backing region.");
    }


    [TestMethod]
    public void PoolRemainsUsableAfterViolation()
    {
        var backing = new InstrumentedBacking();

        using var meter = new Meter("Test", "1.0.0");
        using var pool = new BaseMemoryPool(
            meter,
            capacityStrategy: _ => 1,
            nativeBacking: backing.Allocate,
            nativeRentMode: NativeRentMode.ProtectedSlab);

        const int violatedSize = 64;

        var owner = pool.Rent(violatedSize, AllocationKind.Native);

        byte[] region = backing.Owners[0].Buffer;
        int segmentStart = LocateSegmentData(region, owner);
        Assert.IsGreaterThanOrEqualTo(0, segmentStart, "The fill pattern should be present in the backing region.");

        region[segmentStart + violatedSize] ^= 0xFF;

        Assert.ThrowsExactly<CanaryViolationException>(() => owner.Dispose());

        //A different size lives in its own (size, kind) bucket and must be entirely unaffected.
        const int otherSize = 128;
        using var other = pool.Rent(otherSize, AllocationKind.Native);
        other.Memory.Span.Fill(0x5A);

        Assert.AreEqual(otherSize, other.Memory.Length, "Pool must remain usable for a different size after a canary violation.");
        Assert.AreEqual<byte>(0x5A, other.Memory.Span[0]);
        Assert.AreEqual<byte>(0x5A, other.Memory.Span[otherSize - 1]);
    }


    [TestMethod]
    public void ProtectedSlabOwnerDisposeIsIdempotent()
    {
        var backing = new InstrumentedBacking();

        using var pool = new BaseMemoryPool(nativeBacking: backing.Allocate, nativeRentMode: NativeRentMode.ProtectedSlab);
        var owner = pool.Rent(32, AllocationKind.Native);

        owner.Memory.Span.Fill(0xFF);
        owner.Dispose();

        //Second dispose should not throw.
        owner.Dispose();
    }


    [TestMethod]
    public void ProtectedSlabMemoryAccessAfterDisposeThrows()
    {
        var backing = new InstrumentedBacking();

        using var pool = new BaseMemoryPool(nativeBacking: backing.Allocate, nativeRentMode: NativeRentMode.ProtectedSlab);
        var owner = pool.Rent(32, AllocationKind.Native);

        owner.Memory.Span.Fill(0xFF);
        owner.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = owner.Memory);
    }


    [TestMethod]
    public void TrimExcessReclaimsIdleNativeSlabRegions()
    {
        var backing = new InstrumentedBacking();

        using var meter = new Meter("Test", "1.0.0");
        using var pool = new BaseMemoryPool(
            meter,
            capacityStrategy: _ => 2,
            nativeBacking: backing.Allocate,
            nativeRentMode: NativeRentMode.ProtectedSlab);

        var first = pool.Rent(32, AllocationKind.Native);
        var second = pool.Rent(32, AllocationKind.Native);

        first.Dispose();
        second.Dispose();

        int reclaimed = pool.TrimExcess();
        Assert.IsGreaterThanOrEqualTo(1, reclaimed, "TrimExcess should reclaim the idle protected native slab.");

        var regionOwner = backing.Owners[0];
        Assert.IsTrue(regionOwner.IsDisposed, "The backing region owner should be disposed after TrimExcess.");
        Assert.IsTrue(regionOwner.WasZeroedAtDispose, "The whole region, canaries included, must be zeroed before the region owner is disposed.");
    }


    [TestMethod]
    public void PoolDisposeDefersRegionReleaseUntilLastRentalReturns()
    {
        var backing = new InstrumentedBacking();

        var pool = new BaseMemoryPool(nativeBacking: backing.Allocate, nativeRentMode: NativeRentMode.ProtectedSlab);
        var owner = pool.Rent(32, AllocationKind.Native);
        owner.Memory.Span.Fill(0xCC);

        //Disposing the pool while a rental is active must NOT free the backing region: unlike the
        //managed tier, whose disposed slab arrays remain valid GC objects, releasing a native
        //region under a live rental would be a use-after-free. The slab is only marked and defers
        //its release to the last outstanding return.
        pool.Dispose();

        Assert.IsFalse(backing.Owners[0].IsDisposed,
            "The backing region must stay alive while a rental is outstanding after pool disposal.");
        Assert.AreEqual<byte>(0xCC, owner.Memory.Span[0],
            "An outstanding rental must remain readable after pool disposal.");

        //The last return releases the region deterministically and must not throw.
        owner.Dispose();

        Assert.IsTrue(backing.Owners[0].IsDisposed,
            "The deferred region must be released when the last outstanding rental returns.");
        Assert.IsTrue(backing.Owners[0].WasZeroedAtDispose,
            "The deferred region must be fully zeroed before its owner is disposed.");
    }


    [TestMethod]
    public void LeadingCanaryStompIsDetected()
    {
        var backing = new InstrumentedBacking();

        using var meter = new Meter("Test", "1.0.0");
        using var pool = new BaseMemoryPool(
            meter,
            capacityStrategy: _ => 1,
            nativeBacking: backing.Allocate,
            nativeRentMode: NativeRentMode.ProtectedSlab);

        const int bufferSize = 64;

        var owner = pool.Rent(bufferSize, AllocationKind.Native);

        //The leading canary occupies the 16 bytes immediately before the data; stomp its last byte
        //(an underflow-style corruption, the mirror image of the trailing-canary test).
        byte[] region = backing.Owners[0].Buffer;
        int segmentStart = LocateSegmentData(region, owner);
        Assert.IsGreaterThanOrEqualTo(0, segmentStart, "The fill pattern should be present in the backing region.");

        region[segmentStart - 1] ^= 0xFF;

        var violation = Assert.ThrowsExactly<CanaryViolationException>(() => owner.Dispose());
        Assert.Contains("leading canary", violation.Message,
            "The violation must name the leading canary so the operator knows the overrun direction.");

        bool dataIsZero = region.AsSpan(segmentStart, bufferSize).IndexOfAnyExcept((byte)0) < 0;
        Assert.IsTrue(dataIsZero, "The segment data must be zeroed even when the leading canary is violated.");
    }


    [TestMethod]
    public void BothCanariesStompedReportsBoth()
    {
        var backing = new InstrumentedBacking();

        using var meter = new Meter("Test", "1.0.0");
        using var pool = new BaseMemoryPool(
            meter,
            capacityStrategy: _ => 1,
            nativeBacking: backing.Allocate,
            nativeRentMode: NativeRentMode.ProtectedSlab);

        const int bufferSize = 64;

        var owner = pool.Rent(bufferSize, AllocationKind.Native);

        byte[] region = backing.Owners[0].Buffer;
        int segmentStart = LocateSegmentData(region, owner);
        Assert.IsGreaterThanOrEqualTo(0, segmentStart, "The fill pattern should be present in the backing region.");

        region[segmentStart - 1] ^= 0xFF;
        region[segmentStart + bufferSize] ^= 0xFF;

        var violation = Assert.ThrowsExactly<CanaryViolationException>(() => owner.Dispose());
        Assert.Contains("leading and trailing canaries", violation.Message,
            "A double stomp must report both canaries.");
    }


    [TestMethod]
    public void TrimExcessReclaimsAllRetiredSlab()
    {
        var backing = new InstrumentedBacking();

        using var meter = new Meter("Test", "1.0.0");
        using var pool = new BaseMemoryPool(
            meter,
            capacityStrategy: _ => 1,
            nativeBacking: backing.Allocate,
            nativeRentMode: NativeRentMode.ProtectedSlab);

        const int bufferSize = 64;

        var owner = pool.Rent(bufferSize, AllocationKind.Native);

        byte[] region = backing.Owners[0].Buffer;
        int segmentStart = LocateSegmentData(region, owner);
        Assert.IsGreaterThanOrEqualTo(0, segmentStart, "The fill pattern should be present in the backing region.");

        region[segmentStart + bufferSize] ^= 0xFF;

        Assert.ThrowsExactly<CanaryViolationException>(() => owner.Dispose());

        //The 1-capacity slab now holds only a retired segment — no active rentals — so its locked
        //region must be reclaimable rather than pinned in memory forever.
        int reclaimed = pool.TrimExcess();
        Assert.IsGreaterThanOrEqualTo(1, reclaimed, "TrimExcess must reclaim a slab whose only segment is retired.");
        Assert.IsTrue(backing.Owners[0].IsDisposed, "The retired slab's backing region owner should be disposed.");
        Assert.IsTrue(backing.Owners[0].WasZeroedAtDispose, "The retired slab's region must be fully zeroed before disposal.");
    }


    [TestMethod]
    public void MisbehavingBackingIsRejectedAndDisposed()
    {
        //The exact-size seam contract is load-bearing: a region of the wrong length cannot be
        //subdivided safely, so the pool must reject it loudly and release the mis-sized owner.
        InstrumentedRegionOwner? wrongSized = null;

        using var pool = new BaseMemoryPool(
            nativeBacking: size =>
            {
                wrongSized = new InstrumentedRegionOwner(size + 1);
                return wrongSized;
            },
            nativeRentMode: NativeRentMode.ProtectedSlab);

        Assert.ThrowsExactly<InvalidOperationException>(() => pool.Rent(32, AllocationKind.Native));
        Assert.IsNotNull(wrongSized, "The backing should have been invoked.");
        Assert.IsTrue(wrongSized.IsDisposed, "The mis-sized region owner must be disposed, not leaked.");
    }


    [TestMethod]
    public void NonPositiveCapacityStrategyIsRejected()
    {
        var backing = new InstrumentedBacking();

        using var meter = new Meter("Test", "1.0.0");
        using var pool = new BaseMemoryPool(
            meter,
            capacityStrategy: _ => 0,
            nativeBacking: backing.Allocate,
            nativeRentMode: NativeRentMode.ProtectedSlab);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => pool.Rent(32, AllocationKind.Native));
        Assert.AreEqual(0, backing.CallCount, "No backing region should be allocated for a rejected capacity.");
    }


    [TestMethod]
    public void ProtectedSlabModeWithoutBackingKeepsStrictSemantics()
    {
        //Strict/degradation semantics are orthogonal to the rent mode: with no backing wired and no
        //degradation opt-in, a protected-slab pool must still fail loud rather than silently serve a
        //weaker tier.
        using var strictPool = new BaseMemoryPool(nativeRentMode: NativeRentMode.ProtectedSlab);
        Assert.ThrowsExactly<InvalidOperationException>(() => strictPool.Rent(32, AllocationKind.Native));

        using var degradingPool = new BaseMemoryPool(allowNativeDegradation: true, nativeRentMode: NativeRentMode.ProtectedSlab);
        using var owner = degradingPool.Rent(32, AllocationKind.Native);

        Assert.AreEqual(32, owner.Memory.Length, "A degraded rent falls back to Pinned and is still exact-size.");
    }


    [TestMethod]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Analyzer false positive on testRoot.")]
    public void ProtectedSlabRentTagsNativeRentMode()
    {
        //Native alone does not distinguish per-rent isolated from protected-slab; the mode must be
        //recorded on the lifecycle activity so an operator can tell them apart.
        Activity.Current = null;

        using var testRoot = new Activity(nameof(ProtectedSlabRentTagsNativeRentMode));
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

        var backing = new InstrumentedBacking();
        using var pool = new BaseMemoryPool(nativeBacking: backing.Allocate, nativeRentMode: NativeRentMode.ProtectedSlab);

        using(pool.Rent(32, AllocationKind.Native)) { }

        testRoot.Stop();

        var rentActivity = activities.FirstOrDefault(a => a.OperationName == "Rent");
        Assert.IsNotNull(rentActivity, "Should have captured the Rent lifecycle activity.");

        Assert.AreEqual("ProtectedSlab", rentActivity.GetTagItem("nativeRentMode")?.ToString(),
            "A protected-slab Native rent must record its mode on the lifecycle activity.");
        Assert.AreEqual("Native", rentActivity.GetTagItem("allocationKind")?.ToString(),
            "The effective kind must be recorded as Native.");
    }


    [TestMethod]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Analyzer false positive on testRoot.")]
    public void CanaryViolationEmitsActivityEventAndCounter()
    {
        Activity.Current = null;

        using var testRoot = new Activity(nameof(CanaryViolationEmitsActivityEventAndCounter));
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

        using var meter = new Meter(BaseMemoryPoolMetrics.MeterName, "1.0.0");
        long violationCount = 0;

        using var meterListener = new MeterListener();

        meterListener.InstrumentPublished = (instrument, listenerHandle) =>
        {
            if(instrument.Meter == meter && instrument.Name == BaseMemoryPoolMetrics.BaseMemoryPoolCanaryViolationsTotal)
            {
                listenerHandle.EnableMeasurementEvents(instrument);
            }
        };

        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            Interlocked.Add(ref violationCount, measurement);
        });

        meterListener.Start();

        var backing = new InstrumentedBacking();
        using var pool = new BaseMemoryPool(meter, nativeBacking: backing.Allocate, nativeRentMode: NativeRentMode.ProtectedSlab);

        const int bufferSize = 64;

        var owner = pool.Rent(bufferSize, AllocationKind.Native);

        byte[] region = backing.Owners[0].Buffer;
        int segmentStart = LocateSegmentData(region, owner);
        Assert.IsGreaterThanOrEqualTo(0, segmentStart, "The fill pattern should be present in the backing region.");

        region[segmentStart + bufferSize] ^= 0xFF;

        Assert.ThrowsExactly<CanaryViolationException>(() => owner.Dispose());

        testRoot.Stop();

        var rentActivity = activities.FirstOrDefault(a => a.OperationName == "Rent");
        Assert.IsNotNull(rentActivity, "Should have captured the Rent lifecycle activity.");

        bool hasViolationEvent = rentActivity.Events.Any(e => e.Name == "CanaryViolation");
        Assert.IsTrue(hasViolationEvent, "A canary-violating dispose must add a CanaryViolation event to the rental activity.");

        Assert.AreEqual(1, violationCount, "The canary-violations counter must observe exactly one violation.");
    }
}
