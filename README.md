<img style="display: block; margin-inline-start: auto; margin-inline-end: auto;" src="resources/lumoin-base-github-logo.svg" width="600" alt="Lumoin.Base project logo: a circular emblem of concentric dashed arcs in blue hues evoking layered bedrock, followed by the wordmark 'base'.">

# Lumoin.Base

**Dependency-free primitives shared across the Lumoin family of .NET libraries.**

![Main build workflow](https://github.com/Lumoin/Lumoin.Base/actions/workflows/main.yml/badge.svg)

---

Lumoin.Base holds the small set of primitives that every Lumoin library needs and none of them should
own. It is dependency-free and AOT-, trim-, and browser-clean, so any family member — including
browser/WASM builds — can consume it, and no member depends on another.

The repository ships three packages. **`Lumoin.Base`** is the dependency-free leaf: `BaseMemoryPool`,
an exact-size, zero-on-return `MemoryPool<byte>` whose caller chooses how each buffer is backed
(`Managed`, `Pinned`, or `Native`), and `Tag`, an immutable type-keyed metadata container. The
native tier is an injection seam (`NativeBackingAllocator`), never a compiled-in dependency.
**`Lumoin.Base.Sodium`** is the non-browser libsodium implementation of that seam: wiring
`SodiumBacking.Allocate` into a pool serves `AllocationKind.Native` rents as per-rent isolated
`sodium_malloc` guarded allocations — canary, guard pages, best-effort memory locking, zero on
free. The binding is RID-agnostic and carries no native binaries; the libsodium library itself is
resolved at runtime from family-built native asset packages.
**`Lumoin.Base.MemoryProtection`** is the same seam without libsodium: page-aligned allocations
locked into physical memory through the operating system's own mechanism (`VirtualLock` on
Windows, `mlock` plus best-effort `MADV_DONTDUMP` on Linux/Android, `mlock` on Apple platforms
and FreeBSD), zeroed on free — pure P/Invoke into libraries every supported OS already ships
(glibc and musl alike), so it carries no native assets at all.
