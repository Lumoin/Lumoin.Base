<img style="display: block; margin-inline-start: auto; margin-inline-end: auto;" src="resources/lumoin-base-github-logo.svg" width="600" alt="Lumoin.Base project logo: a circular emblem of concentric dashed arcs in blue hues evoking layered bedrock, followed by the wordmark 'base'.">

# Lumoin.Base

**Dependency-free primitives shared across the Lumoin family of .NET libraries.**

![Main build workflow](https://github.com/Lumoin/Lumoin.Base/actions/workflows/main.yml/badge.svg)

---

Lumoin.Base holds the small set of primitives that every Lumoin library needs and none of them should
own. It is dependency-free and AOT-, trim-, and browser-clean, so any family member — including
browser/WASM builds — can consume it, and no member depends on another.
