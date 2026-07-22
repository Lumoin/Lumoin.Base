using CsCheck;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lumoin.Base.MemoryProtection.Tests;

/// <summary>
/// Property-based tests using CsCheck for <see cref="MemoryProtectionBacking"/>. Unlike the Sodium
/// suite, these run unconditionally: the OS locking mechanism is present on every CI leg, so there
/// is no <c>RequireSodium</c>-style gating to mirror.
/// </summary>
[TestClass]
public sealed class MemoryProtectionCsCheckTests
{
    public TestContext TestContext { get; set; } = null!;


    [TestMethod]
    public void PropertyAllocateReturnsExactSizeWithRoundTrip()
    {
        //Locked memory is a process-wide quota; sample sequentially so parallel samples cannot exhaust it.
        Gen.Int[1, 8192].Sample(size =>
        {
            using var owner = MemoryProtectionBacking.Allocate(size);
            Assert.HasCount(size, owner.Memory, $"Allocate({size}) should return exactly {size} bytes.");

            byte pattern = (byte)(size % 256);
            owner.Memory.Span.Fill(pattern);

            int mismatch = owner.Memory.Span.IndexOfAnyExcept(pattern);
            Assert.AreEqual(-1, mismatch, $"Allocation of size {size} should round-trip the fill pattern (first mismatch at {mismatch}).");
        }, threads: 1);
    }


    [TestMethod]
    public void PropertyPoolNativeRentRoundTripsForAllSizes()
    {
        //A fresh MemoryProtectionBacking-wired strict pool per sample keeps each case independent
        //of the others. Locked memory is a process-wide quota; sample sequentially so parallel
        //samples cannot exhaust it.
        Gen.Int[1, 4096].Sample(size =>
        {
            using var pool = new BaseMemoryPool(nativeBacking: MemoryProtectionBacking.Allocate);
            using var owner = pool.Rent(size, AllocationKind.Native);

            Assert.HasCount(size, owner.Memory, $"Rent({size}, Native) should return exactly {size} bytes.");

            byte pattern = (byte)(size % 256);
            owner.Memory.Span.Fill(pattern);

            int mismatch = owner.Memory.Span.IndexOfAnyExcept(pattern);
            Assert.AreEqual(-1, mismatch, $"Native rent of size {size} should round-trip the fill pattern (first mismatch at {mismatch}).");
        }, threads: 1);
    }
}
