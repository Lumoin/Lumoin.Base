using System.Buffers;
using System.Diagnostics.Metrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lumoin.Base.Sodium.Tests;

/// <summary>
/// Behavioral tests for the protected-slab <see cref="AllocationKind.Native"/> tier
/// (<see cref="NativeRentMode.ProtectedSlab"/>) wired to the real libsodium backing, gated behind
/// <see cref="SodiumTestEnvironment.RequireSodium"/> since every case exercises the native library.
/// </summary>
[TestClass]
public sealed class SodiumProtectedSlabTests
{
    public TestContext TestContext { get; set; } = null!;


    [TestMethod]
    public void SlabModePoolServesManyRentsFromOneGuardedRegion()
    {
        SodiumTestEnvironment.RequireSodium();

        int callCount = 0;
        NativeBackingAllocator countingSodiumBacking = size =>
        {
            Interlocked.Increment(ref callCount);
            return SodiumBacking.Allocate(size);
        };

        using var meter = new Meter("Test", "1.0.0");
        using var pool = new BaseMemoryPool(
            meter,
            capacityStrategy: _ => 8,
            nativeBacking: countingSodiumBacking,
            nativeRentMode: NativeRentMode.ProtectedSlab);

        const int bufferSize = 32;
        var rentals = new List<IMemoryOwner<byte>>();

        for(int i = 0; i < 8; i++)
        {
            rentals.Add(pool.Rent(bufferSize, AllocationKind.Native));
        }

        Assert.AreEqual(1, callCount, "Eight rents within one slab's capacity should share a single sodium_malloc'd guarded region.");

        //Fill each segment with a value unique to its index, then verify none of them aliases.
        for(int i = 0; i < rentals.Count; i++)
        {
            rentals[i].Memory.Span.Fill((byte)(i + 1));
        }

        for(int i = 0; i < rentals.Count; i++)
        {
            byte expected = (byte)(i + 1);
            int mismatch = rentals[i].Memory.Span.IndexOfAnyExcept(expected);
            Assert.AreEqual(-1, mismatch, $"Segment {i} must not alias with any other segment sharing the guarded region.");
        }

        foreach(var rental in rentals)
        {
            rental.Dispose();
        }
    }


    [TestMethod]
    public void SlabModeRoundTripsAndZeroesOnReuse()
    {
        SodiumTestEnvironment.RequireSodium();

        using var meter = new Meter("Test", "1.0.0");
        using var pool = new BaseMemoryPool(
            meter,
            capacityStrategy: _ => 1,
            nativeBacking: SodiumBacking.Allocate,
            nativeRentMode: NativeRentMode.ProtectedSlab);

        var first = pool.Rent(32, AllocationKind.Native);
        first.Memory.Span.Fill(0xDE);
        first.Dispose();

        //With capacity 1, the second rent must reuse the segment just returned.
        using var second = pool.Rent(32, AllocationKind.Native);
        foreach(byte b in second.Memory.Span)
        {
            Assert.AreEqual(0, b, "Returned protected-slab memory backed by libsodium must be zeroed for security.");
        }
    }


    [TestMethod]
    public void TrimExcessReleasesGuardedRegion()
    {
        SodiumTestEnvironment.RequireSodium();

        using var meter = new Meter("Test", "1.0.0");
        using var pool = new BaseMemoryPool(
            meter,
            capacityStrategy: _ => 1,
            nativeBacking: SodiumBacking.Allocate,
            nativeRentMode: NativeRentMode.ProtectedSlab);

        var owner = pool.Rent(32, AllocationKind.Native);
        owner.Memory.Span.Fill(0xAB);
        owner.Dispose();

        //The reclaim path zeroes the whole region and disposes the region owner; for a sodium-backed
        //region that runs sodium_free's own canary check, which ABORTS THE PROCESS on corruption. The
        //process surviving this call is part of the signal that the protected-slab machinery
        //cooperates correctly with a real guarded allocator, not only the managed fakes used elsewhere.
        int reclaimed = pool.TrimExcess();
        Assert.IsGreaterThanOrEqualTo(1, reclaimed, "TrimExcess should reclaim the idle guarded native slab region.");
    }
}
