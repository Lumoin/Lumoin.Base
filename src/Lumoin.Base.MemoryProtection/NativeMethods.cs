using System.Runtime.InteropServices;

namespace Lumoin.Base.MemoryProtection;

/// <summary>
/// The raw operating-system entry points behind <see cref="MemoryProtectionBacking"/>. Everything
/// here targets a library the platform always ships — <c>kernel32</c> on Windows, <c>libc</c> on
/// POSIX systems — which is what lets this package carry zero native assets. The unused platform's
/// imports are inert: P/Invokes bind on first call, and every call site is guarded by an
/// <see cref="OperatingSystem"/> check.
/// </summary>
internal static unsafe partial class NativeMethods
{
    /// <summary>
    /// Registers <see cref="LibcResolver"/> before any P/Invoke in this class can bind: an
    /// explicit static constructor runs before the first access to any static member, which
    /// includes every import below.
    /// </summary>
    static NativeMethods()
    {
        LibcResolver.Register();
    }


    /// <summary>
    /// The Linux/Android <c>madvise</c> advice value excluding a range from core dumps. Not
    /// portable: the value and the advice itself are Linux-specific, so it is only ever passed on
    /// those platforms.
    /// </summary>
    internal const int MADV_DONTDUMP = 16;


    /// <summary>
    /// Locks the page range into physical memory on Windows: the pages cannot be written to the
    /// pagefile while locked. Capacity is governed by the process minimum working set
    /// (<c>ERROR_WORKING_SET_QUOTA</c> when exceeded; raise it with
    /// <c>SetProcessWorkingSetSize</c>).
    /// </summary>
    /// <param name="address">The start of the region; page-aligned in this package.</param>
    /// <param name="size">The length of the region in bytes.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("kernel32.dll", EntryPoint = "VirtualLock", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool VirtualLock(void* address, nuint size);


    /// <summary>
    /// Unlocks a range previously locked with <see cref="VirtualLock"/> on Windows.
    /// </summary>
    /// <param name="address">The start of the region.</param>
    /// <param name="size">The length of the region in bytes.</param>
    /// <returns><see langword="true"/> on success.</returns>
    [LibraryImport("kernel32.dll", EntryPoint = "VirtualUnlock", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool VirtualUnlock(void* address, nuint size);


    /// <summary>
    /// Locks the page range into physical memory on POSIX systems: the pages cannot be swapped
    /// out while locked. Capacity is governed by <c>RLIMIT_MEMLOCK</c> (<c>ENOMEM</c> when
    /// exceeded — famously 64 KB on Android).
    /// </summary>
    /// <param name="address">The start of the region; page-aligned in this package.</param>
    /// <param name="length">The length of the region in bytes.</param>
    /// <returns><c>0</c> on success, <c>-1</c> on failure with the error in <c>errno</c>.</returns>
    [LibraryImport("libc", EntryPoint = "mlock", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int Mlock(void* address, nuint length);


    /// <summary>
    /// Unlocks a range previously locked with <see cref="Mlock"/> on POSIX systems. POSIX locking
    /// is not reference counted, which is why this package never shares a page between
    /// allocations: unlocking one would silently unlock the other.
    /// </summary>
    /// <param name="address">The start of the region.</param>
    /// <param name="length">The length of the region in bytes.</param>
    /// <returns><c>0</c> on success, <c>-1</c> on failure with the error in <c>errno</c>.</returns>
    [LibraryImport("libc", EntryPoint = "munlock", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int Munlock(void* address, nuint length);


    /// <summary>
    /// Advises the kernel about a range; used with <see cref="MADV_DONTDUMP"/> on Linux and
    /// Android to keep the secrets out of core dumps. Best-effort defense-in-depth: a failure
    /// (old kernel, unusual filesystem) is ignored because locking, the core guarantee, has
    /// already succeeded by the time this is called.
    /// </summary>
    /// <param name="address">The start of the region; page-aligned in this package.</param>
    /// <param name="length">The length of the region in bytes.</param>
    /// <param name="advice">The advice value, e.g. <see cref="MADV_DONTDUMP"/>.</param>
    /// <returns><c>0</c> on success, <c>-1</c> on failure with the error in <c>errno</c>.</returns>
    [LibraryImport("libc", EntryPoint = "madvise", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int Madvise(void* address, nuint length, int advice);
}
