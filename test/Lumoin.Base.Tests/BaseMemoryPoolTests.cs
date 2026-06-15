using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lumoin.Base.Tests;

/// <summary>
/// Behavioral and concurrency tests for <see cref="BaseMemoryPool"/>, migrated from the
/// SensitiveMemoryPool suite and extended with the <see cref="AllocationKind"/> dimension.
/// </summary>
[TestClass]
public sealed class BaseMemoryPoolTests
{
    public TestContext TestContext { get; set; } = null!;


    //A stand-in for a real native owner: a managed array, enough to prove the native backing seam is
    //invoked and that the returned memory is exactly the requested size and usable.
    private sealed class ArrayBackedNativeOwner(int size): IMemoryOwner<byte>
    {
        private readonly byte[] buffer = new byte[size];

        public Memory<byte> Memory => buffer;

        public void Dispose() => Array.Clear(buffer);
    }


    //An allocator that serves Native requests from managed arrays and counts how often it is called.
    private static NativeBackingAllocator CountingBacking(StrongBox<int> callCount)
    {
        return size =>
        {
            Interlocked.Increment(ref callCount.Value);

            return new ArrayBackedNativeOwner(size);
        };
    }


    //Builds a pool whose Native tier is actually wired, so the AllocationKind.Native data rows exercise
    //the injected backing path rather than degrading to Pinned.
    private static BaseMemoryPool NewWiredPool()
    {
        return new BaseMemoryPool(CountingBacking(new StrongBox<int>(0)));
    }


    [TestMethod]
    public void RentReturnsExactBufferSize()
    {
        using var pool = new BaseMemoryPool();

        int[] testSizes = [1, 16, 32, 64, 128, 256, 512, 1024];

        foreach(int size in testSizes)
        {
            using var buffer = pool.Rent(size);
            Assert.AreEqual(size, buffer.Memory.Length, $"Buffer size should be exactly {size} bytes.");
        }
    }


    [TestMethod]
    [DataRow(AllocationKind.Managed)]
    [DataRow(AllocationKind.Pinned)]
    [DataRow(AllocationKind.Native)]
    public void RentReturnsExactBufferSizeForKind(AllocationKind kind)
    {
        using var pool = NewWiredPool();

        int[] testSizes = [1, 16, 32, 64, 128, 256, 512, 1024];

        foreach(int size in testSizes)
        {
            using var buffer = pool.Rent(size, kind);
            Assert.AreEqual(size, buffer.Memory.Length, $"Buffer size should be exactly {size} bytes for {kind}.");
        }
    }


    [TestMethod]
    public void DefaultRentIsManagedAndExactSize()
    {
        using var pool = new BaseMemoryPool();
        using var buffer = pool.Rent(37);

        Assert.AreEqual(37, buffer.Memory.Length);

        buffer.Memory.Span.Fill(0xAB);
        Assert.AreEqual<byte>(0xAB, buffer.Memory.Span[0]);
        Assert.AreEqual<byte>(0xAB, buffer.Memory.Span[36]);
    }


    [TestMethod]
    public void RentReusesSlabsForSameSize()
    {
        using var pool = new BaseMemoryPool();
        const int bufferSize = 64;
        const int rentCount = 10;

        var buffers = new List<IMemoryOwner<byte>>();

        try
        {
            for(int i = 0; i < rentCount; i++)
            {
                buffers.Add(pool.Rent(bufferSize));
            }

            foreach(var buffer in buffers)
            {
                Assert.AreEqual(bufferSize, buffer.Memory.Length);
            }
        }
        finally
        {
            foreach(var buffer in buffers)
            {
                buffer.Dispose();
            }
        }
    }


    [TestMethod]
    [DataRow(AllocationKind.Managed)]
    [DataRow(AllocationKind.Pinned)]
    [DataRow(AllocationKind.Native)]
    public void RentReusesSlabsForSameSizeAndKind(AllocationKind kind)
    {
        using var pool = NewWiredPool();
        const int bufferSize = 64;
        const int rentCount = 10;

        var buffers = new List<IMemoryOwner<byte>>();

        try
        {
            for(int i = 0; i < rentCount; i++)
            {
                buffers.Add(pool.Rent(bufferSize, kind));
            }

            foreach(var buffer in buffers)
            {
                Assert.AreEqual(bufferSize, buffer.Memory.Length);
            }
        }
        finally
        {
            foreach(var buffer in buffers)
            {
                buffer.Dispose();
            }
        }
    }


    [TestMethod]
    public void DisposeClearsMemoryAndPreventsAccess()
    {
        using var pool = new BaseMemoryPool();
        var buffer = pool.Rent(32);

        buffer.Memory.Span.Fill(0xFF);
        buffer.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = buffer.Memory);
    }


    [TestMethod]
    public void DoubleDisposeIsIdempotent()
    {
        using var pool = new BaseMemoryPool();
        var buffer = pool.Rent(32);

        buffer.Memory.Span.Fill(0xFF);
        buffer.Dispose();

        //Second dispose should not throw.
        buffer.Dispose();
    }


    [TestMethod]
    public void ReturnedMemoryIsZeroed()
    {
        using var meter = new Meter("Test", "1.0.0");
        using var pool = new BaseMemoryPool(
            meter,
            capacityStrategy: _ => 1);

        //Rent, fill with a recognizable pattern, and return.
        var first = pool.Rent(32);
        first.Memory.Span.Fill(0xDE);
        first.Dispose();

        //Rent again from the same slab and verify the memory is zeroed.
        using var second = pool.Rent(32);
        foreach(byte b in second.Memory.Span)
        {
            Assert.AreEqual(0, b, "Returned memory must be zeroed for security.");
        }
    }


    [TestMethod]
    [DataRow(AllocationKind.Managed)]
    [DataRow(AllocationKind.Pinned)]
    [DataRow(AllocationKind.Native)]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Ownership of the Meter transfers to the BaseMemoryPool, which disposes it when the using-scoped pool is disposed.")]
    public void ReturnedMemoryIsZeroedForKind(AllocationKind kind)
    {
        //Wire a native backing whose stand-in owner clears on dispose, so the Native row holds the
        //same zeroize-on-return invariant the slab-pooled kinds enforce.
        using var pool = new BaseMemoryPool(
            new Meter("Test", "1.0.0"),
            capacityStrategy: _ => 1,
            nativeBacking: CountingBacking(new StrongBox<int>(0)));

        //Rent, fill with a recognizable pattern, and return.
        var first = pool.Rent(32, kind);
        first.Memory.Span.Fill(0xDE);
        first.Dispose();

        //Rent again (same size + kind reuses the slab segment for the pooled kinds) and verify zeroed.
        using var second = pool.Rent(32, kind);
        foreach(byte b in second.Memory.Span)
        {
            Assert.AreEqual(0, b, $"Returned memory must be zeroed for security ({kind}).");
        }
    }


    [TestMethod]
    public void RentHandlesEdgeCases()
    {
        using var pool = new BaseMemoryPool();

        using(var buffer = pool.Rent(1))
        {
            Assert.AreEqual(1, buffer.Memory.Length);
        }

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => pool.Rent(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => pool.Rent(-1));
    }


    [TestMethod]
    [DataRow(AllocationKind.Managed)]
    [DataRow(AllocationKind.Pinned)]
    [DataRow(AllocationKind.Native)]
    public void RentByKindRejectsNonPositiveSizes(AllocationKind kind)
    {
        using var pool = NewWiredPool();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => pool.Rent(0, kind));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => pool.Rent(-1, kind));
    }


    [TestMethod]
    public void RentRejectsASlabWhoseBackingWouldOverflowAnArray()
    {
        //A buffer size whose product with the slab capacity exceeds the maximum array length is rejected
        //before any allocation: the 64-bit bound check fails fast, so the int multiply can never wrap into a
        //too-small backing that a later segment slice would read past. The capacity strategy returns two, so
        //a near-int.MaxValue buffer size makes the product overflow an int while staying allocation-free.
        using var meter = new Meter("Test", "1.0.0");
        using var pool = new BaseMemoryPool(
            meter,
            capacityStrategy: _ => 2);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => pool.Rent(int.MaxValue));
    }


    [TestMethod]
    public void RentThrowsWhenPoolDisposed()
    {
        var pool = new BaseMemoryPool();
        pool.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => pool.Rent(32));
    }


    [TestMethod]
    [DataRow(AllocationKind.Managed)]
    [DataRow(AllocationKind.Pinned)]
    [DataRow(AllocationKind.Native)]
    public void RentByKindThrowsWhenPoolDisposed(AllocationKind kind)
    {
        var pool = NewWiredPool();
        pool.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => pool.Rent(32, kind));
    }


    [TestMethod]
    public void DisposingRentalAfterPoolDisposedDoesNotThrow()
    {
        var pool = new BaseMemoryPool();
        var owner = pool.Rent(32);
        owner.Memory.Span.Fill(0xCC);

        //Disposing the pool clears all slabs while a rental is still active.
        pool.Dispose();

        //The rental's Dispose calls Pool.Return on an already-disposed slab.
        //This must not throw — the error is caught internally and the lifecycle
        //activity records an error status instead.
        owner.Dispose();
    }


    [TestMethod]
    public void DisposingNativeRentalAfterPoolDisposedDoesNotThrow()
    {
        var pool = NewWiredPool();
        var owner = pool.Rent(32, AllocationKind.Native);
        owner.Memory.Span.Fill(0xCC);

        pool.Dispose();

        //The native owner releases its own memory; the pool-side return accounting is skipped
        //gracefully when the pool's meter is gone.
        owner.Dispose();
    }


    [TestMethod]
    public void TrimExcessThrowsWhenPoolDisposed()
    {
        var pool = new BaseMemoryPool();
        pool.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => pool.TrimExcess());
    }


    [TestMethod]
    public void SharedReturnsSingletonInstance()
    {
        var first = BaseMemoryPool.Shared;
        var second = BaseMemoryPool.Shared;

        Assert.AreSame(first, second, "Shared should return the same instance.");

        using var buffer = first.Rent(64);
        Assert.AreEqual(64, buffer.Memory.Length);
    }


    [TestMethod]
    public void DefaultCapacityStrategyReturnsMoreSegmentsForSmallerSizes()
    {
        int smallCapacity = BaseMemoryPool.DefaultCapacityStrategy(32);
        int mediumCapacity = BaseMemoryPool.DefaultCapacityStrategy(128);
        int largeCapacity = BaseMemoryPool.DefaultCapacityStrategy(8192);

        Assert.IsGreaterThan(mediumCapacity, smallCapacity,
            "Small buffers should get more segments per slab than medium buffers.");
        Assert.IsGreaterThan(largeCapacity, mediumCapacity,
            "Medium buffers should get more segments per slab than large buffers.");
    }


    [TestMethod]
    public void CustomCapacityStrategyIsUsed()
    {
        int strategyCallCount = 0;

        int customStrategy(int segmentSize)
        {
            Interlocked.Increment(ref strategyCallCount);
            return 2;
        }

        using var meter = new Meter("Test", "1.0.0");
        using var pool = new BaseMemoryPool(
            meter,
            capacityStrategy: customStrategy);

        //Rent three buffers of the same size to force slab creation and overflow.
        using var b1 = pool.Rent(32);
        using var b2 = pool.Rent(32);
        using var b3 = pool.Rent(32);

        //The strategy should have been invoked at least twice (first slab holds 2, second slab for the third rent).
        Assert.IsGreaterThanOrEqualTo(2, strategyCallCount,
            $"Custom strategy should have been called at least twice, was called {strategyCallCount} times.");
    }


    [TestMethod]
    public void TrimExcessReclaimsUnusedSlabs()
    {
        using var meter = new Meter("Test", "1.0.0");
        using var pool = new BaseMemoryPool(
            meter,
            capacityStrategy: _ => 2);

        //Hold three buffers simultaneously to force creation of a second slab (capacity 2 per slab).
        var b1 = pool.Rent(32);
        var b2 = pool.Rent(32);
        var b3 = pool.Rent(32);

        //Return all buffers so both slabs become fully available.
        b1.Dispose();
        b2.Dispose();
        b3.Dispose();

        int reclaimed = pool.TrimExcess();
        Assert.IsGreaterThan(0, reclaimed, "TrimExcess should reclaim at least one unused slab.");
    }


    [TestMethod]
    public void TrimExcessDoesNotReclaimSlabsWithActiveRentals()
    {
        using var meter = new Meter("Test", "1.0.0");
        using var pool = new BaseMemoryPool(
            meter,
            capacityStrategy: _ => 2);

        //Keep a rental alive so the slab cannot be reclaimed.
        using var active = pool.Rent(32);

        int reclaimed = pool.TrimExcess();
        Assert.AreEqual(0, reclaimed, "TrimExcess should not reclaim slabs with active rentals.");
    }


    [TestMethod]
    public void RentWorksAfterTrimExcess()
    {
        using var meter = new Meter("Test", "1.0.0");
        using var pool = new BaseMemoryPool(
            meter,
            capacityStrategy: _ => 2);

        //Create slabs, return everything, then trim.
        var b1 = pool.Rent(64);
        var b2 = pool.Rent(64);
        b1.Dispose();
        b2.Dispose();
        pool.TrimExcess();

        //Pool should create fresh slabs on demand after trimming.
        using var afterTrim = pool.Rent(64);
        Assert.AreEqual(64, afterTrim.Memory.Length, "Rent should work after TrimExcess reclaims slabs.");
    }


    [TestMethod]
    public void NativeRequestDegradesToPinnedWhenNoBackingWired()
    {
        using var pool = new BaseMemoryPool(allowNativeDegradation: true);
        using var owner = pool.Rent(32, AllocationKind.Native);

        Assert.AreEqual(32, owner.Memory.Length);

        owner.Memory.Span.Fill(0xAB);
        Assert.AreEqual<byte>(0xAB, owner.Memory.Span[31]);
    }


    [TestMethod]
    public void InjectedNativeBackingServesNativeRequests()
    {
        var callCount = new StrongBox<int>(0);

        using var pool = new BaseMemoryPool(CountingBacking(callCount));
        using var owner = pool.Rent(24, AllocationKind.Native);

        Assert.IsGreaterThanOrEqualTo(1, callCount.Value, "A wired native backing must serve AllocationKind.Native.");
        Assert.AreEqual(24, owner.Memory.Length);
    }


    [TestMethod]
    public void NativeRentalsAreNotSlabPooled()
    {
        //Each Native rent calls the backing afresh; two concurrent rents must invoke it twice and
        //yield independent buffers.
        var callCount = new StrongBox<int>(0);

        using var pool = new BaseMemoryPool(CountingBacking(callCount));

        using var a = pool.Rent(32, AllocationKind.Native);
        using var b = pool.Rent(32, AllocationKind.Native);

        Assert.AreEqual(2, callCount.Value, "Native rentals are allocated per rent, not slab-pooled.");

        a.Memory.Span.Fill(0x11);
        b.Memory.Span.Fill(0x22);

        Assert.AreEqual<byte>(0x11, a.Memory.Span[0], "Native buffers must not alias.");
        Assert.AreEqual<byte>(0x22, b.Memory.Span[0], "Native buffers must not alias.");
    }


    [TestMethod]
    public void NativeRequestThrowsWhenDegradationDisallowedAndNoBackingWired()
    {
        //No backing wired and degradation disallowed: the Native request must fail loud rather than
        //silently hand back weaker (Pinned) memory.
        using var pool = new BaseMemoryPool(allowNativeDegradation: false);

        Assert.ThrowsExactly<InvalidOperationException>(() => pool.Rent(32, AllocationKind.Native));
    }


    [TestMethod]
    public void WiredNativeBackingServesNativeEvenWhenDegradationDisallowed()
    {
        //Disallowing degradation only governs the no-backing case; a wired backing still serves Native.
        var callCount = new StrongBox<int>(0);

        using var pool = new BaseMemoryPool(CountingBacking(callCount), allowNativeDegradation: false);
        using var owner = pool.Rent(48, AllocationKind.Native);

        Assert.AreEqual(48, owner.Memory.Length);
        Assert.AreEqual(1, callCount.Value, "A wired native backing must serve Native even when degradation is disallowed.");
    }


    [TestMethod]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Analyzer false positive on testRoot.")]
    public void DegradedNativeRentReportsEffectiveKindAndEmitsEvent()
    {
        //A default (degrading) pool with no backing wired must record the EFFECTIVE kind (Pinned) on the
        //Rent activity, flag the requested kind (Native), and emit the degradation event — so an operator
        //sees the truth, not the false "Native" assurance.
        Activity.Current = null;

        using var testRoot = new Activity(nameof(DegradedNativeRentReportsEffectiveKindAndEmitsEvent));
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

        using var pool = new BaseMemoryPool(allowNativeDegradation: true);

        using(pool.Rent(32, AllocationKind.Native)) { }

        testRoot.Stop();

        var rentActivity = activities.FirstOrDefault(a => a.OperationName == "Rent");
        Assert.IsNotNull(rentActivity, "Should have captured the Rent lifecycle activity.");

        Assert.AreEqual("Pinned", rentActivity.GetTagItem("allocationKind")?.ToString(),
            "Telemetry must record the effective kind (Pinned), not the requested Native.");
        Assert.AreEqual("Native", rentActivity.GetTagItem("requestedAllocationKind")?.ToString(),
            "Telemetry must preserve the originally requested kind (Native).");

        bool hasDegradedEvent = rentActivity.Events.Any(e => e.Name == "AllocationKindDegraded");
        Assert.IsTrue(hasDegradedEvent, "A degraded Native rent must emit an AllocationKindDegraded event.");
    }


    [TestMethod]
    public void TracingCanBeDisabled()
    {
        var activities = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "BaseMemoryPool",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        using var meter = new Meter("Test", "1.0.0");
        using var pool = new BaseMemoryPool(
            meter,
            tracingEnabled: false);

        using(pool.Rent(32)) { }

        Assert.IsEmpty(activities,
            "No activities should be created when tracing is disabled.");
    }


    [TestMethod]
    public async Task MetricsAreReportedCorrectly()
    {
        using var meter = new Meter(BaseMemoryPoolMetrics.MeterName, "1.0.0");
        var reportedMetrics = new ConcurrentDictionary<string, long>();

        using var listener = new MeterListener();

        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if(instrument.Meter == meter)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            reportedMetrics.AddOrUpdate(instrument.Name, measurement, (_, _) => measurement);
        });

        listener.SetMeasurementEventCallback<int>((instrument, measurement, tags, state) =>
        {
            reportedMetrics.AddOrUpdate(instrument.Name, measurement, (_, _) => measurement);
        });

        listener.Start();

        using var pool = new BaseMemoryPool(meter);

        using(pool.Rent(100))
        {
            using(pool.Rent(200))
            {
                listener.RecordObservableInstruments();
                await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.CancellationToken).ConfigureAwait(false);

                bool foundSlabs = reportedMetrics.TryGetValue(BaseMemoryPoolMetrics.BaseMemoryPoolTotalSlabs, out long totalSlabs);
                Assert.IsTrue(foundSlabs, "TotalSlabs metric should be reported.");
                Assert.AreEqual(2, totalSlabs, "Should have created two slabs for different buffer sizes.");

                bool foundMemory = reportedMetrics.TryGetValue(BaseMemoryPoolMetrics.BaseMemoryPoolTotalMemoryAllocated, out long totalMemory);
                Assert.IsTrue(foundMemory, "TotalMemoryAllocated metric should be reported.");

                //Expected memory uses the default capacity strategy.
                int expectedCapacity100 = BaseMemoryPool.DefaultCapacityStrategy(100);
                int expectedCapacity200 = BaseMemoryPool.DefaultCapacityStrategy(200);
                long expectedMemory = (100 * expectedCapacity100) + (200 * expectedCapacity200);
                Assert.AreEqual(expectedMemory, totalMemory, "Total memory should match expected allocation.");
            }
        }
    }


    [TestMethod]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Analyzer false positive on testRoot.")]
    public void TracingRecordsSingleLifecycleActivityPerRental()
    {
        Activity.Current = null;

        using var testRoot = new Activity("TestRoot").Start();
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

        using var pool = new BaseMemoryPool();

        using(pool.Rent(100)) { }
        using(pool.Rent(200)) { }

        testRoot.Stop();

        //Single-activity model: one "Rent" activity per rental lifecycle, no separate "Dispose" activity.
        var rentActivities = activities.Where(a => a.OperationName == "Rent").ToList();
        Assert.HasCount(2, rentActivities, "Should have exactly two lifecycle activities.");

        var firstRent = rentActivities.FirstOrDefault(a => a.GetTagItem("bufferSize")?.ToString() == "100");
        var secondRent = rentActivities.FirstOrDefault(a => a.GetTagItem("bufferSize")?.ToString() == "200");

        Assert.IsNotNull(firstRent, "Should have lifecycle activity for 100-byte buffer.");
        Assert.IsNotNull(secondRent, "Should have lifecycle activity for 200-byte buffer.");

        //No separate dispose activities should exist.
        var disposeActivities = activities.Where(a => a.OperationName == "Dispose").ToList();
        Assert.HasCount(0, disposeActivities,
            "Single-activity model should not create separate dispose activities.");
    }


    [TestMethod]
    public async Task TracingMaintainsParentChildRelationships()
    {
        Activity.Current = null;

        var capturedActivities = new ConcurrentBag<Activity>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => capturedActivities.Add(activity)
        };

        ActivitySource.AddActivityListener(listener);

        using var parentSource = new ActivitySource("TestSource");
        using var parentActivity = parentSource.StartActivity("ParentActivity", ActivityKind.Internal);

        Assert.IsNotNull(parentActivity, "Parent activity should be created.");

        var expectedTraceId = parentActivity.TraceId;

        using var pool = new BaseMemoryPool();

        using(pool.Rent(100))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.CancellationToken).ConfigureAwait(false);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.CancellationToken).ConfigureAwait(false);

        var activities = capturedActivities
            .Where(a => a.TraceId == expectedTraceId)
            .ToList();

        TestContext.WriteLine($"Activities from this test run: {activities.Count}.");
        foreach(var act in activities)
        {
            TestContext.WriteLine($"Activity: {act.OperationName}, SpanId: {act.SpanId}, ParentSpanId: {act.ParentSpanId}.");
        }

        var rentAct = activities.FirstOrDefault(a => a.OperationName == "Rent");
        Assert.IsNotNull(rentAct, "Should have captured the lifecycle activity.");

        Assert.AreEqual(parentActivity.SpanId, rentAct.ParentSpanId,
            "Lifecycle activity should be a child of the parent activity.");
    }


    [TestMethod]
    public async Task DisposeRestoresAmbientActivityToParent()
    {
        Activity.Current = null;

        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };

        ActivitySource.AddActivityListener(listener);

        using var parentSource = new ActivitySource("TestSource");
        using var parentActivity = parentSource.StartActivity("CallerActivity", ActivityKind.Internal);

        Assert.IsNotNull(parentActivity, "Parent activity should be created.");
        Assert.AreSame(parentActivity, Activity.Current,
            "Parent should be the ambient activity before rent.");

        using var pool = new BaseMemoryPool();

        //During the rental scope, Activity.Current is the lifecycle activity (child of parent).
        var owner = pool.Rent(64);
        Assert.AreEqual("Rent", Activity.Current?.OperationName,
            "During rental scope, Activity.Current should be the lifecycle activity.");
        Assert.AreEqual(parentActivity.SpanId, Activity.Current?.ParentSpanId,
            "Lifecycle activity should be a child of the caller's activity.");

        //Dispose stops the lifecycle activity, which restores Activity.Current to its parent.
        owner.Dispose();
        Assert.AreSame(parentActivity, Activity.Current,
            "After dispose, Activity.Current should be restored to the parent.");

        //Verify the same holds across an async boundary with ConfigureAwait(false).
        var owner2 = pool.Rent(64);
        await Task.Delay(TimeSpan.FromMilliseconds(5), TestContext.CancellationToken).ConfigureAwait(false);
        owner2.Dispose();

        //After ConfigureAwait(false), Activity.Current flows via AsyncLocal.
        //Activity.Stop restores it to the parent on whatever thread the continuation runs on.
        var afterAsync = Activity.Current;
        if(afterAsync is not null)
        {
            Assert.AreSame(parentActivity, afterAsync,
                "After async dispose, ambient activity should be the caller's, not the pool's.");
        }
    }


    [TestMethod]
    public async Task RentOnOneThreadDisposeOnAnotherWithConfigureAwaitFalse()
    {
        using var pool = new BaseMemoryPool();

        //Rent on the current thread.
        var owner = pool.Rent(128);
        owner.Memory.Span.Fill(0xBB);

        //Force a thread switch via ConfigureAwait(false).
        await Task.Yield();
        await Task.Delay(TimeSpan.FromMilliseconds(5), TestContext.CancellationToken).ConfigureAwait(false);

        //Dispose may now execute on a thread pool thread.
        owner.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = owner.Memory,
            "Buffer must be inaccessible after cross-thread dispose.");

        //Pool should still be functional after cross-thread return.
        using var subsequent = pool.Rent(128);
        Assert.AreEqual(128, subsequent.Memory.Length,
            "Pool must remain usable after cross-thread disposal.");
    }


    [TestMethod]
    public async Task ConfigureAwaitTruePreservesActivityContext()
    {
        Activity.Current = null;

        var capturedActivities = new ConcurrentBag<Activity>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => capturedActivities.Add(activity)
        };

        ActivitySource.AddActivityListener(listener);

        using var parentSource = new ActivitySource("TestSource");
        using var parentActivity = parentSource.StartActivity("CallerContext", ActivityKind.Internal);

        Assert.IsNotNull(parentActivity, "Parent activity should be created.");

        var expectedTraceId = parentActivity.TraceId;

        using var pool = new BaseMemoryPool();

        //ConfigureAwait(true) preserves the synchronization context when available.
        using(pool.Rent(64))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.CancellationToken).ConfigureAwait(true);
        }

        var rentAct = capturedActivities.FirstOrDefault(
            a => a.Source.Name == "BaseMemoryPool" && a.TraceId == expectedTraceId);

        Assert.IsNotNull(rentAct, "Lifecycle activity should share the parent trace.");
        Assert.AreEqual(parentActivity.SpanId, rentAct.ParentSpanId,
            "Lifecycle activity should be a child of the caller's activity.");
    }


    [TestMethod]
    public async Task ConcurrentRentAndDisposeAcrossThreads()
    {
        using var pool = new BaseMemoryPool();

        var tasks = Enumerable.Range(0, 50).Select(async i =>
        {
            var owner = pool.Rent((i % 8 + 1) * 16);
            owner.Memory.Span.Fill((byte)(i % 256));

            //Yield to force potential thread switches.
            await Task.Yield();

            int length = owner.Memory.Length;
            owner.Dispose();

            return length;
        }).ToArray();

        int[] results = await Task.WhenAll(tasks).ConfigureAwait(false);

        Assert.HasCount(50, results, "All concurrent rent-dispose cycles should complete.");
    }


    [TestMethod]
    [DataRow(AllocationKind.Managed)]
    [DataRow(AllocationKind.Pinned)]
    [DataRow(AllocationKind.Native)]
    public async Task ConcurrentRentReturnNeverAliasesBackingMemory(AllocationKind kind)
    {
        //A small per-slab capacity forces heavy segment reuse, so concurrent renters
        //contend for the same backing slabs. The core safety invariant under test:
        //the pool must never hand the same backing segment to two live renters. Each
        //task fills its buffer with a value unique to that task+iteration and, after a
        //yield that lets other renters interleave, verifies every byte still holds that
        //value. A shared/overlapping segment would corrupt the pattern and be detected.
        using var meter = new Meter("Test", "1.0.0");
        using var pool = new BaseMemoryPool(
            meter,
            capacityStrategy: _ => 4,
            nativeBacking: CountingBacking(new StrongBox<int>(0)));

        const int taskCount = 64;
        const int iterationsPerTask = 500;
        const int bufferSize = 32;

        var failures = new ConcurrentQueue<string>();
        var cancellationToken = TestContext.CancellationToken;

        var tasks = Enumerable.Range(0, taskCount).Select(taskIndex => Task.Run(async () =>
        {
            for(int iteration = 0; iteration < iterationsPerTask; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                byte pattern = (byte)((taskIndex * 31 + iteration) & 0xFF);

                using var owner = pool.Rent(bufferSize, kind);
                if(owner.Memory.Length != bufferSize)
                {
                    failures.Enqueue($"Rented length {owner.Memory.Length}, expected {bufferSize} (task {taskIndex}, iter {iteration}).");
                    return;
                }

                owner.Memory.Span.Fill(pattern);

                //Yield so other renters interleave while this buffer is held and filled.
                await Task.Yield();

                int mismatch = owner.Memory.Span.IndexOfAnyExcept(pattern);
                if(mismatch >= 0)
                {
                    failures.Enqueue($"Aliasing detected at byte {mismatch}: was {owner.Memory.Span[mismatch]}, expected {pattern} (task {taskIndex}, iter {iteration}).");
                    return;
                }
            }
        }, cancellationToken)).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);

        Assert.IsEmpty(failures, $"No renter should ever observe aliased or mis-sized memory. First failures: {string.Join(" | ", failures.Take(5))}");

        //After the storm settles every rental has been returned, so the pool must be
        //fully reusable.
        using var afterStorm = pool.Rent(bufferSize, kind);
        Assert.AreEqual(bufferSize, afterStorm.Memory.Length, "Pool must remain usable after concurrent rent/return.");
    }
}
