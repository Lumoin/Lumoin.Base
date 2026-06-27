using System.Diagnostics;
using System.Globalization;

namespace Lumoin.Base;

/// <summary>
/// An immutable, non-owning view over UTF-8 encoded bytes with an optionally precomputed hash code.
/// </summary>
/// <remarks>
/// <para>
/// This is a byte-native string for use at encoding and I/O boundaries, where text is already UTF-8
/// and decoding to a UTF-16 <see cref="string"/> would only cost an allocation. IRIs, protocol tokens,
/// literal lexical forms, and similar values are kept as their UTF-8 bytes and compared, ordered, and
/// hashed directly over those bytes.
/// </para>
/// <para>
/// The type supports two construction modes that trade hash cost against lookup cost. The default
/// constructor computes the hash eagerly, which is the right trade-off for values used as dictionary
/// keys: paying once at construction avoids rehashing on every lookup. The
/// <see cref="WithoutPrecomputedHash(ReadOnlyMemory{byte})"/> factory and the slicing members skip the
/// eager hash, which is the right trade-off for blob-like or transient values that are not used as
/// keys. Calling <see cref="GetHashCode"/> on a deferred-hash instance recomputes the hash on every
/// call.
/// </para>
/// <para>
/// Equality and ordering operate directly on the underlying byte span and do not depend on the hash
/// code, so an eager-hash instance and a deferred-hash instance covering the same bytes compare equal
/// and (when both are hashed) hash equal.
/// </para>
/// <para>
/// <strong>No validation.</strong> Construction does not check that the bytes are well-formed UTF-8 —
/// a <see cref="Utf8String"/> is an opaque byte carrier and validation is the producing parser's job.
/// Use <see cref="TryFromUtf8(ReadOnlyMemory{byte}, out Utf8String)"/> at an untrusted boundary to
/// validate explicitly.
/// </para>
/// <para>
/// <strong>Ownership.</strong> Instances do not own the underlying memory; they are a borrowed view.
/// A <see cref="Utf8String"/> obtained from a <see cref="Utf8StringPool"/> is valid only while that
/// pool is undisposed. The owning, wipe-on-dispose counterpart is <see cref="SensitiveMemory"/>.
/// </para>
/// <para>
/// <strong>Hashing.</strong> <see cref="GetHashCode"/> is a 32-bit in-memory bucketing hash backed by
/// <see cref="HashCode"/>, which is seeded per process. It is stable within a process only and must
/// never be persisted or compared across processes. For a hashing regime that is stable across a
/// distributed system, intern through a <see cref="Utf8StringPool"/> built with a
/// <see cref="Utf8StringComparer"/> created from a deterministic <see cref="Utf8HashFunction"/>.
/// </para>
/// <para>
/// <strong>Comparison versus keying.</strong> The relational operators and <see cref="CompareTo"/> are
/// first-class for comparing values directly. <see cref="Utf8StringComparer"/> is for <em>collections</em>:
/// it adds the alternate-by-span face that lets a <see cref="HashSet{T}"/> / <see cref="Dictionary{TKey, TValue}"/>
/// of <see cref="Utf8String"/> be probed with a raw <see cref="ReadOnlySpan{T}"/> of <see cref="byte"/>.
/// You do not need the comparer to use <c>==</c>, <c>&lt;</c>, or <see cref="CompareTo"/>.
/// </para>
/// </remarks>
[DebuggerDisplay("\"{ToString(),nq}\" {DebuggerHashStatus,nq}")]
public readonly struct Utf8String:
    IEquatable<Utf8String>,
    IComparable<Utf8String>,
    ISpanFormattable,
    IUtf8SpanFormattable
{
    /// <summary>
    /// Sentinel stored in <see cref="PrecomputedHashCode"/> when the hash has not been precomputed.
    /// A real hash of zero is remapped to one by <see cref="NormalizeHash(int)"/> so the sentinel
    /// stays unambiguous; a one-bit collision is statistically negligible.
    /// </summary>
    private const int DeferredHashSentinel = 0;

    private ReadOnlyMemory<byte> Bytes { get; }
    private int PrecomputedHashCode { get; }


    /// <summary>
    /// Initializes a new <see cref="Utf8String"/> over the given UTF-8 bytes and computes the hash
    /// eagerly. Aliases <paramref name="utf8Bytes"/> (no copy) and performs no UTF-8 validation. Use
    /// this for values that participate in dictionary lookups.
    /// </summary>
    /// <param name="utf8Bytes">The UTF-8 encoded byte sequence to view.</param>
    public Utf8String(ReadOnlyMemory<byte> utf8Bytes)
    {
        Bytes = utf8Bytes;
        PrecomputedHashCode = ComputeHashCode(utf8Bytes.Span);
    }


    /// <summary>
    /// Private constructor for the deferred-hash modes (<see cref="WithoutPrecomputedHash"/> and the
    /// slicing members). The <paramref name="deferHash"/> parameter is a tag distinguishing this
    /// overload from the eager constructor; it is always <see langword="true"/> at the call site.
    /// </summary>
    /// <param name="utf8Bytes">The UTF-8 encoded byte sequence to view.</param>
    /// <param name="deferHash">Always <see langword="true"/>; tag parameter only.</param>
    private Utf8String(ReadOnlyMemory<byte> utf8Bytes, bool deferHash)
    {
        _ = deferHash;
        Bytes = utf8Bytes;
        PrecomputedHashCode = DeferredHashSentinel;
    }


    /// <summary>
    /// Internal constructor that views <paramref name="utf8Bytes"/> and stamps a precomputed hash,
    /// applying the same 0&#8594;1 remap as the eager path so a stamped real-zero hash is never misread
    /// as the deferred sentinel. Used by <see cref="Utf8StringPool"/> to record the bucketing hash it
    /// already computed.
    /// </summary>
    /// <param name="utf8Bytes">The UTF-8 encoded byte sequence to view.</param>
    /// <param name="precomputedHash">The hash to stamp; normalized through <see cref="NormalizeHash(int)"/>.</param>
    internal Utf8String(ReadOnlyMemory<byte> utf8Bytes, int precomputedHash)
    {
        Bytes = utf8Bytes;
        PrecomputedHashCode = NormalizeHash(precomputedHash);
    }


    /// <summary>
    /// Gets an empty <see cref="Utf8String"/>; equivalent to <see langword="default"/>. Like every
    /// <see langword="default"/> instance this is the <em>deferred-hash</em> form, so
    /// <see cref="HasPrecomputedHash"/> is <see langword="false"/> and <see cref="GetHashCode"/>
    /// recomputes on each call. It still compares and hashes equal to an eager empty instance such as
    /// <c>new Utf8String(ReadOnlyMemory&lt;byte&gt;.Empty)</c>; the difference is diagnostic only.
    /// </summary>
    public static Utf8String Empty => default;


    /// <summary>Gets the UTF-8 encoded bytes as a read-only span.</summary>
    public ReadOnlySpan<byte> Span => Bytes.Span;


    /// <summary>Gets the UTF-8 encoded bytes as a <see cref="ReadOnlyMemory{T}"/>.</summary>
    public ReadOnlyMemory<byte> Memory => Bytes;


    /// <summary>Gets the length in bytes (not characters).</summary>
    public int Length => Bytes.Length;


    /// <summary>Gets a value indicating whether this instance contains no bytes.</summary>
    public bool IsEmpty => Bytes.IsEmpty;


    /// <summary>
    /// Gets a value indicating whether this instance carries a precomputed hash. Deferred-hash
    /// instances recompute the hash on every <see cref="GetHashCode"/> call; this is a diagnostic, not
    /// a correctness, distinction (equal bytes always compare and hash equal).
    /// </summary>
    public bool HasPrecomputedHash => PrecomputedHashCode != DeferredHashSentinel;


    /// <summary>
    /// Creates a deferred-hash <see cref="Utf8String"/> over the given UTF-8 bytes without precomputing
    /// the hash. Suitable for blob-like values (document bodies, opaque payloads) that flow through once
    /// and are not used as dictionary keys. Aliases <paramref name="utf8Bytes"/> (no copy).
    /// </summary>
    /// <param name="utf8Bytes">The UTF-8 encoded byte sequence to view.</param>
    /// <returns>A new <see cref="Utf8String"/> over <paramref name="utf8Bytes"/> with the hash deferred.</returns>
    public static Utf8String WithoutPrecomputedHash(ReadOnlyMemory<byte> utf8Bytes)
    {
        return new Utf8String(utf8Bytes, deferHash: true);
    }


    /// <summary>
    /// Validates that <paramref name="utf8Bytes"/> is well-formed UTF-8 and, if so, returns an
    /// eager-hash view over it. The explicit, opt-in validation path for untrusted input; the ordinary
    /// constructors do not validate.
    /// </summary>
    /// <param name="utf8Bytes">The candidate UTF-8 bytes to view.</param>
    /// <param name="value">On success, an eager-hash <see cref="Utf8String"/> over <paramref name="utf8Bytes"/>; otherwise <see cref="Empty"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="utf8Bytes"/> is well-formed UTF-8.</returns>
    public static bool TryFromUtf8(ReadOnlyMemory<byte> utf8Bytes, out Utf8String value)
    {
        if(System.Text.Unicode.Utf8.IsValid(utf8Bytes.Span))
        {
            value = new Utf8String(utf8Bytes);

            return true;
        }

        value = Empty;

        return false;
    }


    /// <summary>Returns a deferred-hash view over the bytes from <paramref name="start"/> to the end.</summary>
    /// <param name="start">The starting byte offset.</param>
    /// <returns>A <see cref="Utf8String"/> view over the same backing memory.</returns>
    public Utf8String Slice(int start)
    {
        return new Utf8String(Bytes.Slice(start), deferHash: true);
    }


    /// <summary>Returns a deferred-hash view over <paramref name="length"/> bytes starting at <paramref name="start"/>.</summary>
    /// <param name="start">The starting byte offset.</param>
    /// <param name="length">The number of bytes.</param>
    /// <returns>A <see cref="Utf8String"/> view over the same backing memory.</returns>
    public Utf8String Slice(int start, int length)
    {
        return new Utf8String(Bytes.Slice(start, length), deferHash: true);
    }


    /// <summary>Returns a deferred-hash view over the bytes selected by <paramref name="range"/>.</summary>
    /// <param name="range">The byte range to view.</param>
    /// <returns>A <see cref="Utf8String"/> view over the same backing memory.</returns>
    public Utf8String this[Range range] => new(Bytes[range], deferHash: true);


    /// <summary>
    /// Determines whether this instance's bytes equal a raw UTF-8 byte sequence — the allocation-free
    /// way to compare against a <c>u8</c> literal without decoding to a <see cref="string"/>.
    /// </summary>
    /// <param name="other">The UTF-8 bytes to compare against.</param>
    /// <returns><see langword="true"/> when the byte sequences are equal.</returns>
    public bool SequenceEqual(ReadOnlySpan<byte> other)
    {
        return Bytes.Span.SequenceEqual(other);
    }


    /// <summary>Determines whether this instance starts with <paramref name="value"/>.</summary>
    /// <param name="value">The prefix bytes to test.</param>
    /// <returns><see langword="true"/> when the bytes start with <paramref name="value"/>.</returns>
    public bool StartsWith(ReadOnlySpan<byte> value)
    {
        return Bytes.Span.StartsWith(value);
    }


    /// <summary>Determines whether this instance ends with <paramref name="value"/>.</summary>
    /// <param name="value">The suffix bytes to test.</param>
    /// <returns><see langword="true"/> when the bytes end with <paramref name="value"/>.</returns>
    public bool EndsWith(ReadOnlySpan<byte> value)
    {
        return Bytes.Span.EndsWith(value);
    }


    /// <summary>Determines whether this instance contains <paramref name="value"/>.</summary>
    /// <param name="value">The byte to find.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is present.</returns>
    public bool Contains(byte value)
    {
        return Bytes.Span.Contains(value);
    }


    /// <summary>Determines whether this instance contains <paramref name="value"/> as a sub-sequence.</summary>
    /// <param name="value">The bytes to find.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> occurs.</returns>
    public bool Contains(ReadOnlySpan<byte> value)
    {
        return Bytes.Span.IndexOf(value) >= 0;
    }


    /// <summary>Returns the index of the first occurrence of <paramref name="value"/>, or -1.</summary>
    /// <param name="value">The byte to find.</param>
    /// <returns>The zero-based index, or -1 when not found.</returns>
    public int IndexOf(byte value)
    {
        return Bytes.Span.IndexOf(value);
    }


    /// <summary>Returns the index of the first occurrence of <paramref name="value"/>, or -1.</summary>
    /// <param name="value">The bytes to find.</param>
    /// <returns>The zero-based index, or -1 when not found.</returns>
    public int IndexOf(ReadOnlySpan<byte> value)
    {
        return Bytes.Span.IndexOf(value);
    }


    /// <summary>
    /// Returns the index of the first byte that matches any value in <paramref name="values"/>, or -1.
    /// The caller owns the <see cref="System.Buffers.SearchValues{T}"/> so it is built once and reused.
    /// </summary>
    /// <param name="values">The set of bytes to search for.</param>
    /// <returns>The zero-based index, or -1 when no byte matches.</returns>
    public int IndexOfAny(System.Buffers.SearchValues<byte> values)
    {
        return Bytes.Span.IndexOfAny(values);
    }


    /// <summary>
    /// Decodes the UTF-8 bytes to a .NET <see cref="string"/>. Allocates; intended for diagnostics and
    /// cold paths. Prefer operating on <see cref="Span"/> directly.
    /// </summary>
    /// <returns>The decoded string.</returns>
    public override string ToString()
    {
        return System.Text.Encoding.UTF8.GetString(Bytes.Span);
    }


    /// <inheritdoc/>
    /// <remarks>
    /// A <see cref="Utf8String"/> has no format specifiers; <paramref name="format"/> and
    /// <paramref name="formatProvider"/> are ignored.
    /// </remarks>
    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        return ToString();
    }


    /// <summary>
    /// Writes the decoded UTF-16 characters into <paramref name="destination"/>.
    /// </summary>
    /// <remarks>
    /// Implements <see cref="ISpanFormattable"/>. This is a real UTF-8 decode (not a byte copy);
    /// <paramref name="format"/> and <paramref name="provider"/> are ignored.
    /// </remarks>
    /// <param name="destination">The character span to write into.</param>
    /// <param name="charsWritten">The number of characters written.</param>
    /// <param name="format">Ignored.</param>
    /// <param name="provider">Ignored.</param>
    /// <returns><see langword="true"/> when <paramref name="destination"/> was large enough.</returns>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
    {
        return System.Text.Encoding.UTF8.TryGetChars(Bytes.Span, destination, out charsWritten);
    }


    /// <summary>
    /// Copies the raw UTF-8 bytes into <paramref name="utf8Destination"/>.
    /// </summary>
    /// <remarks>
    /// Implements <see cref="IUtf8SpanFormattable"/> so a <see cref="Utf8String"/> flows into
    /// <c>Utf8.TryWrite</c> interpolation holes without a decode. For a byte-native string this is a
    /// guarded copy; <paramref name="format"/> and <paramref name="provider"/> are ignored.
    /// </remarks>
    /// <param name="utf8Destination">The byte span to write into.</param>
    /// <param name="bytesWritten">The number of bytes written.</param>
    /// <param name="format">Ignored.</param>
    /// <param name="provider">Ignored.</param>
    /// <returns><see langword="true"/> when <paramref name="utf8Destination"/> was large enough.</returns>
    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
    {
        ReadOnlySpan<byte> source = Bytes.Span;
        if(source.TryCopyTo(utf8Destination))
        {
            bytesWritten = source.Length;

            return true;
        }

        bytesWritten = 0;

        return false;
    }


    /// <inheritdoc/>
    /// <remarks>
    /// Eager-hash instances return the value computed at construction; deferred-hash instances recompute
    /// the hash on every call. The value is a process-seeded in-memory bucketing hash and must not be
    /// persisted or compared across processes.
    /// </remarks>
    public override int GetHashCode()
    {
        return PrecomputedHashCode != DeferredHashSentinel
            ? PrecomputedHashCode
            : ComputeHashCode(Bytes.Span);
    }


    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        return obj is Utf8String other && Equals(other);
    }


    /// <inheritdoc/>
    public bool Equals(Utf8String other)
    {
        return Bytes.Span.SequenceEqual(other.Bytes.Span);
    }


    /// <summary>
    /// Compares two <see cref="Utf8String"/> instances lexicographically by unsigned UTF-8 byte value.
    /// </summary>
    /// <param name="other">The other instance to compare against.</param>
    /// <returns>A negative value, zero, or a positive value as this instance precedes, equals, or follows <paramref name="other"/>.</returns>
    public int CompareTo(Utf8String other)
    {
        return Bytes.Span.SequenceCompareTo(other.Bytes.Span);
    }


    /// <summary>Implicitly views a <see cref="Utf8String"/> as its raw UTF-8 bytes.</summary>
    /// <remarks>
    /// Lets a value flow into any <see cref="ReadOnlySpan{T}"/>-typed parameter — for example
    /// <c>a.SequenceEqual(b)</c> for two <see cref="Utf8String"/> values. Where an overload set offers both
    /// a <see cref="Utf8String"/> and a <see cref="ReadOnlySpan{T}"/> of <see cref="byte"/> parameter, this
    /// conversion can influence overload resolution; pass <see cref="Span"/> explicitly to be unambiguous.
    /// </remarks>
    /// <param name="value">The value to view.</param>
    public static implicit operator ReadOnlySpan<byte>(Utf8String value) => value.Bytes.Span;


    /// <inheritdoc/>
    public static bool operator ==(Utf8String left, Utf8String right) => left.Equals(right);


    /// <inheritdoc/>
    public static bool operator !=(Utf8String left, Utf8String right) => !left.Equals(right);


    /// <inheritdoc/>
    public static bool operator <(Utf8String left, Utf8String right) => left.CompareTo(right) < 0;


    /// <inheritdoc/>
    public static bool operator <=(Utf8String left, Utf8String right) => left.CompareTo(right) <= 0;


    /// <inheritdoc/>
    public static bool operator >(Utf8String left, Utf8String right) => left.CompareTo(right) > 0;


    /// <inheritdoc/>
    public static bool operator >=(Utf8String left, Utf8String right) => left.CompareTo(right) >= 0;


    /// <summary>
    /// Computes the bucketing hash for a UTF-8 byte sequence with <see cref="HashCode"/>, normalized so
    /// the result is never the deferred sentinel. Process-seeded; not stable across processes.
    /// </summary>
    /// <param name="span">The UTF-8 bytes to hash.</param>
    /// <returns>A non-zero hash derived from <paramref name="span"/>.</returns>
    internal static int ComputeHashCode(ReadOnlySpan<byte> span)
    {
        HashCode hash = new();
        hash.AddBytes(span);

        return NormalizeHash(hash.ToHashCode());
    }


    /// <summary>
    /// Remaps a raw hash of zero to one so it is never confused with the deferred sentinel. The single
    /// source of the remap, shared by the eager path, the stamping constructor, and
    /// <see cref="Utf8StringComparer"/>, so every collection buckets identically.
    /// </summary>
    /// <param name="rawHash">The hash to normalize.</param>
    /// <returns><paramref name="rawHash"/> unless it is zero, in which case one.</returns>
    internal static int NormalizeHash(int rawHash)
    {
        return rawHash != DeferredHashSentinel ? rawHash : 1;
    }


    /// <summary>Reports the hash state for the debugger without decoding the bytes when the hash is deferred.</summary>
    private string DebuggerHashStatus
        => PrecomputedHashCode != DeferredHashSentinel
            ? string.Create(CultureInfo.InvariantCulture, $"#{PrecomputedHashCode:X8}")
            : "(hash deferred)";
}
