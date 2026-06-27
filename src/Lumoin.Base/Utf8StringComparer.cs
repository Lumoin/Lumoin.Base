namespace Lumoin.Base;

/// <summary>
/// Computes a 32-bit hash over a UTF-8 byte sequence. Supplied to
/// <see cref="Utf8StringComparer.Create(Utf8HashFunction)"/> to replace the default process-seeded
/// hashing with a deterministic one — for example to make a <see cref="Utf8StringPool"/>'s bucketing
/// stable across processes in a distributed system. The function should be pure and depend only on the
/// bytes; the comparer normalizes a result of zero to one internally.
/// </summary>
/// <param name="utf8Bytes">The UTF-8 bytes to hash.</param>
/// <returns>A hash derived solely from <paramref name="utf8Bytes"/>.</returns>
public delegate int Utf8HashFunction(ReadOnlySpan<byte> utf8Bytes);


/// <summary>
/// Equality, ordering, and alternate-by-span lookup for <see cref="Utf8String"/>. As an
/// <see cref="IAlternateEqualityComparer{TAlternate, T}"/> over <see cref="ReadOnlySpan{T}"/> of
/// <see cref="byte"/>, it lets a <see cref="HashSet{T}"/>, <see cref="Dictionary{TKey, TValue}"/>, or
/// <see cref="System.Collections.Frozen.FrozenSet{T}"/> of <see cref="Utf8String"/> be probed with a
/// raw UTF-8 span — a <c>u8</c> literal or a freshly-lexed token — at zero allocation on a hit.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Ordinal"/> uses <see cref="Utf8String"/>'s default process-seeded hash. Build a comparer
/// from a deterministic <see cref="Utf8HashFunction"/> with <see cref="Create(Utf8HashFunction)"/> when
/// the bucketing must be stable across processes; pass that comparer to a <see cref="Utf8StringPool"/>
/// and the pool stamps interned values with the same hash.
/// </para>
/// <para>
/// Both hashing faces (<see cref="GetHashCode(Utf8String)"/> and <see cref="GetHashCode(ReadOnlySpan{byte})"/>)
/// agree for equal bytes, which is what makes the alternate lookup correct.
/// </para>
/// </remarks>
public sealed class Utf8StringComparer:
    IEqualityComparer<Utf8String>,
    IComparer<Utf8String>,
    IAlternateEqualityComparer<ReadOnlySpan<byte>, Utf8String>
{
    private Utf8HashFunction? HashFunction { get; }


    private Utf8StringComparer(Utf8HashFunction? hashFunction)
    {
        HashFunction = hashFunction;
    }


    /// <summary>
    /// Gets the ordinal comparer that hashes with <see cref="Utf8String"/>'s default process-seeded
    /// hash. Equality and ordering are over unsigned UTF-8 byte value.
    /// </summary>
    public static Utf8StringComparer Ordinal { get; } = new(hashFunction: null);


    /// <summary>
    /// Creates an ordinal comparer that hashes with <paramref name="hashFunction"/> instead of the
    /// default. Equality and ordering are unchanged (always over the bytes); only the hash differs. Use
    /// a deterministic function for a hashing regime that is stable across processes.
    /// </summary>
    /// <param name="hashFunction">The hash function to use.</param>
    /// <returns>A comparer using <paramref name="hashFunction"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="hashFunction"/> is <see langword="null"/>.</exception>
    public static Utf8StringComparer Create(Utf8HashFunction hashFunction)
    {
        ArgumentNullException.ThrowIfNull(hashFunction);

        return new Utf8StringComparer(hashFunction);
    }


    /// <inheritdoc/>
    public bool Equals(Utf8String x, Utf8String y)
    {
        return x.Equals(y);
    }


    /// <inheritdoc/>
    public int GetHashCode(Utf8String obj)
    {
        return HashFunction is null
            ? obj.GetHashCode()
            : Utf8String.NormalizeHash(HashFunction(obj.Span));
    }


    /// <inheritdoc/>
    public int Compare(Utf8String x, Utf8String y)
    {
        return x.CompareTo(y);
    }


    /// <summary>
    /// Determines whether a raw UTF-8 span equals the bytes of a <see cref="Utf8String"/>.
    /// </summary>
    /// <param name="alternate">The raw UTF-8 bytes.</param>
    /// <param name="other">The value to compare against.</param>
    /// <returns><see langword="true"/> when the byte sequences are equal.</returns>
    public bool Equals(ReadOnlySpan<byte> alternate, Utf8String other)
    {
        return other.SequenceEqual(alternate);
    }


    /// <summary>
    /// Computes the hash of a raw UTF-8 span, agreeing with <see cref="GetHashCode(Utf8String)"/> for
    /// equal bytes so the alternate lookup buckets consistently.
    /// </summary>
    /// <param name="alternate">The raw UTF-8 bytes.</param>
    /// <returns>The bucketing hash for <paramref name="alternate"/>.</returns>
    public int GetHashCode(ReadOnlySpan<byte> alternate)
    {
        return HashFunction is null
            ? Utf8String.ComputeHashCode(alternate)
            : Utf8String.NormalizeHash(HashFunction(alternate));
    }


    /// <summary>
    /// Always throws. Materializing a <see cref="Utf8String"/> from a span by implicit insertion is
    /// disallowed because it would create an off-arena, undeduplicated, lifetime-detached value; the
    /// only sanctioned way to materialize a value is to intern it through
    /// <see cref="Utf8StringPool.Intern(ReadOnlySpan{byte})"/> (pooled memory, never a heap allocation).
    /// This affects only the alternate lookup's mutating operations (<c>Add</c>/<c>TryAdd</c>); probing
    /// (<c>TryGetValue</c>/<c>Contains</c>) never calls it.
    /// </summary>
    /// <param name="alternate">The raw UTF-8 bytes.</param>
    /// <returns>Never returns; always throws.</returns>
    /// <exception cref="NotSupportedException">Always.</exception>
    public Utf8String Create(ReadOnlySpan<byte> alternate)
    {
        throw new NotSupportedException(
            "Span-keyed insertion is not supported; intern through Utf8StringPool.Intern.");
    }
}
