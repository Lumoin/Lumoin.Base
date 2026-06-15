using System.Buffers;
using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Globalization;

namespace Lumoin.Base;

/// <summary>
/// Determines the number of segments to allocate per slab based on segment size.
/// </summary>
/// <param name="segmentSize">The size of each segment in elements.</param>
/// <returns>The number of segments to allocate in the new slab.</returns>
public delegate int SlabCapacityStrategy(int segmentSize);


/// <summary>
/// A thread-safe, byte-specialized memory pool designed for sensitive (cryptographic) operations that
/// returns memory of exactly the requested size, and whose caller chooses how each rented buffer is
/// backed via <see cref="Rent(int, AllocationKind)"/>. The pool automatically creates
/// (size, kind)-specific internal sub-pools (slabs) to optimize allocation patterns for the different
/// buffer sizes and lifetimes commonly used in cryptographic operations.
/// </summary>
/// <remarks>
/// <para>
/// This memory pool is specifically designed for sensitive cryptographic material where:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>Exact buffer sizes are required (no over-allocation).</description>
/// </item>
/// <item>
/// <description>Memory is automatically cleared on return and on disposal for security.</description>
/// </item>
/// <item>
/// <description>Size- and kind-specific pooling optimizes for common crypto buffer sizes and lifetimes.</description>
/// </item>
/// <item>
/// <description>Comprehensive metrics and tracing support operational monitoring.</description>
/// </item>
/// <item>
/// <description>Thread-safe operations support concurrent cryptographic operations.</description>
/// </item>
/// </list>
/// <para>
/// The pool maintains separate collections of slabs for each requested (buffer size, allocation kind)
/// pair, ensuring that buffers of different sizes or backings never interfere with each other and
/// allowing for size-specific optimization strategies.
/// </para>
/// <para>
/// Slab capacity is determined by a <see cref="SlabCapacityStrategy"/> delegate, allowing callers to
/// tune amortization. The default strategy allocates more segments for smaller buffers (common
/// key/hash sizes) and fewer for larger buffers.
/// </para>
/// <para>
/// The base <see cref="Rent(int)"/> returns <see cref="AllocationKind.Managed"/> memory, identical to a
/// plain managed pool, so the high-volume hot path is unaffected. Only a caller that knows it is holding
/// a long-lived secret asks for <see cref="AllocationKind.Pinned"/> or <see cref="AllocationKind.Native"/>.
/// The managed and pinned tiers are pure managed and live here; the native tier is supplied by an
/// injected <see cref="NativeBackingAllocator"/> from a non-browser assembly. When none is wired, an
/// <see cref="AllocationKind.Native"/> request degrades to <see cref="AllocationKind.Pinned"/>. Native
/// rentals are allocated per rent (they are not slab-pooled).
/// </para>
/// </remarks>
[DebuggerDisplay("BaseMemoryPool: Slabs={totalSlabs}, Active={activeRentals}, Allocated={totalMemoryAllocated} bytes")]
public sealed class BaseMemoryPool: MemoryPool<byte>
{
    /// <summary>
    /// Dictionary mapping (buffer size, allocation kind) to their corresponding slab collections.
    /// Each pair gets its own list of slabs to prevent cross-contamination and enable size-specific
    /// allocation strategies.
    /// </summary>
    private Dictionary<(int Size, AllocationKind Kind), List<Slab>> Slabs { get; } = new();

    /// <summary>
    /// Indicates whether this memory pool instance has been disposed.
    /// </summary>
    private bool IsDisposed { get; set; }

    /// <summary>
    /// Lock object for synchronizing access to the slabs dictionary and metrics.
    /// </summary>
    private Lock LockObject { get; } = new();

    /// <summary>
    /// The native (locked) backing, supplied by a non-browser assembly. When <see langword="null"/>,
    /// native requests degrade to pinned.
    /// </summary>
    private NativeBackingAllocator? NativeBacking { get; }

    /// <summary>
    /// Activity source for distributed tracing of memory operations.
    /// </summary>
    private static ActivitySource ActivitySource { get; } = new("BaseMemoryPool");

    /// <summary>
    /// Meter instance for collecting and reporting memory pool metrics.
    /// </summary>
    private Meter PoolMeter { get; }

    /// <summary>
    /// Histogram tracking the distribution of requested buffer sizes.
    /// </summary>
    private Histogram<int> BufferSizeHistogram { get; }

    /// <summary>
    /// Counter tracking successful rent operations.
    /// </summary>
    private Counter<long> RentSuccessCounter { get; }

    /// <summary>
    /// Counter tracking memory return operations.
    /// </summary>
    private Counter<long> ReturnCounter { get; }

    /// <summary>
    /// Strategy for determining slab capacity based on segment size.
    /// </summary>
    private SlabCapacityStrategy CapacityStrategy { get; }

    /// <summary>
    /// Controls whether distributed tracing activities are created for memory operations.
    /// Disable for high-frequency cryptographic workloads where tracing overhead is unacceptable.
    /// </summary>
    public bool TracingEnabled { get; }

    /// <summary>
    /// Thread-safe counter for the total number of slabs created.
    /// </summary>
    private int totalSlabs;

    /// <summary>
    /// Thread-safe counter for the total memory allocated in bytes.
    /// </summary>
    private long totalMemoryAllocated;

    /// <summary>
    /// Thread-safe counter for the number of currently active rentals.
    /// </summary>
    private int activeRentals;

    /// <summary>
    /// Thread-safe counter for the total number of segments across all slabs.
    /// </summary>
    private int totalSegments;

    /// <summary>
    /// Default initial capacity for new slabs when no allocation strategy is specified.
    /// </summary>
    public const int DefaultInitialSlabCapacity = 4;


    /// <summary>
    /// Default strategy that allocates more segments for smaller buffers
    /// and fewer for larger ones, tuned for common cryptographic material sizes.
    /// </summary>
    /// <param name="segmentSize">The size of each segment in elements.</param>
    /// <returns>The number of segments to allocate in the new slab.</returns>
    /// <example>
    /// <code>
    /// //Use the default strategy explicitly.
    /// var pool = new BaseMemoryPool(
    ///     capacityStrategy: BaseMemoryPool.DefaultCapacityStrategy);
    /// </code>
    /// </example>
    public static int DefaultCapacityStrategy(int segmentSize) => segmentSize switch
    {
        <= 64 => 32,
        <= 256 => 16,
        <= 4096 => 8,
        _ => 4
    };


    /// <summary>
    /// Lazy singleton backing the <see cref="Shared"/> property.
    /// </summary>
    private static readonly Lazy<BaseMemoryPool> SharedInstance =
        new(() => new BaseMemoryPool());

    /// <summary>
    /// Gets a shared singleton instance of the memory pool.
    /// </summary>
    /// <value>A singleton instance of memory pool for cryptographic material.</value>
    /// <remarks>
    /// Unlike the base <see cref="MemoryPool{T}.Shared"/>, this returns a lazily-initialized
    /// singleton so that callers who expect shared-state semantics get correct behavior.
    /// The shared instance uses the default capacity strategy, has tracing enabled, and has no native
    /// backing wired (so <see cref="AllocationKind.Native"/> degrades to <see cref="AllocationKind.Pinned"/>).
    /// </remarks>
    public static new BaseMemoryPool Shared => SharedInstance.Value;


    /// <summary>
    /// Initializes a new instance with default settings.
    /// </summary>
    /// <param name="nativeBacking">
    /// Optional native (locked) backing, supplied by a non-browser assembly. When <see langword="null"/>,
    /// <see cref="AllocationKind.Native"/> requests degrade to <see cref="AllocationKind.Pinned"/>.
    /// </param>
    public BaseMemoryPool(NativeBackingAllocator? nativeBacking = null)
        : this(new Meter(BaseMemoryPoolMetrics.MeterName, "1.0.0"), nativeBacking: nativeBacking)
    {
    }


    /// <summary>
    /// Initializes a new instance with the specified meter.
    /// </summary>
    /// <param name="meter">The meter instance for collecting operational metrics.</param>
    /// <param name="capacityStrategy">
    /// Optional strategy for determining slab capacity. When <see langword="null"/>,
    /// <see cref="DefaultCapacityStrategy"/> is used.
    /// </param>
    /// <param name="tracingEnabled">
    /// When <see langword="true"/>, distributed tracing activities are created for
    /// rent and return operations. Disable for high-frequency workloads.
    /// </param>
    /// <param name="nativeBacking">
    /// Optional native (locked) backing, supplied by a non-browser assembly. When <see langword="null"/>,
    /// <see cref="AllocationKind.Native"/> requests degrade to <see cref="AllocationKind.Pinned"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="meter"/> is null.</exception>
    public BaseMemoryPool(
        Meter meter,
        SlabCapacityStrategy? capacityStrategy = null,
        bool tracingEnabled = true,
        NativeBackingAllocator? nativeBacking = null)
    {
        ArgumentNullException.ThrowIfNull(meter);

        PoolMeter = meter;
        CapacityStrategy = capacityStrategy ?? DefaultCapacityStrategy;
        TracingEnabled = tracingEnabled;
        NativeBacking = nativeBacking;
        IsDisposed = false;

        //Initialize observable counters for automatic metric collection.
        meter.CreateObservableUpDownCounter(
            BaseMemoryPoolMetrics.BaseMemoryPoolTotalSlabs,
            () => totalSlabs,
            "slabs",
            "Total number of memory slabs created across all buffer sizes.");

        meter.CreateObservableUpDownCounter(
            BaseMemoryPoolMetrics.BaseMemoryPoolTotalMemoryAllocated,
            () => totalMemoryAllocated,
            "bytes",
            "Total memory allocated across all slabs including available segments.");

        meter.CreateObservableUpDownCounter(
            BaseMemoryPoolMetrics.BaseMemoryPoolActiveRentals,
            () => activeRentals,
            "segments",
            "Number of currently rented memory segments.");

        meter.CreateObservableUpDownCounter(
            BaseMemoryPoolMetrics.BaseMemoryPoolAllocationEfficiency,
            CalculateAllocationEfficiency,
            "percent",
            "Percentage of allocated memory currently in use.");

        BufferSizeHistogram = meter.CreateHistogram<int>(
            BaseMemoryPoolMetrics.BaseMemoryPoolBufferSizeDistribution,
            "bytes",
            "Distribution of requested buffer sizes.");

        RentSuccessCounter = meter.CreateCounter<long>(
            BaseMemoryPoolMetrics.BaseMemoryPoolRentOperationsTotal,
            "operations",
            "Total number of successful rent operations.");

        ReturnCounter = meter.CreateCounter<long>(
            BaseMemoryPoolMetrics.BaseMemoryPoolReturnOperationsTotal,
            "operations",
            "Total number of memory return operations.");
    }


    /// <summary>
    /// Gets the maximum buffer size that this pool can allocate.
    /// </summary>
    public override int MaxBufferSize => int.MaxValue;


    /// <summary>
    /// Rents a managed (<see cref="AllocationKind.Managed"/>) memory buffer of exactly the specified size
    /// from the pool.
    /// </summary>
    /// <param name="bufferSize">The exact number of elements required in the buffer.</param>
    /// <returns>
    /// An <see cref="IMemoryOwner{T}"/> that provides access to the rented memory.
    /// The returned memory will be exactly <paramref name="bufferSize"/> elements.
    /// </returns>
    /// <exception cref="ObjectDisposedException">Thrown when the pool has been disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="bufferSize"/> is less than or equal to zero.</exception>
    [SuppressMessage("Naming", "CA1725:Parameter names should match base declaration", Justification = "This memory pool returns buffers of the specified size.")]
    public override IMemoryOwner<byte> Rent(int bufferSize)
    {
        return Rent(bufferSize, AllocationKind.Managed);
    }


    /// <summary>
    /// Rents a memory buffer of exactly the specified size from the pool, backed per
    /// <paramref name="kind"/>.
    /// </summary>
    /// <param name="bufferSize">The exact number of elements required in the buffer.</param>
    /// <param name="kind">How the buffer is backed.</param>
    /// <returns>
    /// An <see cref="IMemoryOwner{T}"/> that provides access to the rented memory.
    /// The returned memory will be exactly <paramref name="bufferSize"/> elements.
    /// </returns>
    /// <exception cref="ObjectDisposedException">Thrown when the pool has been disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="bufferSize"/> is less than or equal to zero.</exception>
    /// <remarks>
    /// <para>
    /// This method is thread-safe and will automatically create (size, kind)-specific slabs
    /// as needed. The returned memory is guaranteed to be exactly the requested size,
    /// unlike some memory pools that may return larger buffers for efficiency.
    /// </para>
    /// <para>
    /// <see cref="AllocationKind.Managed"/> is backed by an ordinary managed array,
    /// <see cref="AllocationKind.Pinned"/> by a pinned-object-heap array
    /// (<c>GC.AllocateArray(pinned: true)</c>), and <see cref="AllocationKind.Native"/> by the injected
    /// <see cref="NativeBackingAllocator"/> (allocated per rent, not slab-pooled), degrading to
    /// <see cref="AllocationKind.Pinned"/> when no backing is wired.
    /// </para>
    /// <para>
    /// A single tracing activity spans the full rental lifecycle from rent to return.
    /// The activity records buffer size tags and a return event upon disposal.
    /// Tracing can be disabled via <see cref="TracingEnabled"/> for hot paths.
    /// </para>
    /// </remarks>
    public IMemoryOwner<byte> Rent(int bufferSize, AllocationKind kind)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        if(bufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferSize),
                "Buffer size must be greater than zero.");
        }

        //Native is the only kind that lives outside this assembly. When a backing is wired it allocates
        //per rent; when not, it degrades to Pinned — the strongest protection this browser-clean leaf
        //offers without a P/Invoke dependency.
        AllocationKind effectiveKind = kind;
        if(effectiveKind == AllocationKind.Native && NativeBacking is null)
        {
            effectiveKind = AllocationKind.Pinned;
        }

        //Single activity spans the entire rental lifecycle from rent to return.
        //Ownership is transferred to the owner which disposes it on return.
        //StartActivity sets Activity.Current to the new activity. This is the intended
        //behavior: the lifecycle activity is the ambient context during the rental scope.
        //When the owner's Dispose calls LifecycleActivity.Dispose, Activity.Stop
        //automatically restores Activity.Current to its parent.
        Activity? activity = TracingEnabled
            ? ActivitySource.StartActivity("Rent", ActivityKind.Internal,
                Activity.Current?.Context ?? default)
            : null;

        activity?.AddTag("bufferSize", bufferSize.ToString(CultureInfo.InvariantCulture));
        activity?.AddTag("poolType", nameof(Byte));
        activity?.AddTag("allocationKind", kind.ToString());

        BufferSizeHistogram.Record(bufferSize);

        //Native rentals are served per rent by the injected backing and are not slab-pooled.
        if(effectiveKind == AllocationKind.Native)
        {
            IMemoryOwner<byte> nativeOwner = NativeBacking!(bufferSize);
            var nativeResult = new NativeMemoryOwner(nativeOwner, this, activity);

            RentSuccessCounter.Add(1, new KeyValuePair<string, object?>("bufferSize", bufferSize));

            return nativeResult;
        }

        IMemoryOwner<byte> result;

        using(LockObject.EnterScope())
        {
            var key = (bufferSize, effectiveKind);

            if(!Slabs.TryGetValue(key, out List<Slab>? slabList))
            {
                slabList = new List<Slab>();
                Slabs.Add(key, slabList);
            }

            Slab? availableSlab = null;
            int rentedIndex = -1;

            foreach(var slab in slabList)
            {
                if(slab.TryRent(out rentedIndex))
                {
                    availableSlab = slab;
                    break;
                }
            }

            if(availableSlab is null)
            {
                int capacity = CapacityStrategy(bufferSize);
                availableSlab = new Slab(bufferSize, capacity, effectiveKind == AllocationKind.Pinned);
                slabList.Add(availableSlab);

                Interlocked.Increment(ref totalSlabs);
                Interlocked.Add(ref totalMemoryAllocated, (long)bufferSize * capacity);
                Interlocked.Add(ref totalSegments, capacity);

                bool rentSuccess = availableSlab.TryRent(out rentedIndex);
                Debug.Assert(rentSuccess, "New slab should always have available capacity.");
            }

            Interlocked.Increment(ref activeRentals);

            result = new SlabMemoryOwner(availableSlab, rentedIndex, this, activity);
        }

        RentSuccessCounter.Add(1, new KeyValuePair<string, object?>("bufferSize", bufferSize));

        return result;
    }


    /// <summary>
    /// Releases all slabs that have no active rentals, reclaiming their memory.
    /// </summary>
    /// <returns>The number of slabs reclaimed.</returns>
    /// <remarks>
    /// <para>
    /// Call this method periodically in long-running services to return unused memory
    /// to the operating system. Slabs that still have rented segments are left untouched.
    /// Native rentals are not slab-pooled and so are unaffected by this method.
    /// </para>
    /// <para>
    /// This operation acquires the pool lock for the duration of the trim. Avoid
    /// calling it on hot paths.
    /// </para>
    /// </remarks>
    public int TrimExcess()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        int reclaimed = 0;

        using(LockObject.EnterScope())
        {
            foreach(var slabList in Slabs.Values)
            {
                for(int i = slabList.Count - 1; i >= 0; i--)
                {
                    var slab = slabList[i];
                    if(slab.IsFull)
                    {
                        int segmentCount = slab.SegmentCount;
                        int segmentSize = slab.SegmentSize;

                        slab.Dispose();
                        slabList.RemoveAt(i);

                        Interlocked.Decrement(ref totalSlabs);
                        Interlocked.Add(ref totalMemoryAllocated, -(long)segmentSize * segmentCount);
                        Interlocked.Add(ref totalSegments, -segmentCount);
                        reclaimed++;
                    }
                }
            }
        }

        return reclaimed;
    }


    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if(!IsDisposed)
        {
            if(disposing)
            {
                using(LockObject.EnterScope())
                {
                    foreach(var slabList in Slabs.Values)
                    {
                        foreach(var slab in slabList)
                        {
                            slab.Dispose();
                        }
                    }
                    Slabs.Clear();

                    totalSlabs = 0;
                    totalMemoryAllocated = 0;
                    activeRentals = 0;
                    totalSegments = 0;
                }

                PoolMeter?.Dispose();
            }

            IsDisposed = true;
        }
    }


    /// <summary>
    /// Returns a previously rented slab segment to its originating slab.
    /// </summary>
    /// <param name="slab">The slab that originally provided the segment.</param>
    /// <param name="index">The segment index to return to the pool.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="slab"/> is null.</exception>
    private void Return(Slab slab, int index)
    {
        ArgumentNullException.ThrowIfNull(slab);

        using(LockObject.EnterScope())
        {
            //The slab clears the segment for security before marking it available.
            slab.Return(index);
            Interlocked.Decrement(ref activeRentals);
            ReturnCounter.Add(1);
        }
    }


    /// <summary>
    /// Records the return-side metrics for a native rental. The native owner zeroes, unlocks and frees
    /// its own memory on disposal; the pool only accounts for it.
    /// </summary>
    private void ReturnNative()
    {
        using(LockObject.EnterScope())
        {
            ReturnCounter.Add(1);
        }
    }


    /// <summary>
    /// Calculates the current allocation efficiency as a percentage.
    /// </summary>
    private double CalculateAllocationEfficiency()
    {
        int currentTotalSegments = totalSegments;
        int currentActiveRentals = activeRentals;

        if(currentTotalSegments == 0)
        {
            return 0.0;
        }

        return (double)currentActiveRentals / currentTotalSegments * 100.0;
    }


    /// <summary>
    /// Represents a contiguous block of memory divided into fixed-size segments. Each slab manages
    /// segments of a specific size and tracks their availability using a <see cref="BitArray"/> to
    /// prevent double-return vulnerabilities. The backing array may be pinned-object-heap allocated.
    /// Segments are handed out and returned by index; the owner never reconstructs an offset.
    /// </summary>
    [DebuggerDisplay("Slab: SegmentSize={SegmentSize}, Available={AvailableSegments.Count}/{SegmentCount}")]
    private sealed class Slab: IDisposable
    {
        /// <summary>
        /// The size of each segment in this slab, measured in number of elements.
        /// </summary>
        public int SegmentSize { get; }

        /// <summary>
        /// The total number of segments that this slab can provide.
        /// </summary>
        public int SegmentCount { get; }

        /// <summary>
        /// The underlying memory buffer that contains all segments.
        /// </summary>
        private byte[] Buffer { get; }

        /// <summary>
        /// Stack tracking the indices of available segments for O(1) allocation.
        /// </summary>
        private Stack<int> AvailableSegments { get; }

        /// <summary>
        /// Tracks which segments are currently rented. A set bit at position N means
        /// segment N is rented. This prevents double-return corruption of the stack.
        /// </summary>
        private BitArray RentedSegments { get; }

        /// <summary>
        /// Indicates whether this slab has been disposed.
        /// </summary>
        private bool IsDisposed { get; set; }


        /// <summary>
        /// Initializes a new slab with the specified segment size and count.
        /// </summary>
        /// <param name="segmentSize">The size of each segment in elements.</param>
        /// <param name="segmentCount">The number of segments to create in this slab.</param>
        /// <param name="pinned">
        /// When <see langword="true"/>, the backing array is allocated on the pinned object heap so it is
        /// never GC-relocated and a zeroize-on-return actually wipes the bytes that held the secret.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="segmentSize"/> or <paramref name="segmentCount"/> is less than or equal to zero.
        /// </exception>
        public Slab(int segmentSize, int segmentCount, bool pinned)
        {
            if(segmentSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(segmentSize),
                    "Segment size must be greater than zero.");
            }
            if(segmentCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(segmentCount),
                    "Segment count must be greater than zero.");
            }

            SegmentSize = segmentSize;
            SegmentCount = segmentCount;
            Buffer = pinned
                ? GC.AllocateArray<byte>(segmentSize * segmentCount, pinned: true)
                : new byte[segmentSize * segmentCount];
            RentedSegments = new BitArray(segmentCount, false);

            AvailableSegments = new Stack<int>(segmentCount);
            for(int i = 0; i < segmentCount; i++)
            {
                AvailableSegments.Push(i);
            }

            IsDisposed = false;
        }

        /// <summary>
        /// Gets a value indicating whether all segments in this slab are available
        /// (none are currently rented).
        /// </summary>
        public bool IsFull => AvailableSegments.Count == SegmentCount;

        /// <summary>
        /// Gets a value indicating whether any segments are available for rent.
        /// </summary>
        public bool HasAvailableSegments => AvailableSegments.Count > 0;


        /// <summary>
        /// Attempts to rent a segment from this slab.
        /// </summary>
        /// <param name="index">
        /// When this method returns, contains the rented segment index if successful;
        /// otherwise, <c>-1</c>.
        /// </param>
        /// <returns>
        /// <see langword="true"/> if a segment was successfully rented;
        /// otherwise, <see langword="false"/>.
        /// </returns>
        public bool TryRent(out int index)
        {
            if(IsDisposed)
            {
                index = -1;
                return false;
            }

            if(AvailableSegments.TryPop(out int segmentIndex))
            {
                Debug.Assert(!RentedSegments[segmentIndex],
                    "Segment popped from available stack should not already be marked as rented.");

                RentedSegments[segmentIndex] = true;
                index = segmentIndex;
                return true;
            }

            index = -1;
            return false;
        }


        /// <summary>
        /// Gets the memory slice for the given segment index. The same shape works for managed and
        /// pinned backing.
        /// </summary>
        /// <param name="index">The segment index.</param>
        /// <returns>A <see cref="Memory{T}"/> over exactly this segment of the backing array.</returns>
        public Memory<byte> SliceFor(int index) => Buffer.AsMemory(index * SegmentSize, SegmentSize);


        /// <summary>
        /// Returns a previously rented segment to this slab, clearing it for security.
        /// </summary>
        /// <param name="index">The segment index to return.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="index"/> is outside the valid range for this slab.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the segment was not currently rented (double-return protection).
        /// </exception>
        /// <exception cref="ObjectDisposedException">Thrown when the slab has been disposed.</exception>
        public void Return(int index)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, nameof(Slab));

            if(index < 0 || index >= SegmentCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index),
                    "Segment index is outside the range of this slab.");
            }

            //Double-return protection: verify the segment is actually rented.
            if(!RentedSegments[index])
            {
                throw new InvalidOperationException(
                    "Segment was not rented or has already been returned.");
            }

            //Clear the memory segment for security before returning.
            Buffer.AsSpan(index * SegmentSize, SegmentSize).Clear();

            RentedSegments[index] = false;
            AvailableSegments.Push(index);
        }


        /// <summary>
        /// Releases all resources used by this slab and clears its memory.
        /// </summary>
        public void Dispose()
        {
            if(!IsDisposed)
            {
                Array.Clear(Buffer);
                AvailableSegments.Clear();
                IsDisposed = true;
            }
        }
    }


    /// <summary>
    /// Provides ownership of a slab-pooled memory segment rented from a <see cref="BaseMemoryPool"/>.
    /// Automatically returns the memory to the pool when disposed and ensures sensitive data is cleared.
    /// The owner carries (slab, index) and returns by index; it does not reconstruct an offset.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A single tracing activity spans the full rental lifecycle. On disposal, a return
    /// event is recorded on the activity and then the activity is stopped and disposed.
    /// This eliminates the need to manipulate <see cref="Activity.Current"/> and avoids
    /// async context pollution.
    /// </para>
    /// </remarks>
    [DebuggerDisplay("SlabMemoryOwner: Size={Slab.SegmentSize}, Disposed={Disposed}")]
    private sealed class SlabMemoryOwner: IMemoryOwner<byte>
    {
        /// <summary>
        /// Activity tracking the full rental lifecycle from rent to return.
        /// Null when tracing is disabled or no listener is attached.
        /// </summary>
        private Activity? LifecycleActivity { get; }

        private Slab Slab { get; }

        private int Index { get; }

        private BaseMemoryPool Pool { get; }

        private bool Disposed { get; set; }


        /// <summary>
        /// Initializes a new instance managing the segment at the given index of the given slab.
        /// </summary>
        /// <param name="slab">The slab that provided the segment.</param>
        /// <param name="index">The segment index within the slab.</param>
        /// <param name="pool">The memory pool that owns the slab.</param>
        /// <param name="lifecycleActivity">
        /// The activity tracking this rental. Ownership is transferred to this instance.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="slab"/> or <paramref name="pool"/> is null.
        /// </exception>
        public SlabMemoryOwner(
            Slab slab,
            int index,
            BaseMemoryPool pool,
            Activity? lifecycleActivity)
        {
            ArgumentNullException.ThrowIfNull(slab);
            ArgumentNullException.ThrowIfNull(pool);

            Slab = slab;
            Index = index;
            Pool = pool;
            LifecycleActivity = lifecycleActivity;
            Disposed = false;
        }


        /// <summary>
        /// Gets the memory managed by this owner.
        /// </summary>
        /// <exception cref="ObjectDisposedException">Thrown when this owner has been disposed.</exception>
        public Memory<byte> Memory
        {
            get
            {
                ObjectDisposedException.ThrowIf(Disposed, nameof(SlabMemoryOwner));
                return Slab.SliceFor(Index);
            }
        }


        /// <summary>
        /// Returns the managed memory to the pool and clears it for security.
        /// The lifecycle activity is finalized with a return event and then disposed.
        /// </summary>
        /// <remarks>
        /// If the pool or slab has already been disposed (e.g. during application shutdown),
        /// the return operation fails gracefully. The lifecycle activity records an error
        /// status but no exception propagates, since throwing from Dispose causes cascading
        /// failures in <see langword="finally"/> blocks.
        /// </remarks>
        public void Dispose()
        {
            if(!Disposed)
            {
                try
                {
                    LifecycleActivity?.AddEvent(new ActivityEvent("Return", tags: new ActivityTagsCollection
                    {
                        { "segmentSize", Slab.SegmentSize },
                        { "segmentIndex", Index }
                    }));

                    Pool.Return(Slab, Index);
                    LifecycleActivity?.SetStatus(ActivityStatusCode.Ok);
                }
                catch(ObjectDisposedException ex)
                {
                    //The pool or slab was disposed before this rental was returned.
                    //This is expected during shutdown or when the pool is disposed
                    //while rentals are still outstanding.
                    LifecycleActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                }
                catch(Exception ex)
                {
                    LifecycleActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    throw;
                }
                finally
                {
                    LifecycleActivity?.Dispose();
                    Disposed = true;
                }
            }
        }
    }


    /// <summary>
    /// Provides ownership of a native (locked) memory buffer supplied per rent by an injected
    /// <see cref="NativeBackingAllocator"/>. Native rentals are not slab-pooled; the wrapped owner is
    /// responsible for zeroing, unlocking and freeing its memory on disposal. This wrapper threads the
    /// lifecycle activity and the pool's return accounting.
    /// </summary>
    [DebuggerDisplay("NativeMemoryOwner: Disposed={Disposed}")]
    private sealed class NativeMemoryOwner: IMemoryOwner<byte>
    {
        private Activity? LifecycleActivity { get; }

        private IMemoryOwner<byte> Inner { get; }

        private BaseMemoryPool Pool { get; }

        private bool Disposed { get; set; }


        /// <summary>
        /// Initializes a new instance wrapping a native owner supplied by the backing allocator.
        /// </summary>
        /// <param name="inner">The native owner whose disposal zeroes, unlocks and frees the memory.</param>
        /// <param name="pool">The memory pool that served the rental.</param>
        /// <param name="lifecycleActivity">
        /// The activity tracking this rental. Ownership is transferred to this instance.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="inner"/> or <paramref name="pool"/> is null.
        /// </exception>
        public NativeMemoryOwner(
            IMemoryOwner<byte> inner,
            BaseMemoryPool pool,
            Activity? lifecycleActivity)
        {
            ArgumentNullException.ThrowIfNull(inner);
            ArgumentNullException.ThrowIfNull(pool);

            Inner = inner;
            Pool = pool;
            LifecycleActivity = lifecycleActivity;
            Disposed = false;
        }


        /// <summary>
        /// Gets the memory managed by the wrapped native owner.
        /// </summary>
        /// <exception cref="ObjectDisposedException">Thrown when this owner has been disposed.</exception>
        public Memory<byte> Memory
        {
            get
            {
                ObjectDisposedException.ThrowIf(Disposed, nameof(NativeMemoryOwner));
                return Inner.Memory;
            }
        }


        /// <summary>
        /// Disposes the wrapped native owner (which zeroes, unlocks and frees) and finalizes the
        /// lifecycle activity. Pool return accounting fails gracefully if the pool is already disposed.
        /// </summary>
        public void Dispose()
        {
            if(!Disposed)
            {
                try
                {
                    LifecycleActivity?.AddEvent(new ActivityEvent("Return"));

                    Inner.Dispose();

                    try
                    {
                        Pool.ReturnNative();
                    }
                    catch(ObjectDisposedException)
                    {
                        //The pool's meter was disposed before this rental was returned. The native owner
                        //has already released its memory above; only the return-count accounting is lost.
                    }

                    LifecycleActivity?.SetStatus(ActivityStatusCode.Ok);
                }
                catch(Exception ex)
                {
                    LifecycleActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    throw;
                }
                finally
                {
                    LifecycleActivity?.Dispose();
                    Disposed = true;
                }
            }
        }
    }
}
