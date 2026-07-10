using System.Buffers;
using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;

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
/// <see cref="AllocationKind.Native"/> request throws unless the pool was constructed to allow degradation
/// to <see cref="AllocationKind.Pinned"/>.
/// </para>
/// <para>
/// How native rentals are served is a pool-level choice (<see cref="NativeRentMode"/>): per-rent
/// isolated backing allocations (the default — maximum isolation for few long-lived keys, never
/// slab-pooled) or protected slabs (backing regions allocated on demand per buffer size, each
/// subdivided into exact-size segments bracketed by per-segment software canaries — locked-memory
/// density for many transient secrets; a stomped canary is detected on return, recorded on the
/// telemetry, and surfaced as a <see cref="CanaryViolationException"/>). The ratified family
/// pattern runs two pool instances side by side, one per mode.
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
    /// Dictionary mapping buffer size to protected native slabs (the kind is always
    /// <see cref="AllocationKind.Native"/>). Populated only when <see cref="NativeRentMode"/> is
    /// <see cref="NativeRentMode.ProtectedSlab"/>.
    /// </summary>
    private Dictionary<int, List<NativeSlab>> NativeSlabs { get; } = new();

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
    /// native requests throw unless <see cref="AllowNativeDegradation"/> is set.
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
    /// Counter tracking protected-slab canary violations detected on segment return.
    /// </summary>
    private Counter<long> CanaryViolationCounter { get; }

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
    /// Controls what happens when an <see cref="AllocationKind.Native"/> request arrives and no
    /// <see cref="NativeBackingAllocator"/> is wired. When <see langword="false"/> (the default) the request
    /// throws, so a pool that must hold secrets in native locked memory fails loud on misconfiguration rather
    /// than silently handing back weaker memory. When <see langword="true"/> the request degrades instead to
    /// <see cref="AllocationKind.Pinned"/> — the explicit portable-floor opt-in, the strongest tier a
    /// browser-clean leaf offers without a P/Invoke dependency.
    /// </summary>
    public bool AllowNativeDegradation { get; }

    /// <summary>
    /// How <see cref="AllocationKind.Native"/> rentals are served from the injected backing:
    /// per-rent isolated allocations (the default — maximum isolation for few long-lived keys) or
    /// protected slabs (on-demand backing regions subdivided with per-segment software canaries —
    /// locked-memory density for many transient secrets). See <see cref="Lumoin.Base.NativeRentMode"/>.
    /// </summary>
    public NativeRentMode NativeRentMode { get; }

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
    /// The shared instance uses the default capacity strategy, has tracing enabled, has no native backing
    /// wired, and is strict (so <see cref="AllocationKind.Native"/> throws — <see cref="Shared"/> is the
    /// general pool, not a secure key pool).
    /// </remarks>
    public static new BaseMemoryPool Shared => SharedInstance.Value;


    /// <summary>
    /// Initializes a new instance with default settings.
    /// </summary>
    /// <param name="nativeBacking">
    /// Optional native (locked) backing, supplied by a non-browser assembly. When <see langword="null"/>,
    /// an <see cref="AllocationKind.Native"/> request throws unless <paramref name="allowNativeDegradation"/>
    /// is <see langword="true"/>.
    /// </param>
    /// <param name="allowNativeDegradation">
    /// When <see langword="false"/> (the default), an <see cref="AllocationKind.Native"/> request with no
    /// <paramref name="nativeBacking"/> wired throws, so the pool fails loud on misconfiguration rather than
    /// silently handing back weaker memory. When <see langword="true"/>, such a request degrades to
    /// <see cref="AllocationKind.Pinned"/> — the explicit portable-floor opt-in. See <see cref="AllowNativeDegradation"/>.
    /// </param>
    /// <param name="nativeRentMode">
    /// How <see cref="AllocationKind.Native"/> rentals are served from the injected backing:
    /// <see cref="NativeRentMode.PerRentIsolated"/> (the default) allocates per rent for maximum
    /// isolation; <see cref="NativeRentMode.ProtectedSlab"/> subdivides on-demand backing regions
    /// with per-segment software canaries for locked-memory density. See <see cref="NativeRentMode"/>.
    /// </param>
    public BaseMemoryPool(NativeBackingAllocator? nativeBacking = null, bool allowNativeDegradation = false, NativeRentMode nativeRentMode = NativeRentMode.PerRentIsolated)
        : this(new Meter(BaseMemoryPoolMetrics.MeterName, "1.0.0"), nativeBacking: nativeBacking, allowNativeDegradation: allowNativeDegradation, nativeRentMode: nativeRentMode)
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
    /// an <see cref="AllocationKind.Native"/> request throws unless <paramref name="allowNativeDegradation"/>
    /// is <see langword="true"/>.
    /// </param>
    /// <param name="allowNativeDegradation">
    /// When <see langword="false"/> (the default), an <see cref="AllocationKind.Native"/> request with no
    /// <paramref name="nativeBacking"/> wired throws, so the pool fails loud on misconfiguration rather than
    /// silently handing back weaker memory. When <see langword="true"/>, such a request degrades to
    /// <see cref="AllocationKind.Pinned"/> — the explicit portable-floor opt-in. See <see cref="AllowNativeDegradation"/>.
    /// </param>
    /// <param name="nativeRentMode">
    /// How <see cref="AllocationKind.Native"/> rentals are served from the injected backing:
    /// <see cref="NativeRentMode.PerRentIsolated"/> (the default) allocates per rent for maximum
    /// isolation; <see cref="NativeRentMode.ProtectedSlab"/> subdivides on-demand backing regions
    /// with per-segment software canaries for locked-memory density. See <see cref="NativeRentMode"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="meter"/> is null.</exception>
    public BaseMemoryPool(
        Meter meter,
        SlabCapacityStrategy? capacityStrategy = null,
        bool tracingEnabled = true,
        NativeBackingAllocator? nativeBacking = null,
        bool allowNativeDegradation = false,
        NativeRentMode nativeRentMode = NativeRentMode.PerRentIsolated)
    {
        ArgumentNullException.ThrowIfNull(meter);

        PoolMeter = meter;
        CapacityStrategy = capacityStrategy ?? DefaultCapacityStrategy;
        TracingEnabled = tracingEnabled;
        NativeBacking = nativeBacking;
        AllowNativeDegradation = allowNativeDegradation;
        NativeRentMode = nativeRentMode;
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

        CanaryViolationCounter = meter.CreateCounter<long>(
            BaseMemoryPoolMetrics.BaseMemoryPoolCanaryViolationsTotal,
            "violations",
            "Total number of protected-slab canary violations detected on segment return.");
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
    /// <see cref="NativeBackingAllocator"/> — per rent in <see cref="Lumoin.Base.NativeRentMode.PerRentIsolated"/>
    /// mode, or as a canary-bracketed segment of a shared backing region in
    /// <see cref="Lumoin.Base.NativeRentMode.ProtectedSlab"/> mode (see <see cref="NativeRentMode"/>).
    /// With no backing wired a native request throws unless <see cref="AllowNativeDegradation"/> is set,
    /// in which case it degrades to <see cref="AllocationKind.Pinned"/>.
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
        //per rent; when not, the request either degrades to Pinned — the strongest protection this
        //browser-clean leaf offers without a P/Invoke dependency — or fails loud, depending on
        //AllowNativeDegradation. The decision is made (and any throw raised) before the tracing activity is
        //started, so a disallowed-degradation request never leaves a dangling activity behind.
        AllocationKind effectiveKind = kind;
        bool degradedFromNative = false;
        if(kind == AllocationKind.Native && NativeBacking is null)
        {
            if(!AllowNativeDegradation)
            {
                throw new InvalidOperationException(
                    "Native requested but no NativeBackingAllocator is wired and this pool disallows degradation; wire a backing or construct with allowNativeDegradation:true to fall back to Pinned");
            }

            effectiveKind = AllocationKind.Pinned;
            degradedFromNative = true;
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
        activity?.AddTag("allocationKind", effectiveKind.ToString());

        //Native alone does not tell an operator how the secret is held — per-rent isolated and
        //protected-slab have different blast radii — so the mode is recorded on the lifecycle
        //activity for every native rent.
        if(effectiveKind == AllocationKind.Native)
        {
            activity?.AddTag("nativeRentMode", NativeRentMode.ToString());
        }

        if(degradedFromNative)
        {
            activity?.AddTag("requestedAllocationKind", AllocationKind.Native.ToString());
            activity?.AddEvent(new ActivityEvent("AllocationKindDegraded", tags: new ActivityTagsCollection
            {
                { "requested", "Native" },
                { "effective", "Pinned" }
            }));
        }

        BufferSizeHistogram.Record(bufferSize);

        //Native rentals are served by the injected backing. In per-rent isolated mode (the
        //default) each rent gets its own backing allocation and is never slab-pooled; in
        //protected-slab mode one backing region per (size, slab) is subdivided into exact-size
        //segments bracketed by per-segment software canaries — locked-memory density in place of
        //per-secret hardware guards.
        if(effectiveKind == AllocationKind.Native)
        {
            if(NativeRentMode == NativeRentMode.ProtectedSlab)
            {
                IMemoryOwner<byte> slabRental = RentFromProtectedSlab(bufferSize, activity);

                RentSuccessCounter.Add(1, new KeyValuePair<string, object?>("bufferSize", bufferSize));

                return slabRental;
            }

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
    /// Per-rent isolated native rentals are not slab-pooled and so are unaffected by this method;
    /// protected native slabs with no active rentals ARE reclaimed (their backing region is
    /// zeroed and disposed, releasing the locked memory).
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

            foreach(var nativeSlabList in NativeSlabs.Values)
            {
                for(int i = nativeSlabList.Count - 1; i >= 0; i--)
                {
                    var nativeSlab = nativeSlabList[i];
                    if(nativeSlab.IsFull)
                    {
                        int segmentCount = nativeSlab.SegmentCount;
                        long regionLength = nativeSlab.RegionLength;

                        nativeSlab.Dispose();
                        nativeSlabList.RemoveAt(i);

                        Interlocked.Decrement(ref totalSlabs);
                        Interlocked.Add(ref totalMemoryAllocated, -regionLength);
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

                    //Unlike the managed slabs above — whose backing arrays stay valid, GC-tracked
                    //objects after disposal, so a straggling reader just sees zeros — disposing a
                    //native slab UNLOCKS AND FREES real native memory. A region with outstanding
                    //rentals is therefore only marked: it zeroes and releases itself when its last
                    //rental returns, backstopped by the backing owner's finalizer if the rental
                    //leaks. Idle regions are released here immediately.
                    foreach(var nativeSlabList in NativeSlabs.Values)
                    {
                        foreach(var nativeSlab in nativeSlabList)
                        {
                            nativeSlab.DisposeWhenIdle();
                        }
                    }
                    NativeSlabs.Clear();

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
    /// Serves a protected-slab native rental: an existing slab with space when one exists,
    /// otherwise a freshly allocated backing region. The injected backing is invoked OUTSIDE the
    /// pool lock — it may make syscalls (mmap/mlock/VirtualLock) or take arbitrary time, and the
    /// per-rent isolated path already calls it unlocked — so two racing threads can both create a
    /// region for the same size; the loser's untouched region is disposed, a rare cost preferred
    /// over a pool-wide stall.
    /// </summary>
    /// <param name="bufferSize">The exact number of bytes required.</param>
    /// <param name="activity">The rental's lifecycle activity; ownership passes to the owner.</param>
    /// <returns>An owner over exactly <paramref name="bufferSize"/> bytes of a slab segment.</returns>
    private IMemoryOwner<byte> RentFromProtectedSlab(int bufferSize, Activity? activity)
    {
        using(LockObject.EnterScope())
        {
            if(TryRentFromExistingNativeSlab(bufferSize, activity, out IMemoryOwner<byte>? rental))
            {
                return rental;
            }
        }

        int capacity = CapacityStrategy(bufferSize);
        NativeSlab? freshSlab = NativeSlab.Create(bufferSize, capacity, NativeBacking!);

        try
        {
            using(LockObject.EnterScope())
            {
                //The pool may have been disposed while the backing allocated; registering the
                //fresh region into a disposed pool would leak it, so the finally releases it.
                ObjectDisposedException.ThrowIf(IsDisposed, this);

                if(TryRentFromExistingNativeSlab(bufferSize, activity, out IMemoryOwner<byte>? rental))
                {
                    //Another thread created capacity for this size while the backing allocated.
                    //The fresh region holds no secrets yet — the finally releases it; the winner's
                    //slab serves the rental.
                    return rental;
                }

                if(!NativeSlabs.TryGetValue(bufferSize, out List<NativeSlab>? nativeSlabList))
                {
                    nativeSlabList = new List<NativeSlab>();
                    NativeSlabs.Add(bufferSize, nativeSlabList);
                }

                nativeSlabList.Add(freshSlab);

                Interlocked.Increment(ref totalSlabs);
                Interlocked.Add(ref totalMemoryAllocated, freshSlab.RegionLength);
                Interlocked.Add(ref totalSegments, capacity);

                bool rentSuccess = freshSlab.TryRent(out int segmentIndex);
                Debug.Assert(rentSuccess, "A new native slab should always have available capacity.");

                Interlocked.Increment(ref activeRentals);

                var rentalFromFresh = new NativeSlabMemoryOwner(freshSlab, segmentIndex, this, activity);

                //Ownership transferred to the pool's slab list; the finally must not release it.
                freshSlab = null;

                return rentalFromFresh;
            }
        }
        finally
        {
            freshSlab?.Dispose();
        }
    }


    /// <summary>
    /// Attempts to serve a protected-slab rental from an already-registered slab with free
    /// capacity. Must be called under the pool lock.
    /// </summary>
    /// <param name="bufferSize">The exact number of bytes required.</param>
    /// <param name="activity">The rental's lifecycle activity; ownership passes to the owner.</param>
    /// <param name="rental">The rental when one was served; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when an existing slab served the rental.</returns>
    private bool TryRentFromExistingNativeSlab(int bufferSize, Activity? activity, [NotNullWhen(true)] out IMemoryOwner<byte>? rental)
    {
        if(NativeSlabs.TryGetValue(bufferSize, out List<NativeSlab>? nativeSlabList))
        {
            foreach(var nativeSlab in nativeSlabList)
            {
                if(nativeSlab.TryRent(out int segmentIndex))
                {
                    Interlocked.Increment(ref activeRentals);
                    rental = new NativeSlabMemoryOwner(nativeSlab, segmentIndex, this, activity);
                    return true;
                }
            }
        }

        rental = null;
        return false;
    }


    /// <summary>
    /// Records the return-side metrics for a per-rent isolated native rental. The native owner
    /// zeroes, unlocks and frees its own memory on disposal; the pool only accounts for it.
    /// </summary>
    private void ReturnNative()
    {
        using(LockObject.EnterScope())
        {
            ReturnCounter.Add(1);
        }
    }


    /// <summary>
    /// Returns a previously rented protected-slab segment to its native slab. The slab verifies
    /// the segment's canaries and zeroes the secret on every exit path; on a violation the
    /// segment is retired and a <see cref="CanaryViolationException"/> propagates — the rental is
    /// over either way, so the pool's accounting is settled before the exception surfaces.
    /// </summary>
    /// <param name="nativeSlab">The native slab that originally provided the segment.</param>
    /// <param name="index">The segment index to return.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="nativeSlab"/> is null.</exception>
    /// <exception cref="CanaryViolationException">
    /// Thrown when the segment's software canary was stomped: memory outside the rented
    /// exact-size span was written. The contents were zeroed and the segment retired.
    /// </exception>
    private void ReturnNativeSlab(NativeSlab nativeSlab, int index)
    {
        ArgumentNullException.ThrowIfNull(nativeSlab);

        using(LockObject.EnterScope())
        {
            try
            {
                nativeSlab.Return(index);
            }
            catch(CanaryViolationException)
            {
                Interlocked.Decrement(ref activeRentals);
                ReturnCounter.Add(1);
                CanaryViolationCounter.Add(1);

                throw;
            }

            Interlocked.Decrement(ref activeRentals);
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

            //The backing length is computed in 64-bit and bound-checked before the array is allocated, so a
            //large segment size times the slab capacity cannot silently overflow the int multiply into a
            //too-small (or negative) array that a later segment slice would then read past. Past the check the
            //product fits an int, which in turn makes every downstream index * SegmentSize slice provably
            //in-range (index < SegmentCount), so they need no further widening.
            long backingLength = (long)segmentSize * segmentCount;
            if(backingLength > Array.MaxLength)
            {
                throw new ArgumentOutOfRangeException(nameof(segmentCount),
                    $"A slab of {segmentCount} segments of {segmentSize} bytes each needs {backingLength} bytes, above the maximum array length of {Array.MaxLength}.");
            }

            SegmentSize = segmentSize;
            SegmentCount = segmentCount;
            int backingSize = (int)backingLength;
            Buffer = pinned
                ? GC.AllocateArray<byte>(backingSize, pinned: true)
                : new byte[backingSize];
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

            //Zero the segment for security before returning — house discipline is
            //CryptographicOperations.ZeroMemory (never Span.Clear), which the compiler cannot elide.
            CryptographicOperations.ZeroMemory(Buffer.AsSpan(index * SegmentSize, SegmentSize));

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
                CryptographicOperations.ZeroMemory(Buffer);
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


    /// <summary>
    /// One protected native slab: a single region from the injected
    /// <see cref="NativeBackingAllocator"/> subdivided into exact-size segments, each bracketed by
    /// per-segment software canaries. Density in place of per-secret hardware guards — hardware
    /// protection is page-granular, so many small secrets (up to the pool's capacity strategy)
    /// share one locked region instead of costing pages each. Canaries are laid down at
    /// construction and verified when a segment
    /// is returned; segment contents are zeroed with
    /// <see cref="CryptographicOperations.ZeroMemory(Span{byte})"/> on every exit path.
    /// Availability bookkeeping mirrors <see cref="Slab"/> (index-based, double-return
    /// protected); a violated segment is retired, never recycled.
    /// </summary>
    [DebuggerDisplay("NativeSlab: SegmentSize={SegmentSize}, Available={AvailableSegments.Count}/{SegmentCount}")]
    private sealed class NativeSlab: IDisposable
    {
        /// <summary>
        /// The length in bytes of each software canary. Two per segment, leading and trailing;
        /// a 16-byte leading canary also keeps segment data 16-byte aligned relative to the
        /// region start.
        /// </summary>
        internal const int CanarySize = 16;

        /// <summary>
        /// The size of each segment's data area in bytes — the exact rented length.
        /// </summary>
        public int SegmentSize { get; }

        /// <summary>
        /// The total number of segments in this slab.
        /// </summary>
        public int SegmentCount { get; }

        /// <summary>
        /// The full backing-region length in bytes (stride times segment count).
        /// </summary>
        public int RegionLength { get; }

        /// <summary>
        /// The distance in bytes between segment starts: leading canary, data, trailing canary.
        /// </summary>
        private int Stride { get; }

        /// <summary>
        /// The single backing allocation; per the seam contract its owner zeroes, unlocks, and
        /// frees on dispose.
        /// </summary>
        private IMemoryOwner<byte> Region { get; }

        /// <summary>
        /// This slab's random canary value, written into every segment's leading and trailing
        /// canary slots at construction. Not a secret — it exists to detect accidental overruns,
        /// not to resist an in-process attacker who can read memory anyway.
        /// </summary>
        private byte[] CanaryTemplate { get; }

        /// <summary>
        /// Stack tracking the indices of available segments for O(1) allocation.
        /// </summary>
        private Stack<int> AvailableSegments { get; }

        /// <summary>
        /// Tracks which segments are currently rented, preventing double-return corruption.
        /// </summary>
        private BitArray RentedSegments { get; }

        /// <summary>
        /// Indicates whether this slab has been disposed.
        /// </summary>
        private bool IsDisposed { get; set; }

        /// <summary>
        /// When set, the slab releases its region as soon as the last outstanding rental returns.
        /// Set by the pool's disposal for slabs that still have active rentals — freeing a region
        /// under a live rental would be a native use-after-free, unlike the managed tier where a
        /// disposed slab's array remains a valid object.
        /// </summary>
        private bool disposeWhenIdle;

        /// <summary>
        /// The number of segments permanently taken out of circulation by canary violations.
        /// </summary>
        private int retiredSegments;


        /// <summary>
        /// Allocates one backing region sized for <paramref name="segmentCount"/> canary-bracketed
        /// segments of <paramref name="segmentSize"/> bytes and prepares the slab over it.
        /// </summary>
        /// <param name="segmentSize">The exact rented size of each segment in bytes.</param>
        /// <param name="segmentCount">The number of segments the slab holds.</param>
        /// <param name="backing">The injected allocator that supplies the locked region.</param>
        /// <returns>The prepared slab.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when the region (segments plus canaries) would exceed the maximum backing
        /// allocation size.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the backing violates the exact-size seam contract.
        /// </exception>
        public static NativeSlab Create(int segmentSize, int segmentCount, NativeBackingAllocator backing)
        {
            if(segmentCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(segmentCount),
                    "Segment count must be greater than zero.");
            }

            //Mirrors the managed slab's 64-bit bound check: the region length must fit the int the
            //backing seam takes, so the multiply can never wrap into a too-small region that a
            //later segment slice would escape.
            long stride = (2L * CanarySize) + segmentSize;
            long regionLength = stride * segmentCount;
            if(regionLength > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(segmentCount),
                    $"A native slab of {segmentCount} segments of {segmentSize} bytes each (plus canaries) needs {regionLength} bytes, above the maximum backing allocation of {int.MaxValue} bytes.");
            }

            IMemoryOwner<byte> region = backing((int)regionLength);
            if(region.Memory.Length != (int)regionLength)
            {
                //A backing that violates the exact-size seam contract cannot be subdivided safely.
                int actualLength = region.Memory.Length;
                region.Dispose();

                throw new InvalidOperationException(
                    $"The native backing returned {actualLength} bytes for a requested region of {regionLength}; the NativeBackingAllocator contract is exact-size.");
            }

            return new NativeSlab(segmentSize, segmentCount, (int)stride, (int)regionLength, region);
        }


        /// <summary>
        /// Initializes the slab over an exact-size region already allocated by the backing.
        /// </summary>
        private NativeSlab(int segmentSize, int segmentCount, int stride, int regionLength, IMemoryOwner<byte> region)
        {
            SegmentSize = segmentSize;
            SegmentCount = segmentCount;
            Stride = stride;
            RegionLength = regionLength;
            Region = region;

            CanaryTemplate = new byte[CanarySize];
            RandomNumberGenerator.Fill(CanaryTemplate);

            //Canaries are laid down for every segment up front so a segment's brackets are
            //verifiable over its whole lifetime, not only after its first rent.
            var regionSpan = region.Memory.Span;
            for(int i = 0; i < segmentCount; i++)
            {
                CanaryTemplate.CopyTo(regionSpan.Slice(i * stride, CanarySize));
                CanaryTemplate.CopyTo(regionSpan.Slice((i * stride) + CanarySize + segmentSize, CanarySize));
            }

            RentedSegments = new BitArray(segmentCount, false);

            AvailableSegments = new Stack<int>(segmentCount);
            for(int i = 0; i < segmentCount; i++)
            {
                AvailableSegments.Push(i);
            }

            IsDisposed = false;
        }


        /// <summary>
        /// Gets a value indicating whether this slab has no active rentals — every segment is
        /// either available or retired — making its locked region reclaimable.
        /// </summary>
        public bool IsFull => AvailableSegments.Count + retiredSegments == SegmentCount;


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
        /// Gets the exact-size data slice for the given segment index — the bytes between the
        /// segment's leading and trailing canaries.
        /// </summary>
        /// <param name="index">The segment index.</param>
        /// <returns>A <see cref="Memory{T}"/> over exactly this segment's data area.</returns>
        public Memory<byte> SliceFor(int index) => Region.Memory.Slice((index * Stride) + CanarySize, SegmentSize);


        /// <summary>
        /// Returns a previously rented segment: verifies its canaries, zeroes the secret on every
        /// exit path, and either recycles the segment or — on a violation — retires it.
        /// </summary>
        /// <param name="index">The segment index to return.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="index"/> is outside the valid range for this slab.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the segment was not currently rented (double-return protection).
        /// </exception>
        /// <exception cref="CanaryViolationException">
        /// Thrown when a canary was stomped: memory outside the rented exact-size span was
        /// written. The contents were zeroed and the segment retired before the throw.
        /// </exception>
        /// <exception cref="ObjectDisposedException">Thrown when the slab has been disposed.</exception>
        public void Return(int index)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, nameof(NativeSlab));

            if(index < 0 || index >= SegmentCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index),
                    "Segment index is outside the range of this native slab.");
            }

            //Double-return protection: verify the segment is actually rented.
            if(!RentedSegments[index])
            {
                throw new InvalidOperationException(
                    "Segment was not rented or has already been returned.");
            }

            var regionSpan = Region.Memory.Span;
            bool leadingIntact = regionSpan.Slice(index * Stride, CanarySize).SequenceEqual(CanaryTemplate);
            bool trailingIntact = regionSpan.Slice((index * Stride) + CanarySize + SegmentSize, CanarySize).SequenceEqual(CanaryTemplate);

            //The secret is zeroed on every exit path, violation or not — house discipline is
            //CryptographicOperations.ZeroMemory (never Span.Clear), which the compiler cannot elide.
            CryptographicOperations.ZeroMemory(regionSpan.Slice((index * Stride) + CanarySize, SegmentSize));

            RentedSegments[index] = false;

            if(leadingIntact && trailingIntact)
            {
                AvailableSegments.Push(index);
                DisposeIfIdleAndMarked();
                return;
            }

            //A stomped canary means something wrote outside the rented exact-size span — native
            //interop handed a wrong length, or genuine corruption. The segment is retired rather
            //than recycled, and the failure surfaces loudly to the disposer.
            retiredSegments++;
            string stomped = !leadingIntact && !trailingIntact
                ? "leading and trailing canaries"
                : !leadingIntact ? "leading canary" : "trailing canary";
            DisposeIfIdleAndMarked();

            throw new CanaryViolationException(
                $"A protected-slab segment of {SegmentSize} bytes was returned with a stomped {stomped}: memory outside the rented span was written. The contents were zeroed and the segment retired.");
        }


        /// <summary>
        /// Marks this slab for disposal, or disposes it immediately when no rental is
        /// outstanding. Called by the pool's disposal; a marked slab releases its region as soon
        /// as the last outstanding rental returns, so a live rental never dereferences freed
        /// native memory.
        /// </summary>
        public void DisposeWhenIdle()
        {
            if(IsFull)
            {
                Dispose();
                return;
            }

            disposeWhenIdle = true;
        }


        /// <summary>
        /// Releases the region when a pool-disposal deferral is pending and the last outstanding
        /// rental has now returned. Runs on the return path, under the pool lock.
        /// </summary>
        private void DisposeIfIdleAndMarked()
        {
            if(disposeWhenIdle && IsFull)
            {
                Dispose();
            }
        }


        /// <summary>
        /// Zeroes the whole region — defense in depth; the region owner zeroes again per the seam
        /// contract — and disposes the backing allocation, unlocking and freeing the memory.
        /// </summary>
        public void Dispose()
        {
            if(!IsDisposed)
            {
                CryptographicOperations.ZeroMemory(Region.Memory.Span);
                Region.Dispose();
                AvailableSegments.Clear();
                IsDisposed = true;
            }
        }
    }


    /// <summary>
    /// Provides ownership of a protected-slab segment rented from a <see cref="BaseMemoryPool"/>.
    /// Disposal returns the segment: canaries are verified, the secret is zeroed, and a canary
    /// violation surfaces as a <see cref="CanaryViolationException"/> after the pool has settled
    /// its accounting and recorded the violation on the metrics and the lifecycle activity.
    /// </summary>
    [DebuggerDisplay("NativeSlabMemoryOwner: Size={Slab.SegmentSize}, Disposed={Disposed}")]
    private sealed class NativeSlabMemoryOwner: IMemoryOwner<byte>
    {
        /// <summary>
        /// Activity tracking the full rental lifecycle from rent to return.
        /// Null when tracing is disabled or no listener is attached.
        /// </summary>
        private Activity? LifecycleActivity { get; }

        private NativeSlab Slab { get; }

        private int Index { get; }

        private BaseMemoryPool Pool { get; }

        private bool Disposed { get; set; }


        /// <summary>
        /// Initializes a new instance managing the segment at the given index of the given slab.
        /// </summary>
        /// <param name="slab">The native slab that provided the segment.</param>
        /// <param name="index">The segment index within the slab.</param>
        /// <param name="pool">The memory pool that owns the slab.</param>
        /// <param name="lifecycleActivity">
        /// The activity tracking this rental. Ownership is transferred to this instance.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="slab"/> or <paramref name="pool"/> is null.
        /// </exception>
        public NativeSlabMemoryOwner(
            NativeSlab slab,
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
        /// Gets the exact-size memory managed by this owner.
        /// </summary>
        /// <exception cref="ObjectDisposedException">Thrown when this owner has been disposed.</exception>
        public Memory<byte> Memory
        {
            get
            {
                ObjectDisposedException.ThrowIf(Disposed, nameof(NativeSlabMemoryOwner));
                return Slab.SliceFor(Index);
            }
        }


        /// <summary>
        /// Returns the segment to its native slab. The slab verifies canaries and zeroes the
        /// secret; the pool settles accounting. A <see cref="CanaryViolationException"/>
        /// propagates deliberately — the caller must learn its interop stomped memory — while a
        /// pool disposed during shutdown is tolerated silently, as with the other owners.
        /// </summary>
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

                    Pool.ReturnNativeSlab(Slab, Index);
                    LifecycleActivity?.SetStatus(ActivityStatusCode.Ok);
                }
                catch(ObjectDisposedException ex)
                {
                    //The pool or slab was disposed before this rental was returned — expected
                    //during shutdown; the lifecycle activity records the error, nothing propagates.
                    LifecycleActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                }
                catch(CanaryViolationException ex)
                {
                    //The violation is a first-class telemetry event on the lifecycle activity; the
                    //exception itself stays loud.
                    LifecycleActivity?.AddEvent(new ActivityEvent("CanaryViolation", tags: new ActivityTagsCollection
                    {
                        { "segmentSize", Slab.SegmentSize },
                        { "segmentIndex", Index }
                    }));
                    LifecycleActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);

                    throw;
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
