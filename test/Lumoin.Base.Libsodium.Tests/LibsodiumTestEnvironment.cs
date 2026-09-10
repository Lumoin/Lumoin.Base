// Family provenance rule (no middleman binaries): the libsodium native library consumed by this
// binding is never a third-party repackaging (libsodium NuGet, NSec, LibSodium.Net); the binaries
// ship inside Lumoin.Base.Libsodium, built by main.yml's natives jobs from pinned upstream source.
// LUMOIN_SODIUM_LIBRARY points this suite at one specific libsodium binary: the host natives jobs
// (win/linux/osx) set it to the binary they just built so the suite proves it before packing
// (skipped: 0 is the gate; the cross-compiled Android and Apple binaries ship on provenance instead),
// and a developer sets it to a local build from the same pinned source.

using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lumoin.Base.Libsodium.Tests;

/// <summary>
/// Assembly-wide libsodium resolution hook (so one specific libsodium binary can be pointed at via
/// <c>LUMOIN_SODIUM_LIBRARY</c> ahead of the default probing) and the shared inconclusive-skip
/// helper other test classes call when a test needs the real native library.
/// </summary>
[TestClass]
public static class LibsodiumTestEnvironment
{
    //Probes availability at most once per process. LibsodiumCrypto's initialization gate is its
    //beforefieldinit type initializer, so a failed sodium_init is sticky for the whole process either
    //way; caching here just avoids re-throwing through the type initializer on every RequireSodium call.
    private static Lazy<bool> IsAvailable { get; } = new(ProbeAvailability);


    [AssemblyInitialize]
    public static void Initialize(TestContext context)
    {
        NativeLibrary.SetDllImportResolver(typeof(LibsodiumCrypto).Assembly, ResolveLibsodium);
    }


    //Only intercepts the "libsodium" module, and only when LUMOIN_SODIUM_LIBRARY names a file that
    //exists; otherwise returning 0 falls through to the default probing (application directory,
    //runtimes/<rid>/native package assets, OS loader paths), so an installed system library still works.
    private static nint ResolveLibsodium(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if(libraryName == "libsodium")
        {
            string? overridePath = Environment.GetEnvironmentVariable("LUMOIN_SODIUM_LIBRARY");
            if(!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath) && NativeLibrary.TryLoad(overridePath, out nint handle))
            {
                return handle;
            }
        }

        return 0;
    }


    /// <summary>
    /// Marks the calling test inconclusive with a standard, actionable message when libsodium is not
    /// available on this host.
    /// </summary>
    public static void RequireSodium()
    {
        if(!IsAvailable.Value)
        {
            Assert.Inconclusive("libsodium native library is not available on this host; set LUMOIN_SODIUM_LIBRARY to a locally built libsodium to run this test.");
        }
    }


    //GetVersionString is a plain P/Invoke that touches no library state, so an absent native library
    //surfaces directly as DllNotFoundException — or as TypeInitializationException wrapping it when the
    //runtime chose to run LibsodiumCrypto's beforefieldinit type initializer (sodium_init) first.
    //EntryPointNotFoundException covers a resolver returning a library that is not libsodium at all.
    private static bool ProbeAvailability()
    {
        try
        {
            _ = LibsodiumCrypto.GetVersionString();
            return true;
        }
        catch(TypeInitializationException)
        {
            return false;
        }
        catch(DllNotFoundException)
        {
            return false;
        }
        catch(EntryPointNotFoundException)
        {
            return false;
        }
    }
}
