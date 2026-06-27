using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Lumoin.Base;

/// <summary>
/// A process-wide, thread-safe interner that maps recurring UTF-8 byte sequences (and .NET strings) to a single
/// shared <see cref="Utf8String"/> over managed memory, and stays bounded by evicting its coldest entries.
/// </summary>
/// <remarks>
/// <para>
/// This is the concurrent, self-bounding counterpart to <see cref="Utf8StringPool"/>. Where the pool is a
/// single-writer arena that packs interned bytes into shared slabs freed only in bulk — ideal for a bounded,
/// long-lived vocabulary owned by one writer — the interner is built for the open, concurrent case: many threads
/// interning a stream of values whose working set must not grow without bound.
/// </para>
/// <para>
/// <strong>Bounding and eviction.</strong> Entries live in two generations, hot and cold. New and recently hit
/// values are in the hot generation; when it fills past <see cref="MaxEntries"/> the cold generation is dropped,
/// the hot one becomes cold, and a fresh hot generation starts. The live working set is thus bounded to roughly
/// twice <see cref="MaxEntries"/>, and cold values are evicted automatically. <see cref="Clear"/> drops everything.
/// </para>
/// <para>
/// <strong>Why eviction is safe.</strong> Interned memory is managed (a per-value array), and every returned
/// <see cref="Utf8String"/> holds a <see cref="ReadOnlyMemory{T}"/> over it. Dropping a generation only releases
/// the interner's own reference; any <see cref="Utf8String"/> a caller still holds keeps its own bytes alive
/// through that memory, and the bytes are reclaimed only once no view remains. There is therefore no dangling
/// view — eviction can never invalidate a value already handed out, unlike freeing an arena slab. This is also
/// why the interner is managed-only: it relies on GC liveness, so it cannot place interned bytes in
/// <see cref="AllocationKind.Native"/> or <see cref="AllocationKind.Pinned"/> memory; use a scoped
/// <see cref="Utf8StringPool"/> for that.
/// </para>
/// <para>
/// <strong>Concurrency.</strong> Lookups and interns are safe from any number of threads. A cache hit is
/// lock-free; only a generation rotation takes a short lock. Reading a returned <see cref="Utf8String"/> never
/// blocks and is always valid.
/// </para>
/// <para>
/// <strong>Secrets.</strong> As with <see cref="Utf8StringPool"/>, interning is a poor fit for secrets — a shared
/// copy is retained until evicted — so hold those in <see cref="SensitiveMemory"/> instead.
/// </para>
/// <para>
/// <strong>Untrusted input.</strong> The default <see cref="Utf8StringComparer.Ordinal"/> hash is deterministic, so
/// interned hashes are stable across processes — but that also makes it predictable, and an adversary feeding
/// crafted colliding keys can push a generation's bucket toward linear lookups. Two-generation eviction bounds the
/// damage (the resident set, and so the worst-case collision chain, stays near twice <see cref="MaxEntries"/>), but
/// for an ambient interner exposed to untrusted input keep <see cref="MaxEntries"/> modest, cap value size with the
/// constructor's maximum value length, or supply a keyed comparer where cross-process hash stability is not required.
/// </para>
/// </remarks>
[DebuggerDisplay("Utf8StringInterner: Count={Count}, MaxEntries={MaxEntries}")]
public sealed class Utf8StringInterner
{
    /// <summary>
    /// The ambient interner used where an explicit one is not threaded through. The application installs one at
    /// startup; it is <see langword="null"/> until set, and the library never creates one implicitly. Intended to
    /// be installed once at startup; the setter is not safe for concurrent reassignment. Do not intern secrets
    /// through the ambient interner — scope a dedicated one for those.
    /// </summary>
    public static Utf8StringInterner? Instance { get; set; }


    /// <summary>The default <see cref="MaxEntries"/> when none is supplied.</summary>
    private const int DefaultMaxEntries = 1 << 16;

    /// <summary>The default <see cref="MaxValueLength"/>: no limit, so every value is cacheable.</summary>
    private const int DefaultMaxValueLength = int.MaxValue;

    /// <summary>Maximum UTF-8 byte count encoded on the stack in <see cref="Intern(string)"/>.</summary>
    private const int MaxStackallocBytes = 256;

    /// <summary>The hot-generation capacity that triggers a rotation; the live set is bounded to about twice this.</summary>
    private int MaxEntries { get; }

    /// <summary>Values longer than this (in UTF-8 bytes) are returned uncached, so no single value can bloat the resident set.</summary>
    private int MaxValueLength { get; }

    /// <summary>Whether <see cref="Intern(ReadOnlySpan{byte})"/> rejects bytes that are not well-formed UTF-8.</summary>
    private bool ValidateOnIntern { get; }

    /// <summary>The comparer defining hashing and equality for interned values.</summary>
    private Utf8StringComparer Comparer { get; }

    /// <summary>Counts every intern operation (hit or miss), or <see langword="null"/> when no meter was supplied.</summary>
    private Counter<long>? InternOperationsCounter { get; }

    /// <summary>Counts intern cache hits, or <see langword="null"/> when no meter was supplied.</summary>
    private Counter<long>? InternHitsCounter { get; }

    /// <summary>Counts generation rotations, or <see langword="null"/> when no meter was supplied.</summary>
    private Counter<long>? RotationsCounter { get; }

    /// <summary>Serializes generation rotations so at most one runs at a time.</summary>
    private object RotationGate { get; } = new();

    /// <summary>
    /// The hot and cold generations, swapped atomically on rotation. A naked field rather than a property because
    /// it is published across threads through <see cref="System.Threading.Volatile"/>, which requires by-ref access.
    /// </summary>
    private Generations currentGenerations;


    /// <summary>
    /// Initializes an interner whose live working set is bounded to roughly twice <paramref name="maxEntries"/>.
    /// </summary>
    /// <param name="maxEntries">The hot-generation capacity that triggers a rotation. Defaults to 65,536.</param>
    /// <param name="comparer">The comparer that defines hashing/equality, or <see langword="null"/> for <see cref="Utf8StringComparer.Ordinal"/>.</param>
    /// <param name="validateOnIntern">When <see langword="true"/> (the default), <see cref="Intern(ReadOnlySpan{byte})"/> rejects bytes that are not well-formed UTF-8. Pass <see langword="false"/> to opt out on trusted, hot paths.</param>
    /// <param name="maxValueLength">Values longer than this (in UTF-8 bytes) are returned uncached, so no single value can bloat the resident set. Defaults to no limit.</param>
    /// <param name="meter">Optional meter for intern metrics. When <see langword="null"/>, no metrics are recorded.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxEntries"/> or <paramref name="maxValueLength"/> is not positive.</exception>
    public Utf8StringInterner(
        int maxEntries = DefaultMaxEntries,
        Utf8StringComparer? comparer = null,
        bool validateOnIntern = true,
        int maxValueLength = DefaultMaxValueLength,
        Meter? meter = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxValueLength);

        MaxEntries = maxEntries;
        MaxValueLength = maxValueLength;
        ValidateOnIntern = validateOnIntern;
        Comparer = comparer ?? Utf8StringComparer.Ordinal;
        currentGenerations = new Generations(NewTable(), NewTable());

        if(meter is not null)
        {
            InternOperationsCounter = meter.CreateCounter<long>(
                Utf8StringInternerMetrics.InternOperationsTotal,
                "operations",
                "Total intern operations (hits + misses).");

            InternHitsCounter = meter.CreateCounter<long>(
                Utf8StringInternerMetrics.InternHitsTotal,
                "operations",
                "Intern cache hit count (hot or cold value returned without allocation).");

            RotationsCounter = meter.CreateCounter<long>(
                Utf8StringInternerMetrics.RotationsTotal,
                "operations",
                "Generation rotations performed (each evicts a cold generation).");

            meter.CreateObservableUpDownCounter(
                Utf8StringInternerMetrics.LiveCount,
                () => Count,
                "strings",
                "Approximate live interned value count across both generations.");
        }
    }


    /// <summary>Gets the approximate number of live interned values across both generations.</summary>
    public int Count
    {
        get
        {
            Generations generations = System.Threading.Volatile.Read(ref currentGenerations);

            return generations.Hot.Count + generations.Cold.Count;
        }
    }


    /// <summary>
    /// Interns a UTF-8 byte sequence, returning a shared <see cref="Utf8String"/> over managed memory. Identical
    /// bytes return the same value until it is evicted.
    /// </summary>
    /// <param name="utf8Bytes">The UTF-8 bytes to intern.</param>
    /// <returns>An interned <see cref="Utf8String"/>.</returns>
    /// <exception cref="ArgumentException">Validation is enabled and <paramref name="utf8Bytes"/> is not well-formed UTF-8.</exception>
    public Utf8String Intern(ReadOnlySpan<byte> utf8Bytes)
    {
        if(ValidateOnIntern && !System.Text.Unicode.Utf8.IsValid(utf8Bytes))
        {
            throw new ArgumentException("The bytes are not well-formed UTF-8.", nameof(utf8Bytes));
        }

        InternOperationsCounter?.Add(1);

        Generations generations = System.Threading.Volatile.Read(ref currentGenerations);
        if(generations.Hot.GetAlternateLookup<ReadOnlySpan<byte>>().TryGetValue(utf8Bytes, out Utf8String hot))
        {
            InternHitsCounter?.Add(1);

            return hot;
        }

        if(generations.Cold.GetAlternateLookup<ReadOnlySpan<byte>>().TryGetValue(utf8Bytes, out Utf8String cold))
        {
            InternHitsCounter?.Add(1);

            //Promote a cold hit so it survives the next rotation, counting it toward the hot fill and rotating if it tips the threshold.
            if(generations.Hot.TryAdd(cold, cold) && System.Threading.Interlocked.Increment(ref generations.HotCount) >= MaxEntries)
            {
                Rotate(generations);
            }

            return cold;
        }

        int hash = Comparer.GetHashCode(utf8Bytes);
        byte[] owned = new byte[utf8Bytes.Length];
        utf8Bytes.CopyTo(owned);
        Utf8String candidate = new(owned, hash);

        //An oversized value is returned without caching, so no single outsized value can bloat the resident set.
        if(utf8Bytes.Length > MaxValueLength)
        {
            return candidate;
        }

        //TryAdd settles the race and reports whether this thread published the value: only a genuine add bumps the
        //hot count (kept with Interlocked so the rotation trigger never locks the table) and can tip a rotation.
        if(generations.Hot.TryAdd(candidate, candidate))
        {
            if(System.Threading.Interlocked.Increment(ref generations.HotCount) >= MaxEntries)
            {
                Rotate(generations);
            }

            return candidate;
        }

        //Another thread interned the same bytes first; its value wins and this extra array is discarded.
        return generations.Hot.TryGetValue(candidate, out Utf8String winner) ? winner : candidate;
    }


    /// <summary>Interns a .NET string by encoding it as UTF-8.</summary>
    /// <param name="value">The string to intern.</param>
    /// <returns>An interned <see cref="Utf8String"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public Utf8String Intern(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        int maxByteCount = System.Text.Encoding.UTF8.GetMaxByteCount(value.Length);
        if(maxByteCount <= MaxStackallocBytes)
        {
            Span<byte> buffer = stackalloc byte[maxByteCount];
            int written = System.Text.Encoding.UTF8.GetBytes(value, buffer);

            return Intern(buffer[..written]);
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(maxByteCount);
        try
        {
            int written = System.Text.Encoding.UTF8.GetBytes(value, rented);

            return Intern(rented.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }


    /// <summary>Probes for an existing interned value without interning a miss.</summary>
    /// <param name="utf8Bytes">The UTF-8 bytes to look up.</param>
    /// <param name="value">On success, the interned value; otherwise <see cref="Utf8String.Empty"/>.</param>
    /// <returns><see langword="true"/> when the value is currently interned.</returns>
    public bool TryGet(ReadOnlySpan<byte> utf8Bytes, out Utf8String value)
    {
        Generations generations = System.Threading.Volatile.Read(ref currentGenerations);
        if(generations.Hot.GetAlternateLookup<ReadOnlySpan<byte>>().TryGetValue(utf8Bytes, out value))
        {
            return true;
        }

        return generations.Cold.GetAlternateLookup<ReadOnlySpan<byte>>().TryGetValue(utf8Bytes, out value);
    }


    /// <summary>
    /// Drops every interned value, returning the interner to empty. Values a caller still holds stay valid; their
    /// managed bytes are reclaimed once no view remains.
    /// </summary>
    public void Clear()
    {
        //Serialize with Rotate so a rotation in flight cannot overwrite the cleared state and resurrect a generation.
        lock(RotationGate)
        {
            System.Threading.Volatile.Write(ref currentGenerations, new Generations(NewTable(), NewTable()));
        }
    }


    /// <summary>
    /// Rotates generations once the hot one is full: the cold generation is dropped, the hot becomes cold, and a
    /// fresh hot generation starts. A no-op when another thread already rotated past the observed state.
    /// </summary>
    /// <param name="observed">The generations the caller saw full; the rotation is skipped if it is no longer current.</param>
    private void Rotate(Generations observed)
    {
        lock(RotationGate)
        {
            Generations current = System.Threading.Volatile.Read(ref currentGenerations);
            if(!ReferenceEquals(current, observed) || current.HotCount < MaxEntries)
            {
                return;
            }

            System.Threading.Volatile.Write(ref currentGenerations, new Generations(NewTable(), current.Hot));
            RotationsCounter?.Add(1);
        }
    }


    /// <summary>Creates an empty generation table keyed by the interner's comparer.</summary>
    /// <returns>A new concurrent table.</returns>
    private ConcurrentDictionary<Utf8String, Utf8String> NewTable()
    {
        return new ConcurrentDictionary<Utf8String, Utf8String>(Comparer);
    }


    /// <summary>A hot/cold pair of generation tables plus the hot generation's add count, swapped atomically on rotation.</summary>
    private sealed class Generations(
        ConcurrentDictionary<Utf8String, Utf8String> hot,
        ConcurrentDictionary<Utf8String, Utf8String> cold)
    {
        /// <summary>The generation new and recently hit values live in.</summary>
        public ConcurrentDictionary<Utf8String, Utf8String> Hot { get; } = hot;

        /// <summary>The previous generation, dropped on the next rotation.</summary>
        public ConcurrentDictionary<Utf8String, Utf8String> Cold { get; } = cold;

        /// <summary>
        /// The number of entries added to <see cref="Hot"/>, maintained with
        /// <see cref="System.Threading.Interlocked"/> so the rotation trigger never has to lock the table for
        /// <see cref="ConcurrentDictionary{TKey, TValue}.Count"/>. A naked field because it is updated by-ref.
        /// </summary>
        public int HotCount;
    }
}
