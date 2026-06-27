namespace Lumoin.Base;

/// <summary>
/// Telemetry tag keys emitted by the domain-agnostic sensitive-memory primitives.
/// </summary>
public static class SensitiveMemoryTelemetry
{
    /// <summary>
    /// <c>sensitive_memory.lifetime_ms</c> — the duration in milliseconds from when a
    /// <see cref="SensitiveMemory"/> value was constructed to when it was disposed. Set by
    /// <see cref="SensitiveMemory"/> on the supplied lifetime span, if any. The span itself is created and
    /// otherwise annotated by the caller — for cryptographic material the crypto backend stamps its own
    /// provider / library / class / operation provenance for CBOM traceability — so this primitive
    /// contributes only the neutral lifetime duration.
    /// </summary>
    public static readonly string LifetimeMs = "sensitive_memory.lifetime_ms";
}
