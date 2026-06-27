using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;

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

    /// <summary>Maximum UTF-8 byte count encoded on the stack in <see cref="Intern(string)"/>.</summary>
    private const int MaxStackallocBytes = 256;

    /// <summary>The hot-generation capacity that triggers a rotation; the live set is bounded to about twice this.</summary>
    private int MaxEntries { get; }

    /// <summary>Whether <see cref="Intern(ReadOnlySpan{byte})"/> rejects bytes that are not well-formed UTF-8.</summary>
    private bool ValidateOnIntern { get; }

    /// <summary>The comparer defining hashing and equality for interned values.</summary>
    private Utf8StringComparer Comparer { get; }

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
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxEntries"/> is not positive.</exception>
    public Utf8StringInterner(
        int maxEntries = DefaultMaxEntries,
        Utf8StringComparer? comparer = null,
        bool validateOnIntern = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);

        MaxEntries = maxEntries;
        ValidateOnIntern = validateOnIntern;
        Comparer = comparer ?? Utf8StringComparer.Ordinal;
        currentGenerations = new Generations(NewTable(), NewTable());
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

        Generations generations = System.Threading.Volatile.Read(ref currentGenerations);
        if(generations.Hot.GetAlternateLookup<ReadOnlySpan<byte>>().TryGetValue(utf8Bytes, out Utf8String hot))
        {
            return hot;
        }

        if(generations.Cold.GetAlternateLookup<ReadOnlySpan<byte>>().TryGetValue(utf8Bytes, out Utf8String cold))
        {
            //Promote a cold hit so it survives the next rotation.
            generations.Hot.TryAdd(cold, cold);

            return cold;
        }

        int hash = Comparer.GetHashCode(utf8Bytes);
        byte[] owned = new byte[utf8Bytes.Length];
        utf8Bytes.CopyTo(owned);
        Utf8String candidate = new(owned, hash);

        //GetOrAdd settles a race: if another thread interned the same bytes first, its value wins and the extra
        //array is discarded.
        Utf8String interned = generations.Hot.GetOrAdd(candidate, candidate);
        if(generations.Hot.Count >= MaxEntries)
        {
            Rotate(generations);
        }

        return interned;
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
        System.Threading.Volatile.Write(ref currentGenerations, new Generations(NewTable(), NewTable()));
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
            if(!ReferenceEquals(current, observed) || current.Hot.Count < MaxEntries)
            {
                return;
            }

            System.Threading.Volatile.Write(ref currentGenerations, new Generations(NewTable(), current.Hot));
        }
    }


    /// <summary>Creates an empty generation table keyed by the interner's comparer.</summary>
    /// <returns>A new concurrent table.</returns>
    private ConcurrentDictionary<Utf8String, Utf8String> NewTable()
    {
        return new ConcurrentDictionary<Utf8String, Utf8String>(Comparer);
    }


    /// <summary>A hot/cold pair of generation tables, swapped atomically on rotation.</summary>
    /// <param name="Hot">The generation new and recently hit values live in.</param>
    /// <param name="Cold">The previous generation, dropped on the next rotation.</param>
    private sealed record Generations(
        ConcurrentDictionary<Utf8String, Utf8String> Hot,
        ConcurrentDictionary<Utf8String, Utf8String> Cold);
}
