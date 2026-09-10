using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Lumoin.Base.MemoryProtection;

/// <summary>
/// Owns one page-aligned, locked native allocation and exposes it as exact-size
/// <see cref="Memory{T}"/>, satisfying the <see cref="Lumoin.Base.NativeBackingAllocator"/> owner
/// contract: disposal zeroes the whole locked region with
/// <see cref="CryptographicOperations.ZeroMemory(Span{byte})"/>, unlocks it, and frees it.
/// </summary>
/// <remarks>
/// <para>
/// Disposal is idempotent and thread-safe: the pointer is claimed once with an atomic exchange,
/// so concurrent or repeated disposals release exactly once. A finalizer backstops a leaked owner
/// so the locked pages are returned to the operating system even when a caller forgets to dispose
/// — but the finalizer gives no timing guarantee, so treating it as the mainline release path
/// holds locked-memory budget (<c>RLIMIT_MEMLOCK</c>, the Windows working-set quota) arbitrarily
/// long. Dispose deterministically.
/// </para>
/// <para>
/// Unlike the guarded tier in <c>Lumoin.Base.Libsodium</c>, the operating system provides no guard
/// pages or canary here — this owner locks and wipes, nothing more. Pair the backing with
/// <c>NativeRentMode.ProtectedSlab</c> when overrun detection is wanted: the pool's per-segment
/// software canaries supply it on top of this locked region.
/// </para>
/// </remarks>
internal sealed unsafe class ProtectedMemoryOwner: MemoryManager<byte>
{
    /// <summary>
    /// The exact rented length in bytes; the length of every span and memory handed out.
    /// </summary>
    private int Length { get; }

    /// <summary>
    /// The full page-rounded allocation length in bytes — the range that was locked and the range
    /// that is zeroed, unlocked, and freed on disposal. Validated to fit an <see cref="int"/> at
    /// allocation time so the disposal wipe can span it with a single <see cref="Span{T}"/>.
    /// </summary>
    private int LockedLength { get; }

    /// <summary>
    /// The page-aligned allocation pointer. Zero once disposed; claimed atomically so the region
    /// is released exactly once across concurrent disposals and the finalizer.
    /// </summary>
    private nint pointer;


    /// <summary>
    /// Initializes a new owner over a locked allocation already made by
    /// <see cref="MemoryProtectionBacking"/>.
    /// </summary>
    /// <param name="pointer">The non-null page-aligned pointer.</param>
    /// <param name="length">The exact rented length in bytes.</param>
    /// <param name="lockedLength">The page-rounded, locked allocation length in bytes.</param>
    public ProtectedMemoryOwner(nint pointer, int length, int lockedLength)
    {
        this.pointer = pointer;
        Length = length;
        LockedLength = lockedLength;
    }


    /// <summary>
    /// Backstop for a leaked owner: releases the locked pages when the caller never disposed.
    /// Deterministic disposal remains the contract; see the class remarks.
    /// </summary>
    [SuppressMessage("Reliability", "CA2015:Do not define finalizers for types derived from MemoryManager<T>",
        Justification = "Owner-ratified backstop, same decision as SodiumMemoryOwner: a leaked owner must not hold locked pages (they exhaust RLIMIT_MEMLOCK / the working-set quota and keep the secret resident indefinitely). The span-outliving-owner hazard CA2015 warns about is undefined behavior for ANY IMemoryOwner and is not removed by moving the free elsewhere.")]
    ~ProtectedMemoryOwner()
    {
        Dispose(disposing: false);
    }


    /// <summary>
    /// Gets a span over exactly the rented bytes.
    /// </summary>
    /// <returns>A span of exactly the rented length.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when this owner has been disposed.</exception>
    public override Span<byte> GetSpan()
    {
        nint currentPointer = pointer;
        ObjectDisposedException.ThrowIf(currentPointer == 0, this);

        return new Span<byte>((void*)currentPointer, Length);
    }


    /// <summary>
    /// Pins the buffer at the given offset. Native memory never moves, so no GC handle is taken;
    /// the returned handle carries the address and its disposal has nothing to release.
    /// </summary>
    /// <param name="elementIndex">The byte offset the handle points at.</param>
    /// <returns>A handle addressing <paramref name="elementIndex"/> bytes into the allocation.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="elementIndex"/> is negative or beyond the rented length. The
    /// length itself is allowed: an empty tail slice pins at the end without dereferencing it.
    /// </exception>
    /// <exception cref="ObjectDisposedException">Thrown when this owner has been disposed.</exception>
    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(elementIndex, Length);

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
    /// Zeroes, unlocks, and frees the locked region exactly once. The whole page-rounded range is
    /// wiped with <see cref="CryptographicOperations.ZeroMemory(Span{byte})"/> while still locked
    /// and resident, then unlocked (an unlock failure cannot be surfaced from disposal and the
    /// pages are freed regardless), then freed. Safe to call concurrently and repeatedly.
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
            //Zero while the pages are still locked and resident, so the wipe cannot race a
            //swap-out; only then release the lock and the pages.
            CryptographicOperations.ZeroMemory(new Span<byte>((void*)claimedPointer, LockedLength));

            if(OperatingSystem.IsWindows())
            {
                _ = NativeMethods.VirtualUnlock((void*)claimedPointer, (nuint)LockedLength);
            }
            else
            {
                _ = NativeMethods.Munlock((void*)claimedPointer, (nuint)LockedLength);
            }

            NativeMemory.AlignedFree((void*)claimedPointer);
        }
    }
}
