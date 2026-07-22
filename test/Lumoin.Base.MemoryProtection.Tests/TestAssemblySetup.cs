using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lumoin.Base.MemoryProtection.Tests;

/// <summary>
/// Assembly-level setup: gives the test process a deterministic locked-page budget. Windows caps
/// <c>VirtualLock</c> by the process minimum working set, so the run raises the floor once up front
/// instead of depending on the host default; POSIX hosts govern locking with <c>RLIMIT_MEMLOCK</c>
/// and need no per-process call.
/// </summary>
[TestClass]
public static partial class TestAssemblySetup
{
    [AssemblyInitialize]
    public static void EnsureLockedMemoryBudget(TestContext context)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        //Only ever grows the limits; an already larger host configuration is kept.
        if (!GetProcessWorkingSetSize(CurrentProcessPseudoHandle, out nuint currentMinimum, out nuint currentMaximum))
        {
            throw new InvalidOperationException($"GetProcessWorkingSetSize failed with Win32 error {Marshal.GetLastPInvokeError()}.");
        }

        nuint minimum = nuint.Max(currentMinimum, (nuint)(32 * 1024 * 1024));
        nuint maximum = nuint.Max(currentMaximum, (nuint)(128 * 1024 * 1024));
        if (!SetProcessWorkingSetSize(CurrentProcessPseudoHandle, minimum, maximum))
        {
            throw new InvalidOperationException($"SetProcessWorkingSetSize failed with Win32 error {Marshal.GetLastPInvokeError()}.");
        }
    }

    //The working set APIs accept the documented -1 pseudo-handle meaning the current process.
    private static nint CurrentProcessPseudoHandle => -1;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessWorkingSetSize(nint process, out nuint minimumWorkingSetSize, out nuint maximumWorkingSetSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessWorkingSetSize(nint process, nuint minimumWorkingSetSize, nuint maximumWorkingSetSize);
}
