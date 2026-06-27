using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;

namespace Lumoin.Base;

/// <summary>
/// A single-writer arena that interns UTF-8 byte sequences and returns <see cref="Utf8String"/> views
/// over pool-managed memory. Duplicate inputs return the same interned value, and all term memory is
/// freed in bulk when the pool is disposed.
/// </summary>
/// <remarks>
/// <para>
/// Interning is the right tool where the same bytes recur often — protocol IRIs, vocabulary terms,
/// property names. The pool packs interned bytes into large slabs rented from a
/// <see cref="MemoryPool{T}"/> (the same abstract seam <see cref="SlabBufferWriter"/> uses, defaulting
/// to a private <see cref="BaseMemoryPool"/>), so thousands of values share a handful of rentals and
/// disposal returns them all at once.
/// </para>
/// <para>
/// <strong>Lookup is zero-allocation on a hit.</strong> A <see cref="HashSet{T}"/> of
/// <see cref="Utf8String"/> is probed through <see cref="Utf8StringComparer"/>'s
/// <see cref="IAlternateEqualityComparer{TAlternate, T}"/> face, so a raw UTF-8 span finds an existing
/// value without constructing a temporary or decoding.
/// </para>
/// <para>
/// <strong>Memory protection.</strong> The arena rents with the <see cref="AllocationKind"/> chosen at
/// construction, so interned bytes can live in <see cref="AllocationKind.Pinned"/> or
/// <see cref="AllocationKind.Native"/> memory. A non-<see cref="AllocationKind.Managed"/> kind requires
/// a <see cref="BaseMemoryPool"/>; the abstract seam cannot honor protection. Note that interning is a
/// poor fit for secrets regardless: it keeps one shared copy alive for the pool's lifetime, defeating
/// deterministic wipe — hold secrets in <see cref="SensitiveMemory"/>, not here.
/// </para>
/// <para>
/// <strong>Validation.</strong> By default the pool rejects bytes that are not well-formed UTF-8, so an
/// interned value is always valid. A caller interning bytes it has already validated (or that are valid
/// by construction) can opt out with <c>validateOnIntern: false</c> to skip the per-intern check.
/// </para>
/// <para>
/// <strong>Lifetime.</strong> Every <see cref="Utf8String"/> the pool returns is valid only until the
/// pool is disposed. Reading one afterwards is undefined (the bytes are cleared on return). The pool is
/// not thread-safe for concurrent interning (single-writer); the underlying pool's rentals are.
/// </para>
/// </remarks>
[DebuggerDisplay("Utf8StringPool: Count={Count}, TotalBytes={TotalBytesInterned}")]
public sealed class Utf8StringPool: IDisposable
{
    /// <summary>
    /// The ambient pool used where an explicit pool is not threaded through. The application installs one
    /// at startup and owns its lifetime — disposing it frees and clears everything interned through it. It
    /// is <see langword="null"/> until set; the library never creates one implicitly, so nothing is
    /// retained or left unwiped unless an application opts in. Do not intern secrets through the ambient
    /// pool — scope a dedicated pool for those. Intended to be installed once at startup; the setter is not
    /// safe for concurrent reassignment.
    /// </summary>
    public static Utf8StringPool? Instance { get; set; }


    /// <summary>Default size, in bytes, for arena slabs.</summary>
    private const int DefaultSlabSize = 64 * 1024;

    /// <summary>Maximum UTF-8 byte count encoded on the stack in <see cref="Intern(string)"/>.</summary>
    private const int MaxStackallocBytes = 256;

    private MemoryPool<byte> Pool { get; }
    private bool OwnsPool { get; }
    private int SlabSize { get; }
    private AllocationKind AllocationKind { get; }
    private bool ValidateOnIntern { get; }
    private Utf8StringComparer Comparer { get; }
    private List<IMemoryOwner<byte>> Slabs { get; } = [];
    private HashSet<Utf8String> Table { get; }
    private HashSet<Utf8String>.AlternateLookup<ReadOnlySpan<byte>> ByBytes { get; }
    private Counter<long>? InternOperationsCounter { get; }
    private Counter<long>? InternHitsCounter { get; }

    /// <summary>
    /// The active arena slab. Tracked in <see cref="Slabs"/> and disposed in bulk during
    /// <see cref="Dispose"/>, so it is not disposed through this property directly.
    /// </summary>
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
        Justification = "The active slab is tracked in the slabs list and disposed in bulk during Dispose.")]
    private IMemoryOwner<byte> CurrentOwner { get; set; }
    private Memory<byte> CurrentBuffer { get; set; }
    private int Position { get; set; }
    private bool Disposed { get; set; }


    /// <summary>
    /// Initializes a pool that owns a private <see cref="BaseMemoryPool"/> tuned for arena use
    /// (one segment per slab class, tracing off).
    /// </summary>
    /// <param name="slabSize">The byte size of each arena slab. Defaults to 64 KB.</param>
    /// <param name="allocationKind">How interned arena memory is backed. Defaults to <see cref="AllocationKind.Managed"/>. <see cref="AllocationKind.Native"/> needs a native-backed pool injected through the other constructor.</param>
    /// <param name="comparer">The comparer that defines hashing/equality, or <see langword="null"/> for <see cref="Utf8StringComparer.Ordinal"/>.</param>
    /// <param name="validateOnIntern">When <see langword="true"/> (the default), <see cref="Intern(ReadOnlySpan{byte})"/> rejects bytes that are not well-formed UTF-8. Pass <see langword="false"/> to opt out on trusted, hot paths.</param>
    public Utf8StringPool(
        int slabSize = DefaultSlabSize,
        AllocationKind allocationKind = AllocationKind.Managed,
        Utf8StringComparer? comparer = null,
        bool validateOnIntern = true)
        : this(CreateDefaultPool(), ownsPool: true, slabSize, allocationKind, comparer, validateOnIntern, meter: null)
    {
    }


    /// <summary>
    /// Initializes a pool that rents arena slabs from <paramref name="pool"/> (which it never disposes).
    /// Pass <see cref="BaseMemoryPool.Shared"/> for the general path, or a dedicated pool to bound or
    /// protect the arena.
    /// </summary>
    /// <param name="pool">The pool to rent arena slabs from.</param>
    /// <param name="slabSize">The byte size of each arena slab. Defaults to 64 KB.</param>
    /// <param name="allocationKind">How interned arena memory is backed. A non-<see cref="AllocationKind.Managed"/> kind requires <paramref name="pool"/> to be a <see cref="BaseMemoryPool"/>.</param>
    /// <param name="comparer">The comparer that defines hashing/equality, or <see langword="null"/> for <see cref="Utf8StringComparer.Ordinal"/>.</param>
    /// <param name="validateOnIntern">When <see langword="true"/> (the default), <see cref="Intern(ReadOnlySpan{byte})"/> rejects bytes that are not well-formed UTF-8. Pass <see langword="false"/> to opt out on trusted, hot paths.</param>
    /// <param name="meter">Optional meter for intern metrics. When <see langword="null"/>, no intern metrics are recorded.</param>
    public Utf8StringPool(
        MemoryPool<byte> pool,
        int slabSize = DefaultSlabSize,
        AllocationKind allocationKind = AllocationKind.Managed,
        Utf8StringComparer? comparer = null,
        bool validateOnIntern = true,
        Meter? meter = null)
        : this(pool, ownsPool: false, slabSize, allocationKind, comparer, validateOnIntern, meter)
    {
    }


    private Utf8StringPool(
        MemoryPool<byte> pool,
        bool ownsPool,
        int slabSize,
        AllocationKind allocationKind,
        Utf8StringComparer? comparer,
        bool validateOnIntern,
        Meter? meter)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slabSize);

        if(allocationKind != AllocationKind.Managed && pool is not BaseMemoryPool)
        {
            throw new ArgumentException(
                "A non-Managed AllocationKind requires a BaseMemoryPool; the abstract MemoryPool<byte> seam cannot honor memory protection.",
                nameof(allocationKind));
        }

        Pool = pool;
        OwnsPool = ownsPool;
        SlabSize = slabSize;
        AllocationKind = allocationKind;
        ValidateOnIntern = validateOnIntern;

        Comparer = comparer ?? Utf8StringComparer.Ordinal;
        Table = new HashSet<Utf8String>(Comparer);
        ByBytes = Table.GetAlternateLookup<ReadOnlySpan<byte>>();

        CurrentOwner = RentBuffer(slabSize);
        CurrentBuffer = CurrentOwner.Memory;
        Slabs.Add(CurrentOwner);

        if(meter is not null)
        {
            InternOperationsCounter = meter.CreateCounter<long>(
                Utf8StringPoolMetrics.InternOperationsTotal,
                "operations",
                "Total intern operations (hits + misses).");

            InternHitsCounter = meter.CreateCounter<long>(
                Utf8StringPoolMetrics.InternHitsTotal,
                "operations",
                "Intern cache hit count (existing value returned).");

            meter.CreateObservableUpDownCounter(
                Utf8StringPoolMetrics.UniqueCount,
                () => Table.Count,
                "strings",
                "Number of unique values interned in the pool.");

            meter.CreateObservableUpDownCounter(
                Utf8StringPoolMetrics.TotalBytesInterned,
                () => TotalBytesInterned,
                "bytes",
                "Total bytes interned in the pool.");
        }
    }


    /// <summary>Gets the number of unique values interned in this pool.</summary>
    public int Count => Table.Count;


    /// <summary>Gets the total bytes interned. Test/diagnostic accessor.</summary>
    internal long TotalBytesInterned { get; private set; }


    /// <summary>Gets the number of arena slabs rented. Test/diagnostic accessor.</summary>
    internal int SlabCount => Slabs.Count;


    /// <summary>
    /// Interns a UTF-8 byte sequence, returning a <see cref="Utf8String"/> backed by pool memory. If the
    /// same bytes were interned before, the previously created value is returned with no allocation.
    /// </summary>
    /// <param name="utf8Bytes">The UTF-8 bytes to intern.</param>
    /// <returns>An interned <see cref="Utf8String"/> over pool-managed memory.</returns>
    /// <exception cref="ObjectDisposedException">The pool has been disposed.</exception>
    /// <exception cref="ArgumentException">Validation is enabled and <paramref name="utf8Bytes"/> is not well-formed UTF-8.</exception>
    public Utf8String Intern(ReadOnlySpan<byte> utf8Bytes)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        if(ValidateOnIntern && !System.Text.Unicode.Utf8.IsValid(utf8Bytes))
        {
            throw new ArgumentException("The bytes are not well-formed UTF-8.", nameof(utf8Bytes));
        }

        InternOperationsCounter?.Add(1);

        if(ByBytes.TryGetValue(utf8Bytes, out Utf8String existing))
        {
            InternHitsCounter?.Add(1);

            return existing;
        }

        Utf8String interned = Allocate(utf8Bytes);
        Table.Add(interned);
        TotalBytesInterned += utf8Bytes.Length;

        return interned;
    }


    /// <summary>
    /// Interns a UTF-8 byte sequence that may span multiple segments. A single-segment sequence is
    /// interned in place; a multi-segment sequence is gathered into a pooled scratch buffer first.
    /// </summary>
    /// <param name="utf8Bytes">The UTF-8 bytes to intern.</param>
    /// <returns>An interned <see cref="Utf8String"/> over pool-managed memory.</returns>
    /// <exception cref="ObjectDisposedException">The pool has been disposed.</exception>
    /// <exception cref="ArgumentException">Validation is enabled and <paramref name="utf8Bytes"/> is not well-formed UTF-8.</exception>
    public Utf8String Intern(ReadOnlySequence<byte> utf8Bytes)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        if(utf8Bytes.IsSingleSegment)
        {
            return Intern(utf8Bytes.FirstSpan);
        }

        int length = (int)utf8Bytes.Length;
        using IMemoryOwner<byte> scratch = RentBuffer(length);
        Span<byte> span = scratch.Memory.Span[..length];
        utf8Bytes.CopyTo(span);

        return Intern(span);
    }


    /// <summary>Interns a .NET string by encoding it as UTF-8.</summary>
    /// <param name="value">The string to intern.</param>
    /// <returns>An interned <see cref="Utf8String"/> over pool-managed memory.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The pool has been disposed.</exception>
    public Utf8String Intern(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ObjectDisposedException.ThrowIf(Disposed, this);

        int maxByteCount = System.Text.Encoding.UTF8.GetMaxByteCount(value.Length);
        if(maxByteCount <= MaxStackallocBytes)
        {
            Span<byte> buffer = stackalloc byte[maxByteCount];
            int written = System.Text.Encoding.UTF8.GetBytes(value, buffer);

            return Intern(buffer[..written]);
        }

        using IMemoryOwner<byte> owner = RentBuffer(maxByteCount);
        int writtenToBuffer = System.Text.Encoding.UTF8.GetBytes(value, owner.Memory.Span);

        return Intern(owner.Memory.Span[..writtenToBuffer]);
    }


    /// <summary>
    /// Probes for an existing interned value without ever growing the arena.
    /// </summary>
    /// <param name="utf8Bytes">The UTF-8 bytes to look up.</param>
    /// <param name="value">On success, the interned value; otherwise <see cref="Utf8String.Empty"/>.</param>
    /// <returns><see langword="true"/> when the value is already interned.</returns>
    /// <exception cref="ObjectDisposedException">The pool has been disposed.</exception>
    public bool TryGet(ReadOnlySpan<byte> utf8Bytes, out Utf8String value)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        return ByBytes.TryGetValue(utf8Bytes, out value);
    }


    /// <summary>
    /// Rents a transient, exact-size scratch buffer from this pool's underlying
    /// <see cref="MemoryPool{T}"/>, backed per the pool's <see cref="AllocationKind"/>. The caller owns
    /// the buffer and must dispose it. Useful for gathering a spanning token before interning it.
    /// </summary>
    /// <param name="length">The exact buffer length in bytes; must be positive.</param>
    /// <returns>An <see cref="IMemoryOwner{T}"/> over a buffer of exactly <paramref name="length"/> bytes.</returns>
    /// <exception cref="ObjectDisposedException">The pool has been disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is not positive.</exception>
    public IMemoryOwner<byte> RentScratch(int length)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);

        return RentBuffer(length);
    }


    /// <inheritdoc/>
    public void Dispose()
    {
        if(Disposed)
        {
            return;
        }

        Disposed = true;

        foreach(IMemoryOwner<byte> owner in Slabs)
        {
            owner.Dispose();
        }

        Slabs.Clear();
        Table.Clear();

        if(OwnsPool)
        {
            Pool.Dispose();
        }
    }


    private static BaseMemoryPool CreateDefaultPool()
    {
        //One segment per slab class so each 64 KB rental is exact (no 4x over-allocation), and tracing
        //off because an arena's many rentals would otherwise flood the activity stream.
        return new BaseMemoryPool(
            new Meter(BaseMemoryPoolMetrics.MeterName, "1.0.0"),
            capacityStrategy: static _ => 1,
            tracingEnabled: false);
    }


    private IMemoryOwner<byte> RentBuffer(int size)
    {
        //Managed goes through the abstract seam; a protected kind is only reachable when the pool is a
        //BaseMemoryPool (enforced at construction), so the cast always succeeds there.
        return AllocationKind != AllocationKind.Managed && Pool is BaseMemoryPool basePool
            ? basePool.Rent(size, AllocationKind)
            : Pool.Rent(size);
    }


    private Utf8String Allocate(ReadOnlySpan<byte> source)
    {
        //The comparer owns the hashing regime; compute the stamp through it so the stored hash is
        //exactly the bucketing hash, including any deterministic override.
        int hash = Comparer.GetHashCode(source);

        if(source.Length > SlabSize)
        {
            //Oversize values get a dedicated exact-size buffer and never disturb the active slab cursor.
            IMemoryOwner<byte> owner = RentBuffer(source.Length);
            Slabs.Add(owner);
            source.CopyTo(owner.Memory.Span);

            return new Utf8String(owner.Memory[..source.Length], hash);
        }

        if(CurrentBuffer.Length - Position < source.Length)
        {
            CurrentOwner = RentBuffer(SlabSize);
            CurrentBuffer = CurrentOwner.Memory;
            Slabs.Add(CurrentOwner);
            Position = 0;
        }

        source.CopyTo(CurrentBuffer.Span[Position..]);
        ReadOnlyMemory<byte> slice = CurrentBuffer.Slice(Position, source.Length);
        Position += source.Length;

        return new Utf8String(slice, hash);
    }
}
