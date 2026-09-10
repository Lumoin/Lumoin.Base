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
  Every member reads as disposed once the pool is: `Count` throws `ObjectDisposedException` like `Intern`,
  `TryGet` and `Reset`, and the observable instruments publish no measurement rather than a zero.
- `Utf8StringPool.Reset()`: bulk-reclaims all interned memory and clears the table so the pool can be reused
  without being recreated, with the same "no live views" contract as disposal — a cheap shrink for scoped reuse.
- `Utf8StringInterner`: a process-wide, thread-safe interner for recurring UTF-8 bytes and .NET strings — the
  concurrent, self-bounding counterpart to `Utf8StringPool`. Values live in hot/cold generations and the cold one
  is evicted when the hot fills, bounding the live set to about twice a configured capacity; eviction is safe
  because interned memory is managed and an outstanding `Utf8String` keeps its own bytes alive (the GC reclaims
  only once no view remains), so it never dangles a value already handed out. Cache hits are lock-free; `Clear()`
  drops everything; an optional static, application-installed `Instance` mirrors the pool's ambient, and a
  self-creating `Shared` is a process-wide interner on the constructor defaults for callers that install none.
  `Intern(string)` encodes with the replacement fallback, so ill-formed UTF-16 collapses onto U+FFFD;
  `TryIntern(string, out Utf8String)` is the strict path that returns `false` for an unpaired surrogate instead of
  replacing it. Managed-only by design (it relies on GC liveness, so no `Native`/`Pinned` backing). Optional
  OpenTelemetry metrics via `Utf8StringInternerMetrics` (intern, hit, and rotation counters plus a live-count
  gauge), and an optional maximum value length that returns oversized values uncached so no single value can bloat
  the resident set.
- Guarded-memory backing `SodiumBacking` (in `Lumoin.Base.Libsodium`): the libsodium
  implementation of the `NativeBackingAllocator` seam. Wiring `SodiumBacking.Allocate` into a
  `BaseMemoryPool` serves `AllocationKind.Native` rents as per-rent isolated `sodium_malloc`
  guarded allocations — canary, guard pages, best-effort memory locking, and zero on free — with
  `sodium_memzero` defense-in-depth and a finalizer backstop on the owner.
  `SodiumBacking.IsAvailable` reports whether the native library loaded so hosts wire the backing
  only where it exists. Not available on browser-wasm and marked `[UnsupportedOSPlatform]`
  accordingly. Briefly shipped as the separate package `Lumoin.Base.Sodium` (last release 0.0.7,
  now retired).
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
- New package `Lumoin.Base.Libsodium`, the family's one libsodium package: a raw crypto binding -
  Ed25519 seed-keypair generation, detached signing and verification, Ed25519-to-X25519 key
  conversion, X25519 scalar multiplication, XChaCha20-Poly1305 authenticated encryption, and the
  ML-KEM-768 (FIPS 203) and X-Wing hybrid post-quantum KEMs, with secret-key scratch memory
  composed by the caller as a `MemoryPool<byte>` (guarded native, locked, pinned, or managed
  backing) - plus the `SodiumBacking` guarded-memory seam implementation described above. Depends
  only on `Lumoin.Base`. The libsodium 1.0.22 native library ships inside the package
  (win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64, android-arm64, android-x64;
  iOS and Mac Catalyst link a bundled static xcframework through the package's buildTransitive
  targets), built by the family from the pinned, checksum-verified upstream release source, so
  the package works as-is from NuGet; on browser-wasm the same binding links the packed static
  archive (`runtimes/browser-wasm/native/libsodium.a`, built at the Emscripten the pinned SDK's
  wasm-tools workload uses) into `dotnet.wasm` at publish through the same buildTransitive
  targets, which warn (`LUMOIN0001`) when the consuming app's workload pins a different
  Emscripten than the archive was built with. Binaries the build runner can load are proven by running the package's own test suite
  against them and the browser archive by executing the wasm smoke under Node; cross-compiled
  mobile binaries ship on pinned-source provenance with architecture and exported-symbol
  verification. The package multi-targets `net11.0` plus `net11.0-ios`/`net11.0-maccatalyst`;
  the Apple assemblies import via `__Internal`, resolving against the statically linked
  xcframework the buildTransitive targets wire into the app build.

### Changed

- **Every package targets `net11.0`** (.NET 11 RC1, go-live licence); consumers build with the
  .NET 11 SDK. This library leads the family's move to .NET 11.
- Toolchain refresh: .NET SDK 11.0.100-rc.1 (the wasm smoke builds on the same pin), MSTest
  4.4.0, Microsoft.Testing.Extensions 2.4.0, code coverage 18.11.0, CsCheck 4.8.0, the
  diagnostics local tools 10.0.731102, `dotnet-stryker` for mutation testing, and harden-runner
  v2.21.1.
- Package `Lumoin.Base.Sodium` is retired: its guarded-memory backing (`SodiumBacking`,
  namespace now `Lumoin.Base.Libsodium`) ships in `Lumoin.Base.Libsodium`. 0.0.7 remains the
  last release of the retired id.
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
- `BaseMemoryPool` validates arguments with the BCL throw helpers
  (`ArgumentOutOfRangeException.ThrowIfNegativeOrZero` and friends): exception types and parameter
  names are unchanged, the messages are the BCL's and carry the offending value.
- `MemoryProtectionBacking.Allocate`'s `InsufficientMemoryException` message names the platform
  error in words beside the numeric code (`Marshal.GetPInvokeErrorMessage`), so an `ENOMEM` budget
  exhaustion and an `EPERM` missing capability are told apart without a lookup.
- `Utf8StringInterner` serializes rotations with `System.Threading.Lock`, resolves each
  generation's span-keyed lookup once instead of on every probe, and publishes the generation pair
  through a volatile property.
- Documentation corrections: `Utf8StringInterner`'s untrusted-input guidance states that the
  default `Utf8StringComparer.Ordinal` hash is seeded per process and that a deterministic
  `Utf8HashFunction` is the predictable one; the observable instruments in `Utf8StringPoolMetrics`
  and `Utf8StringInternerMetrics` are documented as up-down counters; `Utf8StringPool.Intern(string)`
  documents its U+FFFD replacement; `Lumoin.Base.Libsodium`'s docs describe the natives shipping
  inside the package and the `beforefieldinit` initialization gate.

### Fixed

- An `ObjectDisposedException` thrown from a disposed slab or rented owner reported
  `System.String` as the disposed object's name; it now names the owner's type.
- `SensitiveMemory` wipes its bytes on disposal with `CryptographicOperations.ZeroMemory` instead
  of `Span.Clear`, so the wipe cannot be elided.
