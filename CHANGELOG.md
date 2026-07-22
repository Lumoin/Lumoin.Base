# Change Log

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/)
and this project adheres to [Semantic Versioning](http://semver.org/).

<!-- Available types of changes:
### Added
### Changed
### Fixed
### Deprecated
### Removed
### Security
-->

## [Unreleased]

### Added

- Initial package: `Lumoin.Base`. Bedrock primitives shared across the Lumoin family.
- `BaseMemoryPool`: a `MemoryPool<byte>` that lets the caller choose how a buffer is backed
  through an `AllocationKind` passed at rent time (`Managed`, `Pinned`, `Native`), with exact-size
  slabs, deterministic zero-on-return, and OpenTelemetry metrics. Native backing is injected through
  the `NativeBackingAllocator` seam, keeping the assembly dependency-free and AOT / trim /
  browser-clean. A `Native` rent with no backing wired throws by default; the graceful fallback to
  `Pinned` is an explicit opt-in (`allowNativeDegradation`) and telemetry records the effective
  allocation kind plus a degradation event.
- `Tag`: a metadata container for purpose-specific classification of pooled memory.
- `SlabBufferWriter`: an `IBufferWriter<byte>` over a `MemoryPool<byte>` (e.g. `BaseMemoryPool`) that grows by
  renting linked slabs and `Detach()`-es into a single exact-length owned buffer — the streaming-serialization
  sink for the family, so writers never pre-size and never copy-to-grow.
- `SensitiveMemory` / `SensitiveData`: a domain-agnostic base for a pooled, `Tag`-carrying block of sensitive
  bytes that exposes only read-only views and clears its memory on disposal — so a naked `byte[]` never crosses a
  public boundary and potentially sensitive (e.g. reporting) bytes are wiped deterministically. Construction
  optionally accepts an OpenTelemetry `Activity` bounding the value's lifetime.
- `EmptyMemoryOwner`: a shared, allocation-free `IMemoryOwner<byte>` singleton over a zero-length buffer with a
  no-op dispose — the stand-in for empty payloads where a pool would reject a zero-size rental, and recognized by
  `SensitiveMemory` so shared `Empty` singletons are never wiped or poisoned.
- `SensitiveMemoryTelemetry`: the telemetry tag-key (`sensitive_memory.lifetime_ms`) the sensitive-memory
  primitives emit on the lifetime span.
- `Utf8String`: a non-owning, byte-native UTF-8 string value type (`readonly struct` over `ReadOnlyMemory<byte>`)
  for encoding and I/O boundaries — byte-defined equality/ordering with an optionally precomputed hash, zero-copy
  `Slice`/`Range` views, byte-search helpers, `IUtf8SpanFormattable`/`ISpanFormattable`, and an explicit
  `TryFromUtf8` validating factory (construction itself does not validate).
- `Utf8StringComparer`: equality, ordering, and zero-allocation alternate lookup by raw `ReadOnlySpan<byte>` for
  `Utf8String` (`IAlternateEqualityComparer`), so a `HashSet`/`Dictionary`/`FrozenSet` of `Utf8String` is probed
  with a `u8` literal or wire span without allocating. `Create(Utf8HashFunction)` swaps in a deterministic hash for
  bucketing that is stable across processes.
- `Utf8StringPool`: a single-writer arena that interns UTF-8 bytes into bulk-freed slabs rented from a
  `MemoryPool<byte>` (default a private `BaseMemoryPool`), with zero-allocation probing on a cache hit, a chosen
  `AllocationKind` for how interned memory is protected, UTF-8 validation on by default (opt out with
  `validateOnIntern: false`), OpenTelemetry metrics via `Utf8StringPoolMetrics`, and an optional static,
  application-installed `Instance` ambient pool (the library never creates one implicitly). Materialization is
  pool-only — there is no heap-allocating factory; `Utf8String` either views caller memory or is interned.
- `Utf8StringPool.Reset()`: bulk-reclaims all interned memory and clears the table so the pool can be reused
  without being recreated, with the same "no live views" contract as disposal — a cheap shrink for scoped reuse.
- `Utf8StringInterner`: a process-wide, thread-safe interner for recurring UTF-8 bytes and .NET strings — the
  concurrent, self-bounding counterpart to `Utf8StringPool`. Values live in hot/cold generations and the cold one
  is evicted when the hot fills, bounding the live set to about twice a configured capacity; eviction is safe
  because interned memory is managed and an outstanding `Utf8String` keeps its own bytes alive (the GC reclaims
  only once no view remains), so it never dangles a value already handed out. Cache hits are lock-free; `Clear()`
  drops everything; an optional static, application-installed `Instance` mirrors the pool's ambient. Managed-only
  by design (it relies on GC liveness, so no `Native`/`Pinned` backing). Optional OpenTelemetry metrics via
  `Utf8StringInternerMetrics` (intern, hit, and rotation counters plus a live-count gauge), and an optional maximum
  value length that returns oversized values uncached so no single value can bloat the resident set.
- New package `Lumoin.Base.Sodium`: the libsodium implementation of the `NativeBackingAllocator`
  seam. Wiring `SodiumBacking.Allocate` into a `BaseMemoryPool` serves `AllocationKind.Native`
  rents as per-rent isolated `sodium_malloc` guarded allocations — canary, guard pages, best-effort
  memory locking, and zero on free — with `sodium_memzero` defense-in-depth and a finalizer
  backstop on the owner. The managed binding is RID-agnostic and carries no native assets; the
  libsodium native library is resolved at runtime (family-built native asset packages to follow),
  and `SodiumBacking.IsAvailable` reports whether it loaded so hosts wire the backing only where
  it exists.
- Protected-slab native tier: `BaseMemoryPool` gains a pool-level `NativeRentMode`
  (`PerRentIsolated`, the default, or `ProtectedSlab`). In protected-slab mode injected backing
  regions are allocated on demand per buffer size, each subdivided into exact-size segments
  bracketed by per-segment software canaries (verified on return; a stomped canary zeroes the
  secret, retires the segment,
  records the `Lumoin.BaseMemoryPool.CanaryViolationsTotal` counter and a `CanaryViolation`
  activity event, and surfaces as the new `CanaryViolationException`). Density in place of
  per-secret hardware guards: hundreds of transient secrets fit a locked-memory budget that holds
  only a handful of isolated allocations. Native rent activities now carry a `nativeRentMode` tag,
  and `TrimExcess` reclaims idle protected regions.
- New package `Lumoin.Base.MemoryProtection`: the operating-system-twin implementation of the
  `NativeBackingAllocator` seam — no libsodium, no native assets, no dependencies beyond
  `Lumoin.Base`. `MemoryProtectionBacking.Allocate` serves `AllocationKind.Native` rents as
  page-aligned allocations locked into physical memory (`VirtualLock` on Windows, `mlock` plus
  best-effort `MADV_DONTDUMP` on Linux/Android, `mlock` on Apple platforms and FreeBSD; the C
  library resolves on glibc and musl alike), zeroed over the whole
  locked range on disposal, with the same idempotent-dispose and finalizer-backstop owner contract
  as the Sodium tier. Strict and loud: an exhausted locked-memory budget (`RLIMIT_MEMLOCK`, the
  Windows minimum working set) throws `InsufficientMemoryException` with the knob named — never a
  silent fallback to unlocked memory. The OS provides no guard pages or canary here; pair with
  `NativeRentMode.ProtectedSlab` for software-canary overrun detection on top of the locked
  region. `MemoryProtectionBacking.IsSupported` reports platform support.

### Changed

- `SensitiveMemory`: documented the wipe's threat model — exposure it reduces versus what it cannot defend, and
  that the wipe is only fully reliable on non-relocatable (`Pinned`/`Native`) backing.
- `SensitiveMemory`: `Dispose` now stops and stamps an optional OpenTelemetry lifetime `Activity`, and skips
  wiping/disposing (and the disposed-state transition) when backed by the shared `EmptyMemoryOwner`.
- `SlabBufferWriter`: its empty-detach owner is now the shared `EmptyMemoryOwner` rather than a private copy.
- `Tag`: redesigned as a bespoke type with content-based, order-independent equality (the previous record
  derived equality from its backing dictionary, comparing by reference). The `(Type, object)` tuple factory
  overloads, the `Data` property, and the `Type` indexer are removed in favor of the typed `Create<T>` /
  `With<T>` / `Get<T>` / `TryGet<T>` / `Contains<T>` API and a read-only `Entries` projection. **Breaking.**
- Segment clearing throughout the pool now uses `CryptographicOperations.ZeroMemory` instead of
  `Span.Clear`/`Array.Clear`, so the zeroing of returned secrets cannot be elided.
- `BaseMemoryPool` constructors gained the optional `nativeRentMode` parameter (source-compatible;
  recompile against this version).
- Dependency refresh across the repository: .NET SDK 10.0.302; MSTest 4.3.2;
  Microsoft.Testing.Platform extensions 2.3.2; LiquidTestReports.Markdown 2.0.0-beta.6; and the
  build-time `System.Security.Cryptography.Xml` pin at 10.0.10, addressing published advisories
  against 10.0.9. The pinned `actions/checkout` and `actions/setup-dotnet` CI actions moved to
  their latest releases.
