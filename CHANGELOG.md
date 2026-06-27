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
  the `NativeBackingAllocator` seam and degrades to `Pinned` when unwired, keeping the assembly
  dependency-free and AOT / trim / browser-clean.
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
  by design (it relies on GC liveness, so no `Native`/`Pinned` backing).

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
