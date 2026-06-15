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
