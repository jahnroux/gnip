namespace Gnip;

/// <summary>Outcome of a single probe.</summary>
public enum ProbeStatus
{
    Success = 0,
    TimedOut = 1,
    Unreachable = 2,
    Error = 3,
}

/// <summary>One ping result. <see cref="RttMs"/> is null for any non-success outcome.</summary>
/// <param name="Ts">Unix epoch milliseconds (UTC) when the probe was issued.</param>
/// <param name="RttMs">Round-trip time in milliseconds, or null on loss/error.</param>
/// <param name="Status">Classified outcome.</param>
public sealed record PingSample(long Ts, double? RttMs, ProbeStatus Status);

/// <summary>
/// Downsampled series for a time range as parallel columnar arrays (one entry per bucket).
/// <see cref="Avg"/>/<see cref="Min"/>/<see cref="Max"/> are null for buckets with no successful
/// samples; <see cref="Loss"/> is the lost fraction (0..1). <see cref="T"/> is the bucket-start
/// in unix epoch ms.
/// </summary>
public sealed record SeriesResult(
    long BucketMs,
    long[] T,
    double?[] Avg,
    double?[] Min,
    double?[] Max,
    double[] Loss);

/// <summary>A WAN-line transition: the active line changed to <see cref="Name"/> at <see cref="Ts"/>.</summary>
/// <param name="Ts">Unix epoch milliseconds (UTC) of the switch.</param>
/// <param name="Name">The line that became active (or "Unknown").</param>
/// <param name="Ip">The detected public egress IP at that time, if known.</param>
public sealed record LineEvent(long Ts, string Name, string? Ip);
