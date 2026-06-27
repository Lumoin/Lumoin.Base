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
        CollectionAssert.AreEqual(payload, owned.Memory.ToArray(), "Detach must concatenate the slab chain into exactly the written bytes.");
    }


    [TestMethod]
    public void DetachWithNothingWrittenReturnsAnEmptyOwner()
    {
        using SlabBufferWriter writer = new(BaseMemoryPool.Shared);

        using IMemoryOwner<byte> owned = writer.Detach();
        Assert.AreEqual(0, owned.Memory.Length);
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
        CollectionAssert.AreEqual(new byte[] { 9, 8 }, owned.Memory.ToArray());
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
}
