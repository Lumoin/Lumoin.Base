using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lumoin.Base.MemoryProtection.Tests;

/// <summary>
/// Behavioral tests for <see cref="MemoryProtectionBacking"/>: argument validation, allocation,
/// disposal, pinning, and <see cref="BaseMemoryPool"/> integration (both
/// <see cref="NativeRentMode.PerRentIsolated"/> and <see cref="NativeRentMode.ProtectedSlab"/>)
/// against the real OS locking mechanism (<c>VirtualLock</c>/<c>mlock</c>). Unlike the Sodium
/// suite, every test here runs unconditionally: <see cref="MemoryProtectionBacking.IsSupported"/>
/// is true on every CI leg (ubuntu/windows/macos/ubuntu-arm) and on every desktop host, so there
/// is no graceful-skip machinery to model.
/// </summary>
[TestClass]
public sealed class MemoryProtectionBackingTests
{
    public TestContext TestContext { get; set; } = null!;


    [TestMethod]
    public void IsSupportedIsTrueOnThisPlatform()
    {
        //Every CI leg and every desktop host has a supported locking mechanism, so this holds
        //unconditionally — unlike SodiumBacking.IsAvailable, which probes for an optional native
        //library that may not be present.
        Assert.IsTrue(MemoryProtectionBacking.IsSupported, "This platform should have a supported memory-locking mechanism.");
    }


    [TestMethod]
    public void AllocateRejectsNonPositiveSizes()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => MemoryProtectionBacking.Allocate(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => MemoryProtectionBacking.Allocate(-1));
    }


    [TestMethod]
    public void AllocateReturnsExactSizeZeroedAndUsable()
    {
        //Sizes stay small on purpose: tests run method-parallel ([assembly: Parallelize] is
        //method-scoped) and every allocation locks whole pages against the platform budget
        //(RLIMIT_MEMLOCK, the Windows working-set quota), so the
        //suite's worst-case concurrent locked footprint must sit far below common defaults. The
        //strict InsufficientMemoryException budget-exhaustion path is deliberately NOT provoked
        //here — quotas are environment-dependent and a test that locks megabytes would be flaky.
        int[] sizes = [1, 16, 32, 64, 4095, 4096, 4097, 16384];

        foreach(int size in sizes)
        {
            using var owner = MemoryProtectionBacking.Allocate(size);
            Assert.HasCount(size, owner.Memory, $"Allocate({size}) should return exactly {size} bytes.");

            //Fresh native memory is uninitialized; MemoryProtectionBacking must hand out zeroed
            //bytes regardless, so check this BEFORE writing anything.
            int firstNonZero = owner.Memory.Span.IndexOfAnyExcept((byte)0);
            Assert.IsLessThan(0, firstNonZero, $"Allocate({size}) should hand out memory zeroed on handout (first non-zero at {firstNonZero}).");

            owner.Memory.Span.Fill(0xAB);
            Assert.AreEqual<byte>(0xAB, owner.Memory.Span[0], "First byte should be writable and readable.");
            Assert.AreEqual<byte>(0xAB, owner.Memory.Span[size - 1], "Last byte should be writable and readable.");
        }
    }


    [TestMethod]
    public void AllocationsAreIndependent()
    {
        //Mirrors AllocationsAreIndependent in SodiumBackingTests: two live locked allocations of
        //the same size must be backed by distinct memory, never aliasing. Page-granular allocation
        //(see MemoryProtectionBacking.Allocate) is exactly what makes this hold: POSIX mlock/munlock
        //are not reference counted, so sharing a page would silently unlock the other allocation.
        using var a = MemoryProtectionBacking.Allocate(32);
        using var b = MemoryProtectionBacking.Allocate(32);

        a.Memory.Span.Fill(0x11);
        b.Memory.Span.Fill(0x22);

        Assert.AreEqual<byte>(0x11, a.Memory.Span[0], "Independent allocations must not alias.");
        Assert.AreEqual<byte>(0x22, b.Memory.Span[0], "Independent allocations must not alias.");
    }


    [TestMethod]
    public void DisposeIsIdempotent()
    {
        var owner = MemoryProtectionBacking.Allocate(32);
        owner.Memory.Span.Fill(0xFF);

        owner.Dispose();

        //Second dispose should not throw.
        owner.Dispose();
    }


    [TestMethod]
    public void MemoryAccessAfterDisposeThrows()
    {
        var owner = MemoryProtectionBacking.Allocate(32);
        owner.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = owner.Memory);

        //Pin carries its own dispose check, separate from the GetSpan path that Memory exercises;
        //a disposed owner must refuse to hand out a handle to freed, unlocked native memory.
        var manager = (MemoryManager<byte>)owner;
        Assert.ThrowsExactly<ObjectDisposedException>(() => manager.Pin());
    }


    [TestMethod]
    public void PinBoundsAreValidated()
    {
        //Pin computes an address into locked native memory with plain pointer arithmetic, so its
        //bounds contract is safety-relevant: negative and beyond-length offsets are rejected, and
        //the documented boundary elementIndex == length is allowed (an empty tail slice pins at
        //the end without dereferencing it).
        using var owner = MemoryProtectionBacking.Allocate(64);
        var manager = (MemoryManager<byte>)owner;

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => manager.Pin(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => manager.Pin(65));

        using(manager.Pin(0)) { }

        using(manager.Pin(64)) { }
    }


    [TestMethod]
    public unsafe void PinnedHandleAddressesTheAllocation()
    {
        using var owner = MemoryProtectionBacking.Allocate(64);
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
    public unsafe void PinAtANonZeroOffsetAddressesThatByte()
    {
        using var owner = MemoryProtectionBacking.Allocate(64);
        var manager = (MemoryManager<byte>)owner;

        //Fill through GetSpan with a position-dependent pattern so each byte is distinguishable.
        Span<byte> span = manager.GetSpan();
        for(int i = 0; i < span.Length; i++)
        {
            span[i] = (byte)i;
        }

        const int offset = 40;
        using MemoryHandle handle = manager.Pin(offset);

        byte* pointer = (byte*)handle.Pointer;
        Assert.AreEqual((byte)offset, pointer[0], "Pin(N) must address byte N, not byte 0.");
    }


    [TestMethod]
    public async Task ConcurrentDisposeFreesExactlyOnce()
    {
        //The owner's contract says concurrent disposals free exactly once (the pointer is claimed
        //by atomic exchange). Race several disposers through a start gate; the signal is the
        //absence of a double-unlock/double-free and the owner being properly disposed afterwards.
        var owner = MemoryProtectionBacking.Allocate(64);
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
    public void FinalizerBackstopDoesNotCrash()
    {
        AllocateAndDrop();

        //The signal here is the absence of a crash, not an assertion: a leaked owner is released by
        //its finalizer, which zeroes, unlocks, and frees the region; surviving these collections
        //proves the finalizer freed the locked allocation exactly once.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }


    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocateAndDrop()
    {
        var owner = MemoryProtectionBacking.Allocate(32);
        owner.Memory.Span.Fill(0xEE);

        //Deliberately no Dispose() and no retained reference beyond this frame: NoInlining keeps the
        //JIT from extending the owner's liveness into the caller, so it becomes finalizable as soon as
        //this method returns.
    }


    [TestMethod]
    public void StrictPoolServesNativeRentsThroughLockedMemory()
    {
        //Strict: no allowNativeDegradation. A MemoryProtectionBacking-wired backing must serve
        //Native directly rather than degrading, matching
        //WiredNativeBackingServesNativeEvenWhenDegradationDisallowed in BaseMemoryPoolTests.
        using var pool = new BaseMemoryPool(nativeBacking: MemoryProtectionBacking.Allocate);
        using var owner = pool.Rent(32, AllocationKind.Native);

        Assert.HasCount(32, owner.Memory, "Rent should return exactly the requested size.");

        owner.Memory.Span.Fill(0x5A);
        Assert.AreEqual<byte>(0x5A, owner.Memory.Span[0]);
        Assert.AreEqual<byte>(0x5A, owner.Memory.Span[31]);
    }


    [TestMethod]
    public void ProtectedSlabModeWorksOverLockedRegion()
    {
        //One locked region backs a whole slab: capacity 8 means eight 32-byte rents must share a
        //single MemoryProtectionBacking.Allocate call — verified with a counting wrapper delegate
        //rather than trusting geometry — before the segments are checked for aliasing.
        int callCount = 0;
        IMemoryOwner<byte> CountingAllocate(int size)
        {
            Interlocked.Increment(ref callCount);

            return MemoryProtectionBacking.Allocate(size);
        }

        using var meter = new Meter("Test", "1.0.0");
        using var pool = new BaseMemoryPool(
            meter,
            capacityStrategy: _ => 8,
            nativeBacking: CountingAllocate,
            nativeRentMode: NativeRentMode.ProtectedSlab);

        var rentals = new List<IMemoryOwner<byte>>();
        for(int i = 0; i < 8; i++)
        {
            rentals.Add(pool.Rent(32, AllocationKind.Native));
        }

        Assert.AreEqual(1, callCount, "Eight rents within one slab's capacity should share a single locked backing region.");

        for(int i = 0; i < rentals.Count; i++)
        {
            rentals[i].Memory.Span.Fill((byte)(i + 1));
        }

        for(int i = 0; i < rentals.Count; i++)
        {
            Assert.AreEqual<byte>((byte)(i + 1), rentals[i].Memory.Span[0], "Distinct segments of the shared locked region must not alias.");
            Assert.AreEqual<byte>((byte)(i + 1), rentals[i].Memory.Span[31], "Distinct segments of the shared locked region must not alias.");
        }

        foreach(var rental in rentals)
        {
            rental.Dispose();
        }

        int reclaimed = pool.TrimExcess();
        Assert.IsGreaterThanOrEqualTo(1, reclaimed, "TrimExcess should reclaim the idle protected native slab over the locked region.");
    }


    [TestMethod]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Analyzer false positive on testRoot.")]
    public void NativeRentEmitsNoDegradationTelemetry()
    {
        //Mirrors DegradedNativeRentReportsEffectiveKindAndEmitsEvent in BaseMemoryPoolTests, but for
        //the non-degraded path: a MemoryProtectionBacking-wired strict pool serves Native directly,
        //so the Rent activity must report the effective kind as Native with no degradation markers
        //at all.
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

        using var pool = new BaseMemoryPool(nativeBacking: MemoryProtectionBacking.Allocate);

        using(pool.Rent(32, AllocationKind.Native)) { }

        testRoot.Stop();

        var rentActivity = activities.FirstOrDefault(a => a.OperationName == "Rent");
        Assert.IsNotNull(rentActivity, "Should have captured the Rent lifecycle activity.");

        Assert.AreEqual("Native", rentActivity.GetTagItem("allocationKind")?.ToString(),
            "Telemetry must record the effective kind as Native when a backing is wired.");
        Assert.IsNull(rentActivity.GetTagItem("requestedAllocationKind"),
            "A non-degraded Native rent must not carry a requestedAllocationKind tag.");

        Assert.DoesNotContain(e => e.Name == "AllocationKindDegraded", rentActivity.Events, "A non-degraded Native rent must not emit an AllocationKindDegraded event.");
    }


    [TestMethod]
    public void AllocatorDelegateAllocates()
    {
        using var owner = MemoryProtectionBacking.Allocator(24);
        Assert.HasCount(24, owner.Memory, "The cached Allocator delegate should behave exactly like Allocate.");

        //Allocator is a cached delegate instance, not a fresh method-group conversion per read;
        //the locals keep the identity check from reading as a constant-true assertion (MSTEST0032).
        var firstRead = MemoryProtectionBacking.Allocator;
        var secondRead = MemoryProtectionBacking.Allocator;
        Assert.AreSame(firstRead, secondRead);
    }
}
