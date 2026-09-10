// CsCheck seed reproduction for the property tests in this assembly is documented in full in
// test/Lumoin.Base.Tests/Properties/AssemblyProperties.cs and applies here too: set CsCheck_Seed on
// the command line to replay a printed failure; never pin a seed anywhere persistent.

// Method-level parallelism: the activity-observing tests filter by their own trace id, and the guarded
// allocations stay small so concurrent mlock use sits far below the platform budget.
[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]
[assembly: DiscoverInternals]
