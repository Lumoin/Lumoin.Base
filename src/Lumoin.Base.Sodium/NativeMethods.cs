using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Lumoin.Base.Sodium;

/// <summary>
/// The raw libsodium entry points behind <see cref="SodiumBacking"/>. The module name
/// <c>libsodium</c> resolves per platform to <c>libsodium.dll</c>, <c>libsodium.so</c> or
/// <c>libsodium.dylib</c> through the standard .NET native library probing (application directory,
/// <c>runtimes/&lt;rid&gt;/native</c> package assets, OS loader paths); a host or test harness can
/// override resolution with
/// <see cref="NativeLibrary.SetDllImportResolver(System.Reflection.Assembly, DllImportResolver)"/>
/// on this assembly.
/// </summary>
/// <remarks>
/// All libsodium exports use the cdecl calling convention (<c>SODIUM_EXPORT</c>), declared
/// explicitly so the win-x86 flavor does not silently marshal through the platform default.
/// Every import restricts the Windows loader to <see cref="DllImportSearchPath.SafeDirectories"/>
/// so a libsodium.dll planted in the current working directory can never be picked up.
/// </remarks>
internal static unsafe partial class NativeMethods
{
    /// <summary>
    /// The libsodium module name handed to the .NET native library loader.
    /// </summary>
    private const string LibraryName = "libsodium";


    /// <summary>
    /// Initializes libsodium. Thread-safe and idempotent; every other entry point requires a
    /// successful initialization first.
    /// </summary>
    /// <returns>
    /// <c>0</c> on first successful initialization, <c>1</c> when already initialized,
    /// <c>-1</c> on failure.
    /// </returns>
    [LibraryImport(LibraryName, EntryPoint = "sodium_init")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int Init();


    /// <summary>
    /// Allocates <paramref name="size"/> bytes of guarded memory: the page-rounded region is
    /// bracketed by no-access guard pages (an overflow past the end faults immediately, as does an
    /// underflow deep enough to reach the leading guard page), prefixed with a canary that
    /// <see cref="Free"/> verifies (small underflows are caught there, at free time, not at write
    /// time), and locked into physical memory on a best-effort basis
    /// (<c>mlock</c>/<c>VirtualLock</c>).
    /// </summary>
    /// <param name="size">The number of bytes to allocate.</param>
    /// <returns>A pointer to the user region, or <see langword="null"/> on failure.</returns>
    [LibraryImport(LibraryName, EntryPoint = "sodium_malloc")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial void* Malloc(nuint size);


    /// <summary>
    /// Zeroes and releases a <see cref="Malloc"/> allocation. LOUD CONTRACT: libsodium verifies
    /// the canary here and ABORTS THE PROCESS if the allocation's bookkeeping was tampered with —
    /// a deliberate hard-fail, not an exception this binding can translate.
    /// </summary>
    /// <param name="pointer">The pointer returned by <see cref="Malloc"/>; <see langword="null"/> is a no-op.</param>
    [LibraryImport(LibraryName, EntryPoint = "sodium_free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial void Free(void* pointer);


    /// <summary>
    /// Zeroes <paramref name="length"/> bytes at <paramref name="pointer"/> in a way the compiler
    /// cannot optimize away. <see cref="Free"/> zeroes too; calling this first is the family's
    /// defense-in-depth discipline for sensitive material.
    /// </summary>
    /// <param name="pointer">The start of the region to zero.</param>
    /// <param name="length">The number of bytes to zero.</param>
    [LibraryImport(LibraryName, EntryPoint = "sodium_memzero")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial void MemZero(void* pointer, nuint length);
}
