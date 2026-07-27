using System.Buffers;
using System.Runtime.Versioning;

namespace Lumoin.Base.Libsodium;

/// <summary>
/// The libsodium implementation of the <see cref="NativeBackingAllocator"/> seam: wire
/// <see cref="Allocate"/> (or the cached <see cref="Allocator"/> delegate) into a
/// <see cref="BaseMemoryPool"/> and every <c>AllocationKind.Native</c> rent becomes a per-rent
/// isolated <c>sodium_malloc</c> guarded allocation — canary, guard pages, best-effort memory
/// locking, and zero on free, all provided by libsodium itself.
/// </summary>
/// <remarks>
/// <para>
/// <code>
/// using var pool = new BaseMemoryPool(nativeBacking: SodiumBacking.Allocate);
/// using var secret = pool.Rent(32, AllocationKind.Native);
/// </code>
/// </para>
/// <para>
/// This assembly carries no native binaries: the libsodium library is resolved at runtime through
/// the standard .NET native library probing, with family-built native asset packages to supply it
/// (never third-party repackagings). <see cref="IsAvailable"/> reports whether the library loaded
/// and initialized, so a host can wire the backing only where it exists and rely on
/// <see cref="BaseMemoryPool"/>'s strict-by-default degradation everywhere else.
/// </para>
/// <para>
/// LOUD CONTRACT inherited from libsodium: the allocation is bracketed by no-access guard pages,
/// so a write past the end of a rented buffer — or an underflow deep enough to reach the leading
/// guard page — crashes immediately; a SMALL underflow corrupts the canary instead and is caught
/// lazily, at disposal, where <c>sodium_free</c>'s canary check ABORTS THE PROCESS (not at the
/// moment of corruption — a leaked, never-disposed owner never trips it). These are deliberate
/// hard failures — the entire point of guarded allocations — not conditions this binding
/// translates into exceptions. Each allocation also costs several pages of address space and
/// locked-memory budget (guard pages, bookkeeping, page-rounding), which is why the pool serves
/// the Native tier per rent for few long-lived secrets rather than slab-pooling it.
/// </para>
/// </remarks>
//Guarded native memory does not exist in the browser sandbox; the crypto surface of this package does.
[UnsupportedOSPlatform("browser")]
public static class SodiumBacking
{
    /// <summary>
    /// The one-time initialization result: <see langword="null"/> when libsodium loaded and
    /// initialized, otherwise the human-readable reason it did not. Lazy so the first user pays
    /// for the probe and everyone after reads the cached outcome.
    /// </summary>
    private static Lazy<string?> InitializationFailure { get; } = new(Probe);


    /// <summary>
    /// Indicates whether the libsodium native library is present and initialized in this process.
    /// When <see langword="false"/>, <see cref="Allocate"/> throws; a host typically wires the
    /// backing only when this is <see langword="true"/> and otherwise constructs its pool without
    /// a native backing, keeping <see cref="BaseMemoryPool"/>'s strict-by-default semantics.
    /// </summary>
    public static bool IsAvailable => InitializationFailure.Value is null;


    /// <summary>
    /// A cached <see cref="NativeBackingAllocator"/> delegate over <see cref="Allocate"/>, so
    /// callers wiring several pools do not allocate a fresh delegate per method-group conversion.
    /// </summary>
    public static NativeBackingAllocator Allocator { get; } = Allocate;


    /// <summary>
    /// Allocates exactly <paramref name="size"/> bytes of libsodium guarded memory and returns the
    /// owner whose disposal zeroes, unlocks, and frees it. This method's signature matches
    /// <see cref="NativeBackingAllocator"/>, so it wires directly:
    /// <c>new BaseMemoryPool(nativeBacking: SodiumBacking.Allocate)</c>.
    /// </summary>
    /// <param name="size">The exact number of bytes to allocate.</param>
    /// <returns>An owner of exactly <paramref name="size"/> bytes of guarded native memory.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="size"/> is less than or equal to zero, matching
    /// <see cref="BaseMemoryPool"/>'s rent contract (<c>sodium_malloc(0)</c> is representable but
    /// a zero-byte guarded secret is meaningless, so it is rejected rather than allowed).
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the libsodium native library is not available in this process; the message
    /// carries the load or initialization failure. See <see cref="IsAvailable"/>.
    /// </exception>
    /// <exception cref="InsufficientMemoryException">
    /// Thrown when <c>sodium_malloc</c> fails, typically from address-space exhaustion or the
    /// platform's locked-memory limit (e.g. <c>RLIMIT_MEMLOCK</c>). This is the recoverable
    /// allocation-failure type (the runtime reserves <see cref="OutOfMemoryException"/> itself,
    /// which it derives from, so a broad OOM catch still sees it).
    /// </exception>
    public static unsafe IMemoryOwner<byte> Allocate(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);

        string? initializationFailure = InitializationFailure.Value;
        if(initializationFailure is not null)
        {
            throw new InvalidOperationException(initializationFailure);
        }

        void* pointer = NativeMethods.Malloc((nuint)size);
        if(pointer is null)
        {
            throw new InsufficientMemoryException(
                $"sodium_malloc failed to allocate {size} bytes of guarded memory; likely address-space exhaustion or the platform locked-memory limit (each guarded allocation costs several pages).");
        }

        return new SodiumMemoryOwner((nint)pointer, size);
    }


    /// <summary>
    /// Loads and initializes libsodium once for the process.
    /// </summary>
    /// <returns><see langword="null"/> on success, otherwise the failure reason.</returns>
    private static string? Probe()
    {
        try
        {
            //Thread-safe and idempotent in libsodium: 0 = initialized now, 1 = already
            //initialized (e.g. by another binding in the same process), -1 = failure.
            return NativeMethods.Init() >= 0
                ? null
                : "libsodium loaded but sodium_init() returned -1: the library failed to initialize in this process.";
        }
        catch(DllNotFoundException ex)
        {
            return $"The libsodium native library could not be found. Deploy a family-built libsodium for this platform or leave the native backing unwired. Loader message: {ex.Message}";
        }
        catch(BadImageFormatException ex)
        {
            return $"A libsodium native library was found but does not match this process (wrong architecture or corrupt file). Loader message: {ex.Message}";
        }
        catch(EntryPointNotFoundException ex)
        {
            return $"The loaded libsodium is missing a required export; it is too old or not libsodium. Loader message: {ex.Message}";
        }
    }
}
