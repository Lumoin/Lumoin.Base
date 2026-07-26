# CLAUDE.md — Lumoin.Base

## What this is

Bedrock NuGet packages for the Lumoin family (Verifiable, Veritas, Verisync, Veridical, Concordia,
Sopia, …). `Lumoin.Base` is the dependency-free leaf: `BaseMemoryPool` (an exact-size,
zero-on-return `MemoryPool<byte>` with `AllocationKind` tiers `Managed` / `Pinned` / `Native` and
OpenTelemetry metrics/tracing) and `Tag` (immutable, type-keyed metadata). `Lumoin.Base.Libsodium`
is THE libsodium package: the raw crypto binding (Ed25519, X25519, Ed25519↔X25519 conversion,
XChaCha20-Poly1305 AEAD, ML-KEM-768 and X-Wing KEMs; browser-capable, caller-composed
`MemoryPool<byte>` scratch) plus the guarded-memory `NativeBackingAllocator` implementation
(`SodiumBacking.Allocate` → per-rent `sodium_malloc` guarded allocations, marked
`[UnsupportedOSPlatform("browser")]`), with the libsodium 1.0.22 native binaries shipped INSIDE
the package (`runtimes/<rid>/native`, built by main.yml's `natives` job from the pinned,
checksum-verified upstream tarball; statically linked at publish on browser-wasm instead). The
former `Lumoin.Base.Sodium` package is retired at 0.0.7 — its backing moved in here.
`Lumoin.Base.MemoryProtection` is the same seam
via the OS twins (`MemoryProtectionBacking.Allocate` → page-aligned `VirtualLock`/`mlock`+
`MADV_DONTDUMP` locked allocations; pure P/Invoke into kernel32/libc, zero native assets, strict
`InsufficientMemoryException` on budget exhaustion — never silent unlocked fallback). Single TFM
`net10.0`, C# `preview`, SDK pinned in `global.json`; tests are MSTest + CsCheck on
Microsoft.Testing.Platform; the MemoryProtection suite runs for real on every CI leg.

## Hard constraints (do not drift)

- **`Lumoin.Base` stays dependency-free and AOT / trim / browser-clean.** No P/Invoke and no native
  assets in the leaf assembly. Any platform memory backing (native allocation + mlock/VirtualLock)
  lives in a SEPARATE non-browser assembly and is INJECTED via the `NativeBackingAllocator` seam
  (see the comment in `Directory.Build.props`). Note: `Directory.Build.props` adds
  `SupportedPlatform browser` to every non-`.Tests` project. A project whose WHOLE surface is
  non-browser removes that item (as `src/Lumoin.Base.MemoryProtection` does); a browser-capable
  project with a non-browser sub-surface keeps it and marks those APIs
  `[UnsupportedOSPlatform("browser")]` (as `SodiumBacking` in `Lumoin.Base.Libsodium` does).
  P/Invoke declarations themselves do not trip CA1416.
- **Load-bearing family contracts:** exact-size `Rent(n)` (returns exactly `n` bytes),
  zero-on-return, double-return protection, `Tag`'s typed `Create/With/Get` and content equality,
  and the pool staying **byte-specialized** (non-generic; go generic only via an owner-coordinated
  decision). Sibling repos (at least Veritas and Concordia) consume this repo's SOURCE via
  sibling-path `ProjectReference`, so a broken `main` ripples into their builds immediately.
- **Strict native degradation** (commit `b1fcc83`): a `Native` rent with no wired backing THROWS by
  default; the graceful `Pinned` fallback is an explicit opt-in (`allowNativeDegradation: true`)
  and telemetry records the effective allocation kind plus a degradation event. Keep docs,
  tests, and telemetry consistent with this.
- Central package management + `RestorePackagesWithLockFile`: any dependency change must regenerate
  `packages.lock.json` (CI restores with `--locked-mode`). New packable projects must be added to
  `LIBRARY_PROJECTS` in `.github/workflows/main.yml`, to `$projects` in
  `generate-local-test-nuget-packages.ps1`, and (if they fetch anything new) to the harden-runner
  egress allowlist in the workflow.

## Standing family agent rules (coordination hub)

The coordination hub is `C:\Users\veikk\OneDrive - Lumoin\agents-coordination-tempdocs\`. Standing
rules for any agent on the Lumoin codebases are in `verifiable\bootstrap\AGENT_RULES.md` there and
bind work in this repo too. Highlights:

- Never `git push` unless explicitly asked in the current conversation. No AI co-authorship in
  commits or PRs. Work in a branch, not a worktree (harness-managed worktrees excepted).
- Build-performance doctrine: AGENT_RULES **§G** (family doctrine, owner 2026-07-07) — every
  `dotnet build` carries `-bl:tempdocs/buildperf/<name>.binlog` (gitignored, machine-local;
  binlogs embed environment variables, so never commit them or mirror them to the hub). Diagnose
  slow or failing builds by binlog replay, never by raising console verbosity.
- `tempdocs/` in this repo is gitignored and machine-local; durable, code-free design notes are
  mirrored to the hub (this repo's notes: `lumoin-base\tempdocs\` there; Base's architecture is
  also described in the hub's `verifiable\bootstrap\ARCHITECTURE_AND_LEARNINGS.md` §2).

## Policy: On Windows, use native tools — do not shell out to Git Bash

Copied verbatim from the hub-root standing instruction `use-windows-native-tools-not-git-bash.md`
(2026-07-08), as that document requires:

1. **Search with the built-in tools**: `Glob` for filenames, `Grep` for content, `Read` for files.
   These run natively (ripgrep-based), respect scope, and die with the session. Do **not** shell
   out to Git Bash `find`, `grep`, `head`, `cat`, `ls` pipelines.
2. **Never scan the whole filesystem.** `find / -iname <file>` (or any search rooted above the
   repo) is banned. If a file isn't in the repo, ask or use a scoped, indexed search — don't sweep
   the disk.
3. **Prefer the PowerShell tool over the Bash tool** for shell work on Windows. Git Bash (MSYS)
   children detach from the harness when a session dies and become orphans; PowerShell children
   have not shown this failure mode here.
4. **Long-running shellouts need a timeout** and should be run in the background via the harness
   (which tracks and reaps them), never fire-and-forget.
5. **Git worktrees always go under `..\ClaudeWorkTrees` relative to the repo** — i.e.
   `<repo-parent>\ClaudeWorkTrees\<repo>\<branch>` (for repos in `C:\projektit`:
   `C:\projektit\ClaudeWorkTrees\Lumoin.Base\<branch>` etc.). Never create a worktree as a sibling
   of the repo or inside another repo: siblings in `C:\projektit` look like independent repos to
   agents and double the search/build surface. Exception: harness-managed worktrees
   (`EnterWorktree` / agent `isolation: 'worktree'`) live in `<repo>\.claude\worktrees\` and
   auto-clean — leave those alone.

`C:\projektit` should contain no worktree checkouts — if one appears there, it violates this
policy. See the source document on the hub for the incident history (2026-07-07 memory
exhaustion, 2026-07-08 recurrence) and diagnosis/cleanup commands.

## Arc: platform native memory allocation (design frozen; Sodium binding landed, rest deferred)

The owner-ratified design for the native memory tier lives in the sibling checkout
`C:\projektit\Verifiable\tempdocs\fido2\2026-07-07-session10-park.md`, section "Decisions made
this session that outlive it", and is distilled for this repo in
`tempdocs\2026-07-09-native-memory-allocation-plan.md` (machine-local; hub mirror under
`lumoin-base\tempdocs\`). In short: Base gains mechanism-agnostic machinery (slab-allocator
delegate variant, per-segment software canaries, `CryptographicOperations.ZeroMemory` clearing,
finalizer backstop) with platform mechanisms injected, and defaults ship as separate family
packages `Lumoin.Base.MemoryProtection` (OS-twin mlock/MADV_DONTDUMP/VirtualLock, pure P/Invoke,
zero native assets) and `Lumoin.Base.Sodium` (lean open-fork libsodium, guarded allocations).
The `Lumoin.Base.Sodium` managed binding is in-tree (per-rent guarded tier only — the
protected-slab tier is NOT this package's job); its native assets come from a family-built
libsodium fork pinned to an upstream tag, built in own CI — NEVER from third-party repackagings
(no libsodium NuGet, no NSec, no LibSodium.Net as dependencies). Owner sequencing (reconfirmed
2026-07-09): the consuming sodium arc in Verifiable is queued LAST after its CTAP and TPM arcs —
keep `NativeBackingAllocator` and the strict-degradation semantics stable; Base-side machinery
may land ahead of the consumer.
