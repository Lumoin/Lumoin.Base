namespace Lumoin.Base;

/// <summary>
/// Centralized constants for the <see cref="BaseMemoryPool"/> metrics and meter name.
/// This class provides a single discoverable location for consumers who need to
/// reference metric names for monitoring, alerting, or dashboard configuration.
/// </summary>
/// <remarks>
/// Meter names are used by metrics collection infrastructure (like OpenTelemetry)
/// to register which meters to collect from. When you register a meter name like
/// "Lumoin.Base", the collector will gather ALL instruments (counters, histograms,
/// gauges) created by ANY Meter instance with that exact name.
///
/// Example usage in application startup:
/// <code>
/// services.AddOpenTelemetry()
///     .WithMetrics(builder => builder
///         .AddMeter(BaseMemoryPoolMetrics.MeterName)
///         .AddPrometheusExporter());
/// </code>
///
/// Individual metric names are used by monitoring dashboards, alert rules, and
/// analysis tools to query specific metrics.
/// </remarks>
public static class BaseMemoryPoolMetrics
{
    /// <summary>
    /// Meter name for the base-memory-pool instruments.
    /// Register this meter name in your metrics collection configuration to collect
    /// the memory pool operation and performance counters.
    /// </summary>
    /// <remarks>
    /// When registered, this meter will collect metrics from all components that create
    /// Meter instances with this name, including <see cref="BaseMemoryPool"/> instances.
    /// </remarks>
    public static readonly string MeterName = "Lumoin.Base";


    //BaseMemoryPool metrics - use these names for dashboard queries and alerts.

    /// <summary>
    /// Observable counter tracking total number of memory slabs across all buffer sizes.
    /// Higher values may indicate memory pressure or fragmentation.
    /// Unit: slabs (count)
    /// </summary>
    public static readonly string BaseMemoryPoolTotalSlabs = "Lumoin.BaseMemoryPool.TotalSlabs";

    /// <summary>
    /// Observable counter tracking total memory allocated across all slabs.
    /// Includes both used and available segments.
    /// Unit: bytes
    /// </summary>
    public static readonly string BaseMemoryPoolTotalMemoryAllocated = "Lumoin.BaseMemoryPool.TotalMemoryAllocated";

    /// <summary>
    /// Observable counter tracking number of currently rented memory segments.
    /// Indicates current memory pressure and active cryptographic operations.
    /// Unit: segments (count)
    /// </summary>
    public static readonly string BaseMemoryPoolActiveRentals = "Lumoin.BaseMemoryPool.ActiveRentals";

    /// <summary>
    /// Observable counter tracking allocation efficiency as a percentage.
    /// Calculated as (active rentals / total allocated segments) * 100.
    /// Unit: percent (0-100)
    /// </summary>
    public static readonly string BaseMemoryPoolAllocationEfficiency = "Lumoin.BaseMemoryPool.AllocationEfficiency";

    /// <summary>
    /// Histogram tracking distribution of requested buffer sizes.
    /// Helps identify optimization opportunities for common cryptographic buffer sizes.
    /// Unit: bytes
    /// </summary>
    public static readonly string BaseMemoryPoolBufferSizeDistribution = "Lumoin.BaseMemoryPool.BufferSizeDistribution";

    /// <summary>
    /// Counter tracking total number of successful rent operations.
    /// Used for calculating allocation rates and throughput metrics.
    /// Unit: operations (cumulative count)
    /// </summary>
    public static readonly string BaseMemoryPoolRentOperationsTotal = "Lumoin.BaseMemoryPool.RentOperationsTotal";

    /// <summary>
    /// Counter tracking total number of memory return operations.
    /// Should correlate with rent operations for proper resource management.
    /// Unit: operations (cumulative count)
    /// </summary>
    public static readonly string BaseMemoryPoolReturnOperationsTotal = "Lumoin.BaseMemoryPool.ReturnOperationsTotal";
}
