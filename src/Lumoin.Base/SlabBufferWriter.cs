using System.Buffers;
using System.Diagnostics;

namespace Lumoin.Base;

/// <summary>
/// An <see cref="IBufferWriter{T}"/> backed by slab-sized buffers rented from a <see cref="MemoryPool{T}"/>
/// (for example <see cref="BaseMemoryPool"/>). Slabs grow as a writer emits past the active slab's tail; on
/// <see cref="Detach"/> the slab chain is concatenated once into a single owned buffer of exactly the written
/// length, which the caller disposes.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why slabs.</strong> A streaming writer (a projection serializer, a codec) cannot predict total output
/// length up front, but it does not want to copy the whole buffer every time it grows. The slab strategy
/// amortises growth: each slab is sized to a configurable step and slabs are linked rather than reallocated.
/// </para>
/// <para>
/// <strong>Lifecycle.</strong> The writer rents its first slab lazily on the first <see cref="GetSpan"/> /
/// <see cref="GetMemory"/>. Each call ensures the active slab has the requested headroom; if not, the current
/// slab is committed and another rented. <see cref="Reset"/>, <see cref="Detach"/>, and <see cref="Dispose"/>
/// return every rented slab to the pool — and the pool clears each segment on return, so no written bytes
/// linger in pooled memory.
/// </para>
/// <para>
/// The pool is threaded in as the abstract <see cref="MemoryPool{T}"/> so any pool flows down the call chain;
/// pass <see cref="BaseMemoryPool.Shared"/> for the general path.
/// </para>
/// </remarks>
[DebuggerDisplay("SlabBufferWriter: BytesWritten={BytesWritten}, Slabs={Committed.Count}+{ActiveSlab is null ? 0 : 1}, Disposed={Disposed}")]
public sealed class SlabBufferWriter: IBufferWriter<byte>, IDisposable
{
    private const int DefaultSlabSize = 4096;

    private MemoryPool<byte> Pool { get; }
    private int SlabSize { get; }
    private List<IMemoryOwner<byte>> Committed { get; } = [];
    private List<int> CommittedLengths { get; } = [];

    private IMemoryOwner<byte>? ActiveSlab { get; set; }
    private int ActivePosition { get; set; }
    private int TotalCommittedBytes { get; set; }
    private bool Disposed { get; set; }


    /// <summary>
    /// Initialises a new <see cref="SlabBufferWriter"/> backed by <paramref name="pool"/>, renting slabs of
    /// <paramref name="slabSize"/> bytes.
    /// </summary>
    /// <param name="pool">The pool to rent slabs from.</param>
    /// <param name="slabSize">The byte length of each slab. Must be positive.</param>
    /// <exception cref="ArgumentNullException"><paramref name="pool"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slabSize"/> is not positive.</exception>
    public SlabBufferWriter(MemoryPool<byte> pool, int slabSize = DefaultSlabSize)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(slabSize, 0);

        this.Pool = pool;
        this.SlabSize = slabSize;
    }


    /// <summary>The total bytes written so far, summed across the committed slabs and the active slab.</summary>
    public int BytesWritten => TotalCommittedBytes + ActivePosition;


    /// <inheritdoc/>
    public void Advance(int count)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        if(ActiveSlab is null)
        {
            if(count != 0)
            {
                throw new InvalidOperationException("Advance was called without a prior GetSpan/GetMemory.");
            }

            return;
        }

        if(ActivePosition + count > ActiveSlab.Memory.Length)
        {
            throw new InvalidOperationException("Advance moved past the active slab's tail.");
        }

        ActivePosition += count;
    }


    /// <inheritdoc/>
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);

        return ActiveSlab!.Memory[ActivePosition..];
    }


    /// <inheritdoc/>
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);

        return ActiveSlab!.Memory.Span[ActivePosition..];
    }


    /// <summary>Returns all rented slabs to the pool and resets the writer to its initial state for reuse.</summary>
    public void Reset()
    {
        ThrowIfDisposed();
        ReleaseAllSlabs();
    }


    /// <summary>
    /// Concatenates the written bytes into a fresh pooled buffer of exactly <see cref="BytesWritten"/> length and
    /// returns it as an <see cref="IMemoryOwner{T}"/>; when nothing was written it returns an empty owner without
    /// renting. The writer is reset and may be reused. The returned owner is the caller's responsibility to dispose.
    /// </summary>
    /// <returns>An owned buffer of exactly the written bytes; empty when nothing was written.</returns>
    public IMemoryOwner<byte> Detach()
    {
        ThrowIfDisposed();

        int total = BytesWritten;
        if(total == 0)
        {
            ReleaseAllSlabs();

            return EmptyMemoryOwner.Instance;
        }

        IMemoryOwner<byte> result = Pool.Rent(total);
        Span<byte> output = result.Memory.Span[..total];
        int outIndex = 0;
        for(int i = 0; i < Committed.Count; i++)
        {
            int length = CommittedLengths[i];
            Committed[i].Memory.Span[..length].CopyTo(output[outIndex..]);
            outIndex += length;
        }

        if(ActiveSlab is not null && ActivePosition > 0)
        {
            ActiveSlab.Memory.Span[..ActivePosition].CopyTo(output[outIndex..]);
        }

        ReleaseAllSlabs();

        return result;
    }


    /// <inheritdoc/>
    public void Dispose()
    {
        if(Disposed)
        {
            return;
        }

        ReleaseAllSlabs();
        Disposed = true;
    }


    private void EnsureCapacity(int sizeHint)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);

        int needed = sizeHint == 0 ? 1 : sizeHint;

        if(ActiveSlab is null)
        {
            int initialSize = needed > SlabSize ? needed : SlabSize;
            ActiveSlab = Pool.Rent(initialSize);
            ActivePosition = 0;

            return;
        }

        int remaining = ActiveSlab.Memory.Length - ActivePosition;
        if(remaining >= needed)
        {
            return;
        }

        //Commit the current slab and rent a new one large enough for the request.
        Committed.Add(ActiveSlab);
        CommittedLengths.Add(ActivePosition);
        TotalCommittedBytes += ActivePosition;

        int newSize = needed > SlabSize ? needed : SlabSize;
        ActiveSlab = Pool.Rent(newSize);
        ActivePosition = 0;
    }


    private void ReleaseAllSlabs()
    {
        for(int i = 0; i < Committed.Count; i++)
        {
            Committed[i].Dispose();
        }

        Committed.Clear();
        CommittedLengths.Clear();
        TotalCommittedBytes = 0;

        ActiveSlab?.Dispose();
        ActiveSlab = null;
        ActivePosition = 0;
    }


    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
    }
}
