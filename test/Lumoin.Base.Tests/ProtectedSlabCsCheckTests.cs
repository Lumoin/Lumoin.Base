using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
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
    public TestContext TestContext { get; set; } = null!;


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


    [TestMethod]
    public async Task PropertyConcurrentSlabRentReturnNeverAliases()
    {
        //Adapted from ConcurrentRentReturnNeverAliasesBackingMemory in BaseMemoryPoolCsCheckTests: a
        //small per-slab capacity forces heavy segment reuse within the protected slab too, so
        //concurrent renters contend for the same shared backing region and its canary-bracketed
        //segments. The invariant under test is unchanged: no two live renters ever alias.
        using var meter = new Meter("Test", "1.0.0");
        using var pool = new BaseMemoryPool(
            meter,
            capacityStrategy: _ => 4,
            nativeBacking: size => new ArrayOwner(size),
            nativeRentMode: NativeRentMode.ProtectedSlab);

        const int taskCount = 32;
        const int iterationsPerTask = 200;
        const int bufferSize = 32;

        var failures = new ConcurrentQueue<string>();
        var cancellationToken = TestContext.CancellationToken;

        var tasks = Enumerable.Range(0, taskCount).Select(taskIndex => Task.Run(async () =>
        {
            for(int iteration = 0; iteration < iterationsPerTask; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                byte pattern = (byte)((taskIndex * 31 + iteration) & 0xFF);

                using var owner = pool.Rent(bufferSize, AllocationKind.Native);
                if(owner.Memory.Length != bufferSize)
                {
                    failures.Enqueue($"Rented length {owner.Memory.Length}, expected {bufferSize} (task {taskIndex}, iter {iteration}).");
                    return;
                }

                owner.Memory.Span.Fill(pattern);

                //Yield so other renters interleave while this segment is held and filled.
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

        Assert.IsEmpty(failures, $"No renter should ever observe aliased or mis-sized memory in a protected slab. First failures: {string.Join(" | ", failures.Take(5))}");

        //After the storm settles every rental has been returned, so the pool must be fully usable.
        using var afterStorm = pool.Rent(bufferSize, AllocationKind.Native);
        Assert.HasCount(bufferSize, afterStorm.Memory, "Pool must remain usable after concurrent protected-slab rent/return.");
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
