using System.Buffers;

namespace Lumoin.Base;

/// <summary>
/// The injection seam for the native, locked memory tier. A non-browser assembly supplies this to
/// <see cref="BaseMemoryPool"/>; it allocates <paramref name="size"/> bytes of native memory (for
/// example <c>NativeMemory.AlignedAlloc</c> plus <c>mlock</c>/<c>VirtualLock</c>) and returns it as an
/// <see cref="IMemoryOwner{T}"/> whose disposal zeroes, unlocks, and frees. It lives outside
/// <c>Lumoin.Base</c> because that P/Invoke cannot compile into a browser-targeted leaf.
/// </summary>
/// <param name="size">The exact number of bytes to allocate.</param>
/// <returns>An owner of exactly <paramref name="size"/> native bytes.</returns>
public delegate IMemoryOwner<byte> NativeBackingAllocator(int size);
