using System.Buffers;
using CsCheck;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lumoin.Base.Tests;

/// <summary>
/// Property-based tests using CsCheck for the protected-slab <see cref="AllocationKind.Native"/>
/// tier (<see cref="NativeRentMode.ProtectedSlab"/>).
/// </summary>
[TestClass]
public sealed class ProtectedSlabCsCheckTests
{
    [TestMethod]
    public void PropertyProtectedSlabRentAlwaysReturnsExactSizeWithRoundTrip()
    {
        Gen.Int[1, 4096].Sample(size =>
        {
            using var pool = new BaseMemoryPool(nativeBacking: s => new ArrayOwner(s), nativeRentMode: NativeRentMode.ProtectedSlab);
            using var owner = pool.Rent(size, AllocationKind.Native);

            Assert.HasCount(size, owner.Memory, $"Protected-slab rent of size {size} should return exactly {size} bytes.");

            byte pattern = (byte)(size % 256);
            owner.Memory.Span.Fill(pattern);

            int mismatch = owner.Memory.Span.IndexOfAnyExcept(pattern);
            Assert.AreEqual(-1, mismatch, $"Protected-slab rent of size {size} should round-trip the fill pattern (first mismatch at {mismatch}).");
        });
    }


    //A stand-in native owner: a managed array of exactly the requested size that clears on dispose,
    //enough to satisfy NativeSlab's exact-size backing contract under property generation.
    private sealed class ArrayOwner(int size): IMemoryOwner<byte>
    {
        private readonly byte[] buffer = new byte[size];

        public Memory<byte> Memory => buffer;

        public void Dispose() => Array.Clear(buffer);
    }
}
