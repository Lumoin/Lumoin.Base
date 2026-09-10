using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Lumoin.Base.Libsodium;

/// <summary>
/// The guarded-memory entry points (<c>sodium_malloc</c>, <c>sodium_free</c>, <c>sodium_memzero</c>)
/// behind <see cref="SodiumBacking"/>. Module resolution, the <c>__Internal</c> Apple builds, the
/// cdecl declaration and the safe-directory loader restriction are documented once on the crypto
/// part of this class in <c>NativeMethods.cs</c>; the same <c>LibraryName</c> and per-import
/// attributes apply here.
/// </summary>
internal static unsafe partial class NativeMethods
{
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
