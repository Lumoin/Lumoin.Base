// CsCheck seed reproduction for the property tests in this assembly is documented in full in
// test/Lumoin.Base.Tests/Properties/AssemblyProperties.cs and applies here too: set CsCheck_Seed on
// the command line to replay a printed failure; never pin a seed anywhere persistent.

// Method-level parallelism: every allocation here locks whole pages against the platform budget, so
// the sizes in these tests stay small enough that the worst-case concurrent locked footprint sits far
// below common defaults (see AllocateReturnsExactSizeZeroedAndUsable).
[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]
[assembly: DiscoverInternals]
