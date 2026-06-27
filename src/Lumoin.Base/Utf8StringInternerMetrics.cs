namespace Lumoin.Base;

/// <summary>
/// Centralized constants for the <see cref="Utf8StringInterner"/> metrics and meter name. A single
/// discoverable location for consumers who reference metric names for monitoring, alerting, or
/// dashboard configuration.
/// </summary>
/// <remarks>
/// Register <see cref="MeterName"/> with your metrics collection infrastructure (for example
/// OpenTelemetry) to collect all instruments created by any <see cref="System.Diagnostics.Metrics.Meter"/>
/// with that name, including the ones a <see cref="Utf8StringInterner"/> creates when given a meter.
/// <code>
/// services.AddOpenTelemetry()
///     .WithMetrics(builder => builder
///         .AddMeter(Utf8StringInternerMetrics.MeterName)
///         .AddPrometheusExporter());
/// </code>
/// </remarks>
public static class Utf8StringInternerMetrics
{
    /// <summary>
    /// Meter name for the UTF-8 string interner instruments. Register this name in your metrics collection
    /// configuration to collect the interner's intern counters and gauges.
    /// </summary>
    public static readonly string MeterName = "Lumoin.Base.Utf8StringInterner";


    /// <summary>
    /// Counter tracking total intern operations (hits plus misses).
    /// Unit: operations (cumulative count)
    /// </summary>
    public static readonly string InternOperationsTotal = "Lumoin.Utf8StringInterner.InternOperationsTotal";


    /// <summary>
    /// Counter tracking intern cache hits (a hot or cold value returned without allocation).
    /// Unit: operations (cumulative count)
    /// </summary>
    public static readonly string InternHitsTotal = "Lumoin.Utf8StringInterner.InternHitsTotal";


    /// <summary>
    /// Counter tracking generation rotations, each of which evicts a cold generation.
    /// Unit: operations (cumulative count)
    /// </summary>
    public static readonly string RotationsTotal = "Lumoin.Utf8StringInterner.RotationsTotal";


    /// <summary>
    /// Observable gauge tracking the approximate live interned value count across both generations.
    /// Unit: strings (count)
    /// </summary>
    public static readonly string LiveCount = "Lumoin.Utf8StringInterner.LiveCount";
}
