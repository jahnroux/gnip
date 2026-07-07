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

    /// <summary>
    /// WAN lines for failover detection (empty = feature off). Order matters: the first entry is
    /// treated as the primary line. The active line is detected by matching this host's public
    /// egress IP against each line's CIDR.
    /// </summary>
    public List<LineConfig> Lines { get; set; } = [];

    /// <summary>Seconds between public-egress-IP checks used to detect the active WAN line.</summary>
    public int LineCheckSeconds { get; set; } = 15;
}

/// <summary>One WAN line: a display name and the public IP/CIDR its traffic egresses from.</summary>
public sealed class LineConfig
{
    public string? Name { get; set; }

    /// <summary>Public IPv4 or CIDR, e.g. "203.0.113.5/32" (single IP) or "41.164.173.112/30".</summary>
    public string? Ip { get; set; }
}
