using System.Buffers;

namespace Lumoin.Base;

/// <summary>
/// A shared, allocation-free <see cref="IMemoryOwner{T}"/> singleton over a zero-length buffer.
/// </summary>
/// <remarks>
/// <para>
/// Use <see cref="Instance"/> wherever an API needs an <see cref="IMemoryOwner{T}"/> but the payload is
/// empty — parsing a zero-length wire field, representing an "empty" buffer-backed value, or avoiding a
/// pool rental for trivially empty data. Some pools (such as <see cref="BaseMemoryPool"/>) reject
/// zero-size rentals, so this is the compatible stand-in.
/// </para>
/// <para>
/// <strong>Disposal is a no-op</strong>, so the singleton can be shared across many owners and used in a
/// <c>using</c> without one consumer's disposal affecting another. Types that wipe on disposal — notably
/// <see cref="SensitiveMemory"/> — recognize this owner and skip both the wipe and the disposed-state
/// transition, so a shared <c>Empty</c> value stays usable for every later caller.
/// </para>
/// <para>
/// <strong>Thread safety:</strong> immutable; <see cref="Instance"/> is safe to use concurrently.
/// </para>
/// </remarks>
public sealed class EmptyMemoryOwner: IMemoryOwner<byte>
{
    /// <summary>Gets the shared singleton instance.</summary>
    public static EmptyMemoryOwner Instance { get; } = new();


    /// <summary>Prevents external instantiation; use <see cref="Instance"/>.</summary>
    private EmptyMemoryOwner()
    {
    }


    /// <summary>Gets the empty backing memory.</summary>
    /// <value>Always <see cref="Memory{T}.Empty"/>.</value>
    public Memory<byte> Memory => Memory<byte>.Empty;


    /// <summary>Does nothing; there is no rented memory to return. Safe to call any number of times.</summary>
    public void Dispose()
    {
        //Intentionally empty: a shared singleton with no resources.
    }
}
