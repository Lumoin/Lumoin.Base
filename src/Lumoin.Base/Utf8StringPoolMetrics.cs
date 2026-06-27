namespace Lumoin.Base;

/// <summary>
/// Centralized constants for the <see cref="Utf8StringPool"/> metrics and meter name. A single
/// discoverable location for consumers who reference metric names for monitoring, alerting, or
/// dashboard configuration.
/// </summary>
/// <remarks>
/// Register <see cref="MeterName"/> with your metrics collection infrastructure (for example
/// OpenTelemetry) to collect all instruments created by any <see cref="System.Diagnostics.Metrics.Meter"/>
/// with that name, including the ones a <see cref="Utf8StringPool"/> creates when given a meter.
/// <code>
/// services.AddOpenTelemetry()
///     .WithMetrics(builder => builder
///         .AddMeter(Utf8StringPoolMetrics.MeterName)
///         .AddPrometheusExporter());
/// </code>
/// </remarks>
public static class Utf8StringPoolMetrics
{
    /// <summary>
    /// Meter name for the UTF-8 string pool instruments. Register this name in your metrics collection
    /// configuration to collect the pool's intern counters and gauges.
    /// </summary>
    public static readonly string MeterName = "Lumoin.Base.Utf8StringPool";


    /// <summary>
    /// Counter tracking total intern operations (hits plus misses).
    /// Unit: operations (cumulative count)
    /// </summary>
    public static readonly string InternOperationsTotal = "Lumoin.Utf8StringPool.InternOperationsTotal";


    /// <summary>
    /// Counter tracking intern cache hits (an existing value returned without allocation).
    /// Unit: operations (cumulative count)
    /// </summary>
    public static readonly string InternHitsTotal = "Lumoin.Utf8StringPool.InternHitsTotal";


    /// <summary>
    /// Observable counter tracking the number of unique values interned in the pool.
    /// Unit: strings (count)
    /// </summary>
    public static readonly string UniqueCount = "Lumoin.Utf8StringPool.UniqueCount";


    /// <summary>
    /// Observable counter tracking the total bytes interned in the pool.
    /// Unit: bytes
    /// </summary>
    public static readonly string TotalBytesInterned = "Lumoin.Utf8StringPool.TotalBytesInterned";
}
