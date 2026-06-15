namespace Lumoin.Base;

/// <summary>
/// How a <see cref="BaseMemoryPool"/> buffer is backed. The caller that knows what it is allocating
/// chooses the kind; the default leaves the hot path unchanged.
/// </summary>
public enum AllocationKind
{
    /// <summary>
    /// Ordinary managed heap. The default. Relocation is harmless for short-lived scratch, and this
    /// keeps the high-volume path (serialization, canonicalization, codecs) performing as before.
    /// </summary>
    Managed = 0,

    /// <summary>
    /// Pinned managed heap (the Pinned Object Heap, via <c>GC.AllocateArray(pinned: true)</c>). Never
    /// GC-relocated, so a zeroize-on-return actually wipes the bytes that held the secret. Pure managed
    /// and available on every platform. For long-lived secrets where the relocation erase gap matters
    /// but native locking is unavailable or unwanted.
    /// </summary>
    Pinned = 1,

    /// <summary>
    /// Native, locked, non-swappable memory supplied by an injected <see cref="NativeBackingAllocator"/>.
    /// Degrades to <see cref="Pinned"/> when no backing is wired (browser, mobile, or unconfigured).
    /// </summary>
    Native = 2
}
