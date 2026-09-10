using System.Buffers;
using System.Runtime.InteropServices;

namespace Lumoin.Base.MemoryProtection;

/// <summary>
/// The operating-system-twin implementation of the <see cref="NativeBackingAllocator"/> seam:
/// wire <see cref="Allocate"/> (or the cached <see cref="Allocator"/> delegate) into a
/// <see cref="BaseMemoryPool"/> and every <c>AllocationKind.Native</c> rent becomes a
/// page-aligned allocation locked into physical memory — <c>VirtualLock</c> on Windows,
/// <c>mlock</c> plus best-effort <c>MADV_DONTDUMP</c> on Linux and Android, <c>mlock</c> on Apple
/// platforms and FreeBSD — zeroed over its whole locked range on disposal.
/// </summary>
/// <remarks>
/// <para>
/// <code>
/// using var pool = new BaseMemoryPool(nativeBacking: MemoryProtectionBacking.Allocate);
/// using var secret = pool.Rent(32, AllocationKind.Native);
/// </code>
/// </para>
/// <para>
/// This package carries no native binaries and no dependencies beyond <c>Lumoin.Base</c>: the
/// imports target <c>kernel32</c> and <c>libc</c>, which every supported operating system already
/// ships. That is its role in the family — the locked native tier for consumers that cannot or
/// do not want to carry the libsodium-backed <c>Lumoin.Base.Libsodium</c>.
/// </para>
/// <para>
/// What locking buys and what it does not: locked pages cannot be swapped to disk (and on
/// Linux/Android are best-effort excluded from core dumps — the <c>madvise</c> advice is
/// swallowed on failure, so an old kernel or restricted container may still capture them), which
/// closes the paging exposure; it does NOT defend against an in-process reader, a live-process
/// memory dump, or hibernation images, and the operating system provides no guard pages or
/// canary here — unlike <c>sodium_malloc</c>, an overrun is not detected by this backing. Pair
/// the backing with <c>NativeRentMode.ProtectedSlab</c> when overrun detection is wanted: the
/// pool's per-segment software canaries run on top of this locked region.
/// </para>
/// <para>
/// Capacity is a real budget: every allocation is page-rounded (a 32-byte per-rent secret costs a
/// whole page of locked memory — <c>NativeRentMode.ProtectedSlab</c> amortizes this), Windows
/// caps locked pages by the process minimum working set (raise with
/// <c>SetProcessWorkingSetSize</c>), and POSIX systems cap them by <c>RLIMIT_MEMLOCK</c>
/// (famously 64 KB on Android). Exceeding the budget throws
/// <see cref="InsufficientMemoryException"/> — strict and loud, per family discipline, never a
/// silent fallback to unlocked memory.
/// </para>
/// </remarks>
public static class MemoryProtectionBacking
{
    /// <summary>
    /// Indicates whether this process runs on an operating system with a supported locking
    /// mechanism. A host typically wires the backing only when this is <see langword="true"/> and
    /// otherwise constructs its pool without a native backing, keeping
    /// <see cref="BaseMemoryPool"/>'s strict-by-default semantics; <see cref="Allocate"/> on an
    /// unsupported platform throws <see cref="PlatformNotSupportedException"/>.
    /// </summary>
    public static bool IsSupported =>
        OperatingSystem.IsWindows()
        || OperatingSystem.IsLinux()
        || OperatingSystem.IsAndroid()
        || OperatingSystem.IsMacOS()
        || OperatingSystem.IsMacCatalyst()
        || OperatingSystem.IsIOS()
        || OperatingSystem.IsTvOS()
        || OperatingSystem.IsFreeBSD();


    /// <summary>
    /// A cached <see cref="NativeBackingAllocator"/> delegate over <see cref="Allocate"/>, so
    /// callers wiring several pools do not allocate a fresh delegate per method-group conversion.
    /// </summary>
    public static NativeBackingAllocator Allocator { get; } = Allocate;


    /// <summary>
    /// Allocates exactly <paramref name="size"/> bytes of page-aligned, locked native memory and
    /// returns the owner whose disposal zeroes, unlocks, and frees it. This method's signature
    /// matches <see cref="NativeBackingAllocator"/>, so it wires directly:
    /// <c>new BaseMemoryPool(nativeBacking: MemoryProtectionBacking.Allocate)</c>.
    /// </summary>
    /// <param name="size">The exact number of bytes to allocate.</param>
    /// <returns>An owner of exactly <paramref name="size"/> bytes of locked native memory.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="size"/> is not positive, or so large that its page-rounded
    /// region cannot be spanned.
    /// </exception>
    /// <exception cref="PlatformNotSupportedException">
    /// Thrown when the operating system has no supported locking mechanism (see
    /// <see cref="IsSupported"/>), or when a nominally supported system's C library cannot be
    /// bound even through <see cref="LibcResolver"/>'s probing (glibc, Apple, FreeBSD, and musl
    /// names) — the loader failure is the inner exception rather than escaping raw.
    /// </exception>
    /// <exception cref="InsufficientMemoryException">
    /// Thrown when the platform refuses to lock the pages — the locked-memory budget is exhausted
    /// (<c>RLIMIT_MEMLOCK</c>, the Windows minimum working set). The message names the platform
    /// error and the knob that raises the budget. This is deliberate strictness: the backing
    /// never hands back unlocked memory as if it were locked.
    /// </exception>
    public static unsafe IMemoryOwner<byte> Allocate(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);

        if(!IsSupported)
        {
            throw new PlatformNotSupportedException(
                "This operating system has no supported memory-locking mechanism (VirtualLock/mlock); construct the pool without a native backing instead. See MemoryProtectionBacking.IsSupported.");
        }

        //Page-granular allocation is load-bearing, not an optimization: POSIX mlock/munlock are
        //not reference counted, so two allocations sharing a page would silently unlock each
        //other. Every allocation therefore owns whole pages exclusively.
        int pageSize = Environment.SystemPageSize;
        long roundedLength = (((long)size + pageSize) - 1) / pageSize * pageSize;
        if(roundedLength > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(size),
                $"A rental of {size} bytes page-rounds to {roundedLength} bytes, above the maximum lockable allocation of {int.MaxValue} bytes.");
        }

        int lockedLength = (int)roundedLength;
        void* pointer = NativeMemory.AlignedAlloc((nuint)lockedLength, (nuint)pageSize);

        try
        {
            if(OperatingSystem.IsWindows())
            {
                if(!NativeMethods.VirtualLock(pointer, (nuint)lockedLength))
                {
                    int error = Marshal.GetLastPInvokeError();
                    NativeMemory.AlignedFree(pointer);

                    throw new InsufficientMemoryException(
                        $"VirtualLock failed with Win32 error {error} ({Marshal.GetPInvokeErrorMessage(error)}) for a {lockedLength}-byte region. Windows caps locked pages by the process minimum working set; raise it with SetProcessWorkingSetSize to hold more locked memory.");
                }
            }
            else
            {
                if(NativeMethods.Mlock(pointer, (nuint)lockedLength) != 0)
                {
                    int error = Marshal.GetLastPInvokeError();
                    NativeMemory.AlignedFree(pointer);

                    throw new InsufficientMemoryException(
                        $"mlock failed with errno {error} ({Marshal.GetPInvokeErrorMessage(error)}) for a {lockedLength}-byte region. The locked-memory limit (RLIMIT_MEMLOCK, e.g. `ulimit -l`) likely needs raising; on Android the budget is famously 64 KB.");
                }

                if(OperatingSystem.IsLinux() || OperatingSystem.IsAndroid())
                {
                    //Best-effort dump exclusion, deliberately unchecked: locking — the core
                    //guarantee — already succeeded, and MADV_DONTDUMP does not exist everywhere.
                    _ = NativeMethods.Madvise(pointer, (nuint)lockedLength, NativeMethods.MADV_DONTDUMP);
                }
            }
        }
        catch(DllNotFoundException ex)
        {
            //An OS passed IsSupported but its C library could not be bound even through
            //LibcResolver's probing — surface the DOCUMENTED exception type, not a loader error.
            NativeMemory.AlignedFree(pointer);

            throw new PlatformNotSupportedException(
                "The platform C library could not be loaded for memory locking; this C library flavor is not resolvable. See the inner exception for the loader detail.", ex);
        }
        catch(EntryPointNotFoundException ex)
        {
            NativeMemory.AlignedFree(pointer);

            throw new PlatformNotSupportedException(
                "The platform C library is missing a required locking entry point. See the inner exception for the loader detail.", ex);
        }

        //Fresh native memory is uninitialized; hand out zeroed bytes like every other tier does.
        NativeMemory.Clear(pointer, (nuint)lockedLength);

        return new ProtectedMemoryOwner((nint)pointer, size, lockedLength);
    }
}
