using CsCheck;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lumoin.Base.Libsodium.Tests;

/// <summary>
/// Property-based tests using CsCheck for <see cref="SodiumBacking"/>, gated behind
/// <see cref="LibsodiumTestEnvironment.RequireSodium"/> since every case exercises the real native
/// library.
/// </summary>
[TestClass]
public sealed class SodiumBackingCsCheckTests
{
    public TestContext TestContext { get; set; } = null!;


    [TestMethod]
    public void PropertyAllocateReturnsExactSizeWithRoundTrip()
    {
        LibsodiumTestEnvironment.RequireSodium();

        //Locked memory is a process-wide quota; sample sequentially so parallel samples cannot exhaust it.
        Gen.Int[1, 8192].Sample(size =>
        {
            using var owner = SodiumBacking.Allocate(size);
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
        LibsodiumTestEnvironment.RequireSodium();

        //A fresh sodium-wired strict pool per sample keeps each case independent of the others.
        //Locked memory is a process-wide quota; sample sequentially so parallel samples cannot
        //exhaust it.
        Gen.Int[1, 4096].Sample(size =>
        {
            using var pool = new BaseMemoryPool(nativeBacking: SodiumBacking.Allocate);
            using var owner = pool.Rent(size, AllocationKind.Native);

            Assert.HasCount(size, owner.Memory, $"Rent({size}, Native) should return exactly {size} bytes.");

            byte pattern = (byte)(size % 256);
            owner.Memory.Span.Fill(pattern);

            int mismatch = owner.Memory.Span.IndexOfAnyExcept(pattern);
            Assert.AreEqual(-1, mismatch, $"Native rent of size {size} should round-trip the fill pattern (first mismatch at {mismatch}).");
        }, threads: 1);
    }
}
