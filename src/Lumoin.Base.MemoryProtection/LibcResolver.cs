using System.Reflection;
using System.Runtime.InteropServices;

namespace Lumoin.Base.MemoryProtection;

/// <summary>
/// Resolves the <c>libc</c> module name across C libraries. The bare name resolves through the
/// runtime's default probing on glibc distributions and Apple platforms, but musl-based Linux
/// (Alpine and derivatives — first-class .NET container bases) ships no <c>libc.so</c> alias at
/// all: musl's C library and dynamic linker are one file named <c>ld-musl-&lt;arch&gt;.so.1</c>.
/// Without this resolver, the first P/Invoke on musl would escape as
/// <see cref="DllNotFoundException"/> instead of anything this package documents.
/// </summary>
internal static class LibcResolver
{
    /// <summary>
    /// Registers the resolver for this assembly. Called from <see cref="NativeMethods"/>'s static
    /// constructor, which the runtime guarantees to run before any of that class's P/Invokes can
    /// bind — deterministic without a library module initializer (CA2255).
    /// </summary>
    internal static void Register()
    {
        NativeLibrary.SetDllImportResolver(typeof(LibcResolver).Assembly, Resolve);
    }


    /// <summary>
    /// Probes the platform's C library under its real names — the glibc SONAME first (stable
    /// since 1997; the common server case), then the runtime's own default probing (Apple
    /// platforms), then FreeBSD's SONAME (stable since 2008), then the musl loader names (stable
    /// since 2014) — and finally falls back to the process's main-program handle, whose global
    /// symbol scope resolves the already-loaded C library's exports regardless of SONAME, so a
    /// future SONAME change degrades to still-working rather than to a load failure. Returning
    /// zero falls through to default handling for any module other than <c>libc</c>.
    /// </summary>
    /// <param name="libraryName">The module name being bound.</param>
    /// <param name="assembly">The requesting assembly.</param>
    /// <param name="searchPath">The search-path hint from the import.</param>
    /// <returns>A native library handle, or zero to defer to the default resolution.</returns>
    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if(libraryName != "libc" || OperatingSystem.IsWindows())
        {
            return 0;
        }

        if(NativeLibrary.TryLoad("libc.so.6", assembly, searchPath, out nint handle)
            || NativeLibrary.TryLoad("libc", assembly, searchPath, out handle)
            || NativeLibrary.TryLoad("libc.so.7", assembly, searchPath, out handle))
        {
            return handle;
        }

        //musl names carry the architecture; cover the container-relevant ones.
        string? muslArchitecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x86_64",
            Architecture.Arm64 => "aarch64",
            Architecture.X86 => "i386",
            Architecture.Arm => "armhf",
            _ => null
        };

        if(muslArchitecture is not null
            && (NativeLibrary.TryLoad($"libc.musl-{muslArchitecture}.so.1", assembly, searchPath, out handle)
                || NativeLibrary.TryLoad($"ld-musl-{muslArchitecture}.so.1", assembly, searchPath, out handle)))
        {
            return handle;
        }

        //Last resort, and the reason the name list above cannot silently rot: the C library is
        //already loaded in every .NET process (the runtime links against it), so its exports
        //resolve through the process's global symbol scope regardless of SONAME. If every
        //explicit name goes stale (a hypothetical glibc 3, a FreeBSD SONAME bump), binding still
        //succeeds here; the explicit names stay preferred only because a direct library handle
        //bypasses symbol interposition. A genuinely absent symbol then surfaces as
        //EntryPointNotFoundException, which Allocate translates into the documented
        //PlatformNotSupportedException.
        return NativeLibrary.GetMainProgramHandle();
    }
}
