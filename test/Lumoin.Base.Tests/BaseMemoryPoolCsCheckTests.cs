using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using CsCheck;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lumoin.Base.Tests;

/// <summary>
/// Property-based tests using CsCheck for comprehensive validation of <see cref="BaseMemoryPool"/>.
/// </summary>
[TestClass]
public sealed class BaseMemoryPoolCsCheckTests
{
    public TestContext TestContext { get; set; } = null!;


    [TestMethod]
    public void PropertyRentAlwaysReturnsExactSize()
    {
        Gen.Int[1, 10000].Sample(bufferSize =>
        {
            using var pool = new BaseMemoryPool();
            using var buffer = pool.Rent(bufferSize);

            Assert.AreEqual(bufferSize, buffer.Memory.Length,
                $"Buffer size {bufferSize} should return exactly {bufferSize} elements.");
        });
    }


    [TestMethod]
    public void PropertyRentAlwaysReturnsExactSizeForEveryKind()
    {
        var kinds = Gen.OneOfConst(AllocationKind.Managed, AllocationKind.Pinned, AllocationKind.Native);

        Gen.Select(Gen.Int[1, 10000], kinds).Sample(t =>
        {
            (int bufferSize, AllocationKind kind) = t;

            using var pool = new BaseMemoryPool(size => new ArrayOwner(size));
            using var buffer = pool.Rent(bufferSize, kind);

            Assert.AreEqual(bufferSize, buffer.Memory.Length,
                $"Buffer size {bufferSize} should return exactly {bufferSize} elements for {kind}.");
        });
    }


    [TestMethod]
    public void PropertyMultipleRentReturnCycles()
    {
        Gen.Int[1, 100].Sample(cycleCount =>
        {
            using var pool = new BaseMemoryPool();

            for(int i = 0; i < cycleCount; i++)
            {
                int bufferSize = (i % 10) + 1;
                using var buffer = pool.Rent(bufferSize);

                Assert.AreEqual(bufferSize, buffer.Memory.Length);

                if(buffer.Memory.Length > 0)
                {
                    buffer.Memory.Span[0] = (byte)(i % 256);
                }
            }
        });
    }


    [TestMethod]
    public void PropertyConcurrentOperationsAreThreadSafe()
    {
        Gen.Int[1, 20].Sample(threadCount =>
        {
            using var pool = new BaseMemoryPool();
            var tasks = new Task[threadCount];
            var exceptions = new ConcurrentBag<Exception>();

            for(int i = 0; i < threadCount; i++)
            {
                int threadId = i;
                tasks[i] = Task.Run(() =>
                {
                    try
                    {
                        for(int j = 0; j < 100; j++)
                        {
                            int size = (j % 50) + 1;
                            using var buffer = pool.Rent(size);

                            Assert.AreEqual(size, buffer.Memory.Length);

                            if(buffer.Memory.Length > 0)
                            {
                                buffer.Memory.Span.Fill((byte)(threadId % 256));
                            }
                        }
                    }
                    catch(Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }, TestContext.CancellationToken);
            }

            Task.WaitAll(tasks, TestContext.CancellationToken);

            Assert.IsTrue(exceptions.IsEmpty,
                $"No exceptions should occur during concurrent operations. Found: {string.Join(", ", exceptions)}.");
        });
    }


    [TestMethod]
    public void PropertyMemoryIsClearedAfterDisposal()
    {
        Gen.Int[1, 1000].Sample(bufferSize =>
        {
            using var pool = new BaseMemoryPool();

            var buffer = pool.Rent(bufferSize);
            buffer.Memory.Span.Fill(0xAA);
            buffer.Dispose();

            Assert.ThrowsExactly<ObjectDisposedException>(() => _ = buffer.Memory,
                "Accessing disposed buffer should throw ObjectDisposedException.");
        });
    }


    [TestMethod]
    public void PropertyReturnedSegmentIsZeroedOnReuse()
    {
        //A capacity of 1 means the next same-size rent must reuse the just-returned segment, so the
        //zeroize-on-return invariant is directly observable.
        Gen.Int[1, 512].Sample(bufferSize =>
        {
            using var meter = new Meter("PropertyTest", "1.0.0");
            using var pool = new BaseMemoryPool(meter, capacityStrategy: _ => 1);

            var first = pool.Rent(bufferSize);
            first.Memory.Span.Fill(0xCD);
            first.Dispose();

            using var second = pool.Rent(bufferSize);
            int nonZero = second.Memory.Span.IndexOfAnyExcept((byte)0);
            Assert.AreEqual(-1, nonZero,
                $"Reused segment of size {bufferSize} must be fully zeroed (first non-zero at {nonZero}).");
        });
    }


    [TestMethod]
    public void PropertyHandlesVariousBufferSizeDistributions()
    {
        int[] cryptoSizes = [16, 20, 32, 48, 64, 128, 256, 384, 512, 1024];

        Gen.Int[0, cryptoSizes.Length - 1].Array[1, 100].Sample(sizeIndices =>
        {
            using var pool = new BaseMemoryPool();
            var buffers = new List<IMemoryOwner<byte>>();
            try
            {
                foreach(int index in sizeIndices)
                {
                    int size = cryptoSizes[index];
                    var buffer = pool.Rent(size);
                    buffers.Add(buffer);

                    Assert.AreEqual(size, buffer.Memory.Length);
                }
            }
            finally
            {
                foreach(var buffer in buffers)
                {
                    buffer.Dispose();
                }
            }
        });
    }


    [TestMethod]
    public void PropertyTrimExcessNeverBreaksActiveRentals()
    {
        Gen.Int[1, 50].Sample(operationCount =>
        {
            using var meter = new Meter("PropertyTest", "1.0.0");
            using var pool = new BaseMemoryPool(
                meter,
                capacityStrategy: _ => 2);

            var activeBuffers = new List<IMemoryOwner<byte>>();

            try
            {
                for(int i = 0; i < operationCount; i++)
                {
                    int size = (i % 5 + 1) * 32;
                    activeBuffers.Add(pool.Rent(size));

                    //Periodically return some buffers and trim.
                    if(i % 7 == 0 && activeBuffers.Count > 1)
                    {
                        activeBuffers[0].Dispose();
                        activeBuffers.RemoveAt(0);
                        pool.TrimExcess();
                    }
                }

                //All remaining active buffers should still be accessible.
                foreach(var buffer in activeBuffers)
                {
                    Assert.IsGreaterThan(0, buffer.Memory.Length,
                        "Active buffers must remain accessible after TrimExcess.");
                }
            }
            finally
            {
                foreach(var buffer in activeBuffers)
                {
                    buffer.Dispose();
                }
            }
        });
    }


    //A stand-in native owner: a managed array that clears on dispose, enough to exercise the native
    //backing seam under property generation.
    private sealed class ArrayOwner(int size): IMemoryOwner<byte>
    {
        private readonly byte[] buffer = new byte[size];

        public Memory<byte> Memory => buffer;

        public void Dispose() => Array.Clear(buffer);
    }
}
