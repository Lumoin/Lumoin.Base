// Family provenance rule (no middleman binaries): the libsodium native library consumed by this
// binding is never a third-party repackaging (libsodium NuGet, NSec, LibSodium.Net). Until
// family-built native asset packages exist, LUMOIN_SODIUM_LIBRARY is the interim dev-loop knob — it
// points at a libsodium built locally from pinned upstream source, and nothing produced by that local
// build is ever published. The knob is shared with Lumoin.Base.Sodium.Tests so one locally built
// library serves both suites.

using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lumoin.Base.Libsodium.Tests;

/// <summary>
/// Assembly-wide libsodium resolution hook (so a locally built library can be pointed at via
/// <c>LUMOIN_SODIUM_LIBRARY</c> without shipping a native asset) and the shared inconclusive-skip
/// helper other test classes call when a test needs the real native library.
/// </summary>
[TestClass]
public static class LibsodiumTestEnvironment
{
    //Probes availability at most once per process. LibsodiumCrypto's initialization gate is a static
    //constructor, so a failed probe is sticky for the whole process either way; caching it here just
    //avoids re-throwing through the type initializer on every RequireSodium call.
    private static readonly Lazy<bool> Available = new(ProbeAvailability);


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
        if(!Available.Value)
        {
            Assert.Inconclusive("libsodium native library is not available on this host; set LUMOIN_SODIUM_LIBRARY to a locally built libsodium to run this test.");
        }
    }


    //The first touch of any LibsodiumCrypto static member runs its sodium_init static constructor;
    //when the native library is absent that surfaces as TypeInitializationException (wrapping
    //DllNotFoundException). The direct exceptions cover a resolver returning a library that is not
    //libsodium at all.
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
