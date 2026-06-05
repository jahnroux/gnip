namespace Gnip;

/// <summary>Runtime configuration, bound from the "Gnip" section of appsettings.json.</summary>
public sealed class GnipOptions
{
    public const string SectionName = "Gnip";

    /// <summary>Host name or IP address to ping.</summary>
    public string Host { get; set; } = "8.8.8.8";

    /// <summary>Seconds between pings.</summary>
    public int IntervalSeconds { get; set; } = 1;

    /// <summary>Per-ping timeout in milliseconds.</summary>
    public int TimeoutMs { get; set; } = 1000;

    /// <summary>SQLite file path (relative to the content root unless absolute).</summary>
    public string DbPath { get; set; } = "gnip.db";

    /// <summary>How long to keep samples. Not enforced until M3.</summary>
    public int RetentionHours { get; set; } = 48;

    /// <summary>Width of the live rolling window shown by the web UI, in seconds.</summary>
    public int LiveWindowSeconds { get; set; } = 600;

    /// <summary>Latency threshold (ms) drawn as a reference line on the graph.</summary>
    public int HighLatencyMs { get; set; } = 100;
}
