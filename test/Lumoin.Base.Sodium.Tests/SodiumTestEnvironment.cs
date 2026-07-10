// Family provenance rule (no middleman binaries): the libsodium native library consumed by
// SodiumBacking is never a third-party repackaging (libsodium NuGet, NSec, LibSodium.Net). Until
// family-built native asset packages exist, LUMOIN_SODIUM_LIBRARY is the interim dev-loop knob — it
// points at a libsodium built locally from pinned upstream source, and nothing produced by that local
// build is ever published.

using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lumoin.Base.Sodium.Tests;

/// <summary>
/// Assembly-wide libsodium resolution hook (so a locally built library can be pointed at via
/// <c>LUMOIN_SODIUM_LIBRARY</c> without shipping a native asset) and the shared inconclusive-skip
/// helper other test classes call when a test needs the real native library.
/// </summary>
[TestClass]
public static class SodiumTestEnvironment
{
    [AssemblyInitialize]
    public static void Initialize(TestContext context)
    {
        NativeLibrary.SetDllImportResolver(typeof(SodiumBacking).Assembly, ResolveLibsodium);
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
        if(!SodiumBacking.IsAvailable)
        {
            Assert.Inconclusive("libsodium native library is not available on this host; set LUMOIN_SODIUM_LIBRARY to a locally built libsodium to run this test.");
        }
    }
}
