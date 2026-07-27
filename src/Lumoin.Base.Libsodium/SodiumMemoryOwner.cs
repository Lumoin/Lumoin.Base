using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;

namespace Lumoin.Base.Libsodium;

/// <summary>
/// Owns one <c>sodium_malloc</c> guarded allocation and exposes it as exact-size
/// <see cref="Memory{T}"/>, satisfying the <see cref="Lumoin.Base.NativeBackingAllocator"/> owner
/// contract: disposal zeroes (<c>sodium_memzero</c> first as defense-in-depth, then
/// <c>sodium_free</c> zeroes again), unlocks, and frees the native memory.
/// </summary>
/// <remarks>
/// <para>
/// Disposal is idempotent and thread-safe: the pointer is claimed once with an atomic exchange, so
/// concurrent or repeated disposals free exactly once. A finalizer backstops a leaked owner so the
/// locked allocation is returned to the operating system even when a caller forgets to dispose —
/// but the finalizer gives no timing guarantee, so treating it as the mainline release path leaves
/// secrets in locked memory arbitrarily long. Dispose deterministically.
/// </para>
/// <para>
/// The memory carries libsodium's hard-fail protections: writing past the end hits the trailing
/// guard page and crashes immediately (a deep underflow hits the leading guard page the same
/// way), and a small underflow corrupts the canary, which makes <c>sodium_free</c> ABORT THE
/// PROCESS at disposal. Both are deliberate loud failures inherited from libsodium, not
/// conditions this owner can translate into exceptions.
/// </para>
/// </remarks>
//Guarded native memory does not exist in the browser sandbox; the crypto surface of this package does.
[UnsupportedOSPlatform("browser")]
internal sealed unsafe class SodiumMemoryOwner: MemoryManager<byte>
{
    /// <summary>
    /// The exact allocation length in bytes; also the length of every span and memory handed out.
    /// </summary>
    private readonly int length;

    /// <summary>
    /// The <c>sodium_malloc</c> pointer. Zero once disposed; claimed atomically so the region is
    /// freed exactly once across concurrent disposals and the finalizer.
    /// </summary>
    private nint pointer;


    /// <summary>
    /// Initializes a new owner over an allocation already made by <see cref="NativeMethods.Malloc"/>.
    /// </summary>
    /// <param name="pointer">The non-null pointer returned by <see cref="NativeMethods.Malloc"/>.</param>
    /// <param name="length">The exact number of bytes allocated at <paramref name="pointer"/>.</param>
    public SodiumMemoryOwner(nint pointer, int length)
    {
        this.pointer = pointer;
        this.length = length;
    }


    /// <summary>
    /// Backstop for a leaked owner: releases the locked native allocation when the caller never
    /// disposed. Deterministic disposal remains the contract; see the class remarks.
    /// </summary>
    [SuppressMessage("Reliability", "CA2015:Do not define finalizers for types derived from MemoryManager<T>",
        Justification = "Owner-ratified backstop: a leaked owner must not leak mlocked pages (they exhaust the platform locked-memory budget and hold the secret resident indefinitely). The hazard CA2015 warns about — the span outliving its owner — is undefined behavior for ANY IMemoryOwner and does not disappear by moving the free into a SafeHandle finalizer; the guarded allocation at least turns such misuse into a loud crash instead of silent corruption.")]
    ~SodiumMemoryOwner()
    {
        Dispose(disposing: false);
    }


    /// <summary>
    /// Gets a span over exactly the allocated bytes.
    /// </summary>
    /// <returns>A span of exactly the allocation length.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when this owner has been disposed.</exception>
    public override Span<byte> GetSpan()
    {
        nint currentPointer = pointer;
        ObjectDisposedException.ThrowIf(currentPointer == 0, this);

        return new Span<byte>((void*)currentPointer, length);
    }


    /// <summary>
    /// Pins the buffer at the given offset. Native memory never moves, so no GC handle is taken;
    /// the returned handle carries the address and its disposal has nothing to release.
    /// </summary>
    /// <param name="elementIndex">The byte offset the handle points at.</param>
    /// <returns>A handle addressing <paramref name="elementIndex"/> bytes into the allocation.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="elementIndex"/> is negative or beyond the allocation length. The
    /// length itself is allowed: an empty tail slice pins at the end without dereferencing it.
    /// </exception>
    /// <exception cref="ObjectDisposedException">Thrown when this owner has been disposed.</exception>
    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(elementIndex, length);

        nint currentPointer = pointer;
        ObjectDisposedException.ThrowIf(currentPointer == 0, this);

        return new MemoryHandle((void*)(currentPointer + elementIndex));
    }


    /// <summary>
    /// Nothing to release: <see cref="Pin"/> takes no GC handle because native memory never moves.
    /// </summary>
    public override void Unpin()
    {
    }


    /// <summary>
    /// Zeroes and frees the allocation exactly once. <c>sodium_memzero</c> runs first as
    /// defense-in-depth, then <c>sodium_free</c> zeroes again, unlocks the pages, and releases
    /// them. Safe to call concurrently and repeatedly.
    /// </summary>
    /// <param name="disposing">
    /// <see langword="true"/> from <see cref="IDisposable.Dispose"/>, <see langword="false"/> from
    /// the finalizer backstop. The native release is identical either way.
    /// </param>
    protected override void Dispose(bool disposing)
    {
        nint claimedPointer = Interlocked.Exchange(ref pointer, 0);
        if(claimedPointer != 0)
        {
            NativeMethods.MemZero((void*)claimedPointer, (nuint)length);
            NativeMethods.Free((void*)claimedPointer);
        }
    }
}
