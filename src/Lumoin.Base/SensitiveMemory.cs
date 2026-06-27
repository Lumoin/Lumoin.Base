using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Base;

/// <summary>
/// Base class for sensitive data that carries out-of-band metadata via a <see cref="Tag"/>.
/// </summary>
[DebuggerDisplay("SensitiveData: {Tag}")]
public abstract class SensitiveData
{
    /// <summary>The metadata tag describing this sensitive data.</summary>
    public Tag Tag { get; }


    /// <summary>Initializes a new instance with the specified tag.</summary>
    /// <param name="tag">Tags the data with out-of-band information such as content type, origin, or purpose.</param>
    /// <exception cref="ArgumentNullException"><paramref name="tag"/> is <see langword="null"/>.</exception>
    protected SensitiveData(Tag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        Tag = tag;
    }
}


/// <summary>
/// A domain-agnostic, pooled, tagged block of sensitive memory: it owns an <see cref="IMemoryOwner{T}"/>, exposes
/// it only as read-only spans, and <strong>clears its bytes on disposal</strong>. The intended pattern is that a
/// producer rents from <see cref="BaseMemoryPool"/> (often via a <see cref="SlabBufferWriter"/>), transfers
/// ownership of the resulting <see cref="IMemoryOwner{T}"/> into a <see cref="SensitiveMemory"/> subclass, and the
/// consumer disposes that subclass — so a naked <see cref="byte"/> array never crosses a public boundary and the
/// buffer is wiped deterministically when done.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is specific to cryptography; it is equally the right carrier for potentially sensitive business,
/// financial, or reporting bytes. Higher layers derive concrete types (a serialized document, a key) that add
/// domain accessors. Equality is by content (constant in shape, not constant-time), so two values with identical
/// bytes are equal regardless of concrete type.
/// </para>
/// <para>
/// <strong>Threat model of the wipe.</strong> Clearing the bytes on disposal reduces exposure through memory
/// dumps, the page file, and later reuse of the buffer. It does <strong>not</strong> defend against a compromised
/// host, against a page written to disk by hibernation, or against any value that was ever copied into a
/// <see cref="string"/> or otherwise duplicated — once the bytes leave this owner the wipe cannot reach the copy.
/// The wipe is also only fully reliable on <strong>non-relocatable backing</strong>: the garbage collector may
/// relocate an <see cref="AllocationKind.Managed"/> array between writes, leaving a stale copy the clear cannot
/// reach. For the strongest guarantee, back the owner with <see cref="AllocationKind.Pinned"/> or
/// <see cref="AllocationKind.Native"/> memory from a <see cref="BaseMemoryPool"/>, which is never GC-relocated.
/// </para>
/// <para>
/// <strong>OpenTelemetry lifetime spans.</strong> When an <see cref="Activity"/> is supplied at
/// construction, this class bounds it to the value's lifetime — stopping it on disposal and stamping
/// <see cref="SensitiveMemoryTelemetry.LifetimeMs"/>. The caller starts the activity and stamps whatever
/// domain-specific provenance it needs (for cryptographic material the crypto backend stamps provider /
/// library / class / operation attributes for CBOM traceability); this primitive contributes only the
/// neutral lifetime duration. Pass <see langword="null"/> (the default) when no OTel listener is active —
/// that path is zero-cost.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public abstract class SensitiveMemory: SensitiveData, IDisposable, IEquatable<SensitiveMemory>
{
    private bool Disposed { get; set; }
    private Activity? Lifetime { get; }


    /// <summary>The owned sensitive bytes. Ownership transferred to this instance at construction.</summary>
    protected IMemoryOwner<byte> MemoryOwner { get; }


    /// <summary>Initializes a new instance, taking ownership of <paramref name="sensitiveMemory"/>.</summary>
    /// <param name="sensitiveMemory">The memory owner holding the sensitive bytes. Ownership transfers here.</param>
    /// <param name="tag">Metadata describing the contents — e.g. format, origin, classification.</param>
    /// <param name="lifetime">
    /// An optional OpenTelemetry <see cref="Activity"/> spanning this value's lifetime. Started by the
    /// caller before construction; stopped and stamped with <see cref="SensitiveMemoryTelemetry.LifetimeMs"/>
    /// on disposal. Pass <see langword="null"/> (the default) when no OTel listener is active.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="sensitiveMemory"/> is <see langword="null"/>.</exception>
    protected SensitiveMemory(IMemoryOwner<byte> sensitiveMemory, Tag tag, Activity? lifetime = null) : base(tag)
    {
        ArgumentNullException.ThrowIfNull(sensitiveMemory);
        MemoryOwner = sensitiveMemory;
        Lifetime = lifetime;
    }


    /// <summary>Exposes the bytes as a read-only span.</summary>
    /// <exception cref="ObjectDisposedException">This instance has been disposed.</exception>
    public ReadOnlySpan<byte> AsReadOnlySpan()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        return MemoryOwner.Memory.Span;
    }


    /// <summary>Exposes the bytes as read-only memory.</summary>
    /// <exception cref="ObjectDisposedException">This instance has been disposed.</exception>
    public ReadOnlyMemory<byte> AsReadOnlyMemory()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        return MemoryOwner.Memory;
    }


    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }


    /// <summary>Clears the bytes, returns the owned memory, and stops the OTel lifetime span if one was supplied.</summary>
    /// <param name="disposing"><see langword="true"/> when called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if(Disposed)
        {
            return;
        }

        //A shared EmptyMemoryOwner singleton (e.g. a concrete type's Empty value) must never be disposed or
        //marked disposed: its Dispose is a no-op, but setting the disposed flag would poison the singleton
        //for every later user. Returning here — without setting disposed — leaves the shared empty usable.
        if(MemoryOwner is EmptyMemoryOwner)
        {
            return;
        }

        if(disposing)
        {
            //Clear in case the underlying owner does not wipe on return; pooled owners also clear, so this is
            //belt-and-suspenders for the sensitive case.
            MemoryOwner.Memory.Span.Clear();
            MemoryOwner.Dispose();

            //If the caller bounded an OTel span to this value's lifetime, stop it and contribute the neutral
            //lifetime duration; all domain-specific provenance was stamped by the caller at construction.
            if(Lifetime is not null)
            {
                Lifetime.Stop();
                Lifetime.SetTag(SensitiveMemoryTelemetry.LifetimeMs, Lifetime.Duration.TotalMilliseconds);
            }
        }

        Disposed = true;
    }


    /// <inheritdoc/>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool Equals([NotNullWhen(true)] SensitiveMemory? other) =>
        other is not null && MemoryOwner.Memory.Span.SequenceEqual(other.MemoryOwner.Memory.Span);


    /// <inheritdoc/>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is SensitiveMemory other && Equals(other);


    /// <inheritdoc/>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static bool operator ==(SensitiveMemory? left, SensitiveMemory? right) =>
        left is null ? right is null : left.Equals(right);


    /// <inheritdoc/>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static bool operator !=(SensitiveMemory? left, SensitiveMemory? right) => !(left == right);


    /// <inheritdoc/>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.AddBytes(MemoryOwner.Memory.Span);

        return hash.ToHashCode();
    }


    /// <summary>
    /// Deliberately reports only the length and state — never the sensitive bytes — and guards against a disposed
    /// owner so inspecting a disposed value in the debugger cannot throw.
    /// </summary>
    private string DebuggerDisplay => Disposed
        ? $"{GetType().Name}: disposed"
        : $"{GetType().Name}: {MemoryOwner.Memory.Length} bytes";
}
