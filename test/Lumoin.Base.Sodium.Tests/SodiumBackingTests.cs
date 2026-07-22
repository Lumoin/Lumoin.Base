using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lumoin.Base.Sodium.Tests;

/// <summary>
/// Behavioral tests for <see cref="SodiumBacking"/>: argument validation that holds regardless of
/// whether libsodium is present, and — gated behind <see cref="SodiumTestEnvironment.RequireSodium"/>
/// — allocation, disposal, pinning, and <see cref="BaseMemoryPool"/> integration against the real
/// native library.
/// </summary>
[TestClass]
public sealed class SodiumBackingTests
{
    public TestContext TestContext { get; set; } = null!;


    [TestMethod]
    public void AllocateRejectsNonPositiveSizes()
    {
        //Argument validation precedes the availability check (see SodiumBacking.Allocate), so this
        //holds on hosts without libsodium and runs unconditionally.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SodiumBacking.Allocate(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SodiumBacking.Allocate(-1));
    }


    [TestMethod]
    public void AllocateAndAvailabilityAgree()
    {
        //The two branches partition every host, so this test always exercises real behavior — it is
        //the one guaranteed to run unconditionally even on a bare CI machine without libsodium.
        if(SodiumBacking.IsAvailable)
        {
            using var owner = SodiumBacking.Allocate(16);
            Assert.HasCount(16, owner.Memory, "Allocate should return exactly the requested size.");
        }
        else
        {
            //The probe's failure reason is the operator's only diagnostic when a host is
            //misconfigured, so the message must stay actionable, not just the right type.
            var exception = Assert.ThrowsExactly<InvalidOperationException>(() => SodiumBacking.Allocate(16));
            Assert.Contains("libsodium", exception.Message,
                "The unavailability exception must carry the probe's failure reason.");
        }
    }


    [TestMethod]
    public void AllocateReturnsExactSizeAndUsableMemory()
    {
        SodiumTestEnvironment.RequireSodium();

        int[] sizes = [1, 16, 32, 64, 128, 256, 512, 1024, 4096, 5000];

        foreach(int size in sizes)
        {
            using var owner = SodiumBacking.Allocate(size);
            Assert.HasCount(size, owner.Memory, $"Allocate({size}) should return exactly {size} bytes.");

            owner.Memory.Span.Fill(0xAB);
            Assert.AreEqual<byte>(0xAB, owner.Memory.Span[0], "First byte should be writable and readable.");
            Assert.AreEqual<byte>(0xAB, owner.Memory.Span[size - 1], "Last byte should be writable and readable.");
        }
    }


    [TestMethod]
    public void AllocationsAreIndependent()
    {
        SodiumTestEnvironment.RequireSodium();

        //Mirrors NativeRentalsAreNotSlabPooled in BaseMemoryPoolTests: two live guarded allocations of
        //the same size must be backed by distinct memory, never aliasing.
        using var a = SodiumBacking.Allocate(32);
        using var b = SodiumBacking.Allocate(32);

        a.Memory.Span.Fill(0x11);
        b.Memory.Span.Fill(0x22);

        Assert.AreEqual<byte>(0x11, a.Memory.Span[0], "Independent allocations must not alias.");
        Assert.AreEqual<byte>(0x22, b.Memory.Span[0], "Independent allocations must not alias.");
    }


    [TestMethod]
    public void DisposeIsIdempotent()
    {
        SodiumTestEnvironment.RequireSodium();

        var owner = SodiumBacking.Allocate(32);
        owner.Memory.Span.Fill(0xFF);

        owner.Dispose();

        //Second dispose should not throw.
        owner.Dispose();
    }


    [TestMethod]
    public void MemoryAccessAfterDisposeThrows()
    {
        SodiumTestEnvironment.RequireSodium();

        var owner = SodiumBacking.Allocate(32);
        owner.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = owner.Memory);

        //Pin carries its own dispose check, separate from the GetSpan path that Memory exercises;
        //a disposed owner must refuse to hand out a handle to freed native memory.
        var manager = (MemoryManager<byte>)owner;
        Assert.ThrowsExactly<ObjectDisposedException>(() => manager.Pin());
    }


    [TestMethod]
    public void PinBoundsAreValidated()
    {
        SodiumTestEnvironment.RequireSodium();

        //Pin computes an address into guarded native memory with plain pointer arithmetic, so its
        //bounds contract is safety-relevant: negative and beyond-length offsets are rejected, and
        //the documented boundary elementIndex == length is allowed (an empty tail slice pins at
        //the end without dereferencing it).
        using var owner = SodiumBacking.Allocate(64);
        var manager = (MemoryManager<byte>)owner;

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => manager.Pin(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => manager.Pin(65));

        using(manager.Pin(0)) { }
        using(manager.Pin(64)) { }
    }


    [TestMethod]
    public async Task ConcurrentDisposeFreesExactlyOnce()
    {
        SodiumTestEnvironment.RequireSodium();

        //The owner's contract says concurrent disposals free exactly once (the pointer is claimed
        //by atomic exchange). Race several disposers through a start gate; the signal is the
        //absence of a double-free — libsodium aborts the process or corrupts its allocator state
        //on one — and the owner being properly disposed afterwards.
        var owner = SodiumBacking.Allocate(64);
        owner.Memory.Span.Fill(0xC3);

        using var startGate = new ManualResetEventSlim(false);
        var disposers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            startGate.Wait(TestContext.CancellationToken);
            owner.Dispose();
        }, TestContext.CancellationToken)).ToArray();

        startGate.Set();
        await Task.WhenAll(disposers).ConfigureAwait(false);

        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = owner.Memory,
            "The owner must be disposed after the concurrent disposal storm.");
    }


    [TestMethod]
    public unsafe void PinnedHandleAddressesTheAllocation()
    {
        SodiumTestEnvironment.RequireSodium();

        using var owner = SodiumBacking.Allocate(64);
        var memory = owner.Memory;
        using var handle = memory.Pin();

        byte* pointer = (byte*)handle.Pointer;
        for(int i = 0; i < 64; i++)
        {
            pointer[i] = (byte)i;
        }

        for(int i = 0; i < 64; i++)
        {
            Assert.AreEqual((byte)i, memory.Span[i], $"Byte {i} written through the pinned handle should be visible via Memory.Span.");
        }
    }


    [TestMethod]
    public void StrictPoolServesNativeRentsThroughSodium()
    {
        SodiumTestEnvironment.RequireSodium();

        //Strict: no allowNativeDegradation. A sodium-wired backing must serve Native directly rather
        //than degrading, matching WiredNativeBackingServesNativeEvenWhenDegradationDisallowed in
        //BaseMemoryPoolTests.
        using var pool = new BaseMemoryPool(nativeBacking: SodiumBacking.Allocate);
        using var owner = pool.Rent(32, AllocationKind.Native);

        Assert.HasCount(32, owner.Memory, "Rent should return exactly the requested size.");

        owner.Memory.Span.Fill(0x5A);
        Assert.AreEqual<byte>(0x5A, owner.Memory.Span[0]);
        Assert.AreEqual<byte>(0x5A, owner.Memory.Span[31]);
    }


    [TestMethod]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Analyzer false positive on testRoot.")]
    public void NativeRentEmitsNoDegradationTelemetry()
    {
        SodiumTestEnvironment.RequireSodium();

        //Mirrors DegradedNativeRentReportsEffectiveKindAndEmitsEvent in BaseMemoryPoolTests, but for
        //the non-degraded path: a sodium-wired strict pool serves Native directly, so the Rent activity
        //must report the effective kind as Native with no degradation markers at all.
        Activity.Current = null;

        using var testRoot = new Activity(nameof(NativeRentEmitsNoDegradationTelemetry));
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

        using var pool = new BaseMemoryPool(nativeBacking: SodiumBacking.Allocate);

        using(pool.Rent(32, AllocationKind.Native)) { }

        testRoot.Stop();

        var rentActivity = activities.FirstOrDefault(a => a.OperationName == "Rent");
        Assert.IsNotNull(rentActivity, "Should have captured the Rent lifecycle activity.");

        Assert.AreEqual("Native", rentActivity.GetTagItem("allocationKind")?.ToString(),
            "Telemetry must record the effective kind as Native when a backing is wired.");
        Assert.IsNull(rentActivity.GetTagItem("requestedAllocationKind"),
            "A non-degraded Native rent must not carry a requestedAllocationKind tag.");

        bool hasDegradedEvent = rentActivity.Events.Any(e => e.Name == "AllocationKindDegraded");
        Assert.IsFalse(hasDegradedEvent, "A non-degraded Native rent must not emit an AllocationKindDegraded event.");
    }


    [TestMethod]
    public void AllocatorDelegateAllocates()
    {
        SodiumTestEnvironment.RequireSodium();

        using var owner = SodiumBacking.Allocator(24);
        Assert.HasCount(24, owner.Memory, "The cached Allocator delegate should behave exactly like Allocate.");

        //Allocator is a cached delegate instance, not a fresh method-group conversion per read;
        //the locals keep the identity check from reading as a constant-true assertion (MSTEST0032).
        var firstRead = SodiumBacking.Allocator;
        var secondRead = SodiumBacking.Allocator;
        Assert.AreSame(firstRead, secondRead);
    }


    [TestMethod]
    public void FinalizerBackstopDoesNotCrash()
    {
        SodiumTestEnvironment.RequireSodium();

        AllocateAndDrop();

        //The signal here is the absence of a crash/abort, not an assertion: a leaked owner is released
        //by its finalizer, and libsodium ABORTS THE PROCESS on a corrupted allocation header, so
        //surviving these collections proves the finalizer freed the guarded allocation exactly once.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }


    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocateAndDrop()
    {
        var owner = SodiumBacking.Allocate(32);
        owner.Memory.Span.Fill(0xEE);

        //Deliberately no Dispose() and no retained reference beyond this frame: NoInlining keeps the
        //JIT from extending the owner's liveness into the caller, so it becomes finalizable as soon as
        //this method returns.
    }
}
