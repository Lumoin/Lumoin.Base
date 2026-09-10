using System.Buffers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lumoin.Base.Tests;

/// <summary>
/// Tests for <see cref="SlabBufferWriter"/>: it must hand back exactly the bytes written, concatenating across
/// slab boundaries, and return its slabs to the pool on detach/reset/dispose.
/// </summary>
[TestClass]
public sealed class SlabBufferWriterTests
{
    private static void WriteBytes(SlabBufferWriter writer, ReadOnlySpan<byte> data)
    {
        foreach(byte b in data)
        {
            Span<byte> span = writer.GetSpan(1);
            span[0] = b;
            writer.Advance(1);
        }
    }


    [TestMethod]
    public void DetachReturnsExactlyTheWrittenBytesAcrossSlabBoundaries()
    {
        // A tiny slab size forces the 10-byte payload to span several slabs.
        using SlabBufferWriter writer = new(BaseMemoryPool.Shared, slabSize: 4);
        byte[] payload = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];

        WriteBytes(writer, payload);
        Assert.AreEqual(10, writer.BytesWritten);

        using IMemoryOwner<byte> owned = writer.Detach();
        Assert.AreSequenceEqual(payload, owned.Memory, "Detach must concatenate the slab chain into exactly the written bytes.");
    }


    [TestMethod]
    public void DetachWithNothingWrittenReturnsAnEmptyOwner()
    {
        using SlabBufferWriter writer = new(BaseMemoryPool.Shared);

        using IMemoryOwner<byte> owned = writer.Detach();
        Assert.IsEmpty(owned.Memory);
    }


    [TestMethod]
    public void ResetAllowsReuse()
    {
        using SlabBufferWriter writer = new(BaseMemoryPool.Shared, slabSize: 8);

        WriteBytes(writer, [1, 2, 3]);
        writer.Reset();
        Assert.AreEqual(0, writer.BytesWritten);

        WriteBytes(writer, [9, 8]);
        using IMemoryOwner<byte> owned = writer.Detach();
        Assert.AreSequenceEqual(new byte[] { 9, 8 }, owned.Memory);
    }


    [TestMethod]
    public void UseAfterDisposeThrows()
    {
        SlabBufferWriter writer = new(BaseMemoryPool.Shared);
        writer.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => writer.GetSpan(1));
    }


    [TestMethod]
    public void ConstructorRejectsNonPositiveSlabSize()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SlabBufferWriter(BaseMemoryPool.Shared, 0));
    }


    [TestMethod]
    public void AdvanceZeroBeforeAnyWriteIsANoOp()
    {
        using SlabBufferWriter writer = new(BaseMemoryPool.Shared);

        writer.Advance(0);

        Assert.AreEqual(0, writer.BytesWritten);
    }


    [TestMethod]
    public void AdvanceBeforeAnyWriteThrows()
    {
        using SlabBufferWriter writer = new(BaseMemoryPool.Shared);

        Assert.ThrowsExactly<InvalidOperationException>(() => writer.Advance(1));
    }


    [TestMethod]
    public void AdvancePastActiveSlabTailThrows()
    {
        //GetSpan(n) with n at the slab size hands back exactly the whole (empty) slab; advancing one byte
        //further than that must trip the "moved past the active slab's tail" guard.
        using SlabBufferWriter writer = new(BaseMemoryPool.Shared, slabSize: 8);

        int spanLength = writer.GetSpan(8).Length;
        Assert.AreEqual(8, spanLength);

        Assert.ThrowsExactly<InvalidOperationException>(() => writer.Advance(spanLength + 1));
    }


    [TestMethod]
    public void WriteThroughGetMemoryRoundTripsIntoDetach()
    {
        using SlabBufferWriter writer = new(BaseMemoryPool.Shared);
        byte[] payload = [1, 2, 3, 4];

        Memory<byte> memory = writer.GetMemory(payload.Length);
        payload.CopyTo(memory);
        writer.Advance(payload.Length);

        using IMemoryOwner<byte> owned = writer.Detach();
        Assert.AreSequenceEqual(payload, owned.Memory, "A write through GetMemory must round-trip into Detach.");
    }


    [TestMethod]
    public void DetachAfterDisposeThrows()
    {
        SlabBufferWriter writer = new(BaseMemoryPool.Shared);
        writer.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => writer.Detach());
    }


    [TestMethod]
    public void DisposeTwiceIsSafe()
    {
        SlabBufferWriter writer = new(BaseMemoryPool.Shared);
        writer.Dispose();

        //Second dispose should not throw.
        writer.Dispose();
    }


    [TestMethod]
    public void DisposeReleasesEveryCommittedSlab()
    {
        CountingMemoryPool pool = new();

        using(SlabBufferWriter writer = new(pool, slabSize: 4))
        {
            WriteBytes(writer, [0, 1, 2, 3, 4, 5, 6, 7, 8, 9]);
            Assert.IsGreaterThanOrEqualTo(2, pool.RentCount, "A tiny slab size must force at least two committed slabs.");
        }

        Assert.AreEqual(pool.RentCount, pool.DisposeCount, "Dispose must return every rented slab, not just the active one.");
    }


    /// <summary>
    /// A <see cref="MemoryPool{T}"/> stand-in that counts rents and returns, so a test can prove every
    /// rented slab — not just the active one — was actually released.
    /// </summary>
    private sealed class CountingMemoryPool: MemoryPool<byte>
    {
        public int RentCount { get; private set; }

        public int DisposeCount { get; private set; }

        public override int MaxBufferSize => int.MaxValue;

        public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
        {
            RentCount++;
            int size = minBufferSize < 0 ? 4096 : minBufferSize;

            return new CountingOwner(new byte[size], this);
        }

        protected override void Dispose(bool disposing)
        {
        }


        private sealed class CountingOwner(byte[] buffer, CountingMemoryPool pool): IMemoryOwner<byte>
        {
            public Memory<byte> Memory => buffer;

            public void Dispose() => pool.DisposeCount++;
        }
    }
}
