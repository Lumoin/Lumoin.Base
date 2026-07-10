namespace Lumoin.Base;

/// <summary>
/// How a <see cref="BaseMemoryPool"/> serves <see cref="AllocationKind.Native"/> rentals from its
/// injected <see cref="NativeBackingAllocator"/>. This is a pool-level choice: the ratified family
/// design runs TWO pool instances side by side — a per-rent isolated one for few long-lived keys
/// and a protected-slab one for many transient secrets — rather than one compromise allocator,
/// because hardware memory protection is page-granular, so per-secret hardware guards and
/// locked-memory density are mutually exclusive.
/// </summary>
public enum NativeRentMode
{
    /// <summary>
    /// Every native rental is its own backing allocation (for example one <c>sodium_malloc</c>
    /// guarded region per rent) and is never pooled. Maximum isolation: per-secret hardware guard
    /// pages and canary, immediate hard-fail on overflow — at a cost of several pages of address
    /// space and locked-memory budget per rental. The default; right for few long-lived keys.
    /// </summary>
    PerRentIsolated = 0,

    /// <summary>
    /// Native rentals of a given size share one backing region per slab: the pool subdivides a
    /// single injected allocation into exact-size segments bracketed by per-segment software
    /// canaries, zeroed with <c>CryptographicOperations.ZeroMemory</c> on every return. Density in
    /// place of per-secret hardware guards: hundreds of secrets fit a locked-memory budget that
    /// holds only a handful of isolated allocations (for example Android's 64 KB
    /// <c>RLIMIT_MEMLOCK</c>). Cross-segment overflows inside the region are detected by the
    /// canary check on return — delayed-loud, surfaced as <see cref="CanaryViolationException"/> —
    /// rather than faulting at the moment of the write; writes escaping the whole region still
    /// fault immediately when the backing itself is guarded (for example a <c>sodium_malloc</c>
    /// region). Right for many transient secrets.
    /// </summary>
    ProtectedSlab = 1
}
