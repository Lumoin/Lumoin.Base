// Reproducing property-test failures
// -----------------------------------
// The property tests use CsCheck's Gen...Sample(...) and run unseeded, so each run explores fresh
// random cases. When a Sample fails it has already shrunk to a minimal counter-example and prints the
// seed that produced it, for example:
//
//     CsCheck.CsCheckException: Set seed: "0ycPmO1H_kG7" or -e CsCheck_Seed=0ycPmO1H_kG7 to reproduce (3 shrinks, 43 skipped, 100 total).
//
// To rerun exactly that case, set the CsCheck_Seed environment variable to the printed value. CsCheck
// reads it for the whole process and applies it to every Sample run, so combine it with a test filter.
// CsCheck_Iter overrides the iteration count (default 100); CsCheck_Time runs for a number of seconds
// instead. These environment variables are the global override mechanism documented by CsCheck 4.7.0.
//
// PowerShell:
//     $env:CsCheck_Seed = "0ycPmO1H_kG7"
//     dotnet test test/Lumoin.Base.Tests/Lumoin.Base.Tests.csproj -c Release --filter "FullyQualifiedName~BaseMemoryPoolCsCheckTests"
//     Remove-Item Env:\CsCheck_Seed
//
// bash:
//     CsCheck_Seed=0ycPmO1H_kG7 CsCheck_Iter=1000 \
//       dotnet test test/Lumoin.Base.Tests/Lumoin.Base.Tests.csproj -c Release --filter "FullyQualifiedName~BaseMemoryPoolCsCheckTests"
//
// Do not pin a seed anywhere persistent (config.runsettings, csproj, CI env): a fixed seed turns the
// property tests into a single example and destroys their exploration. Pin only on the command line for
// the lifetime of one debugging session, then clear it.

[assembly: Parallelize]
[assembly: DiscoverInternals]
