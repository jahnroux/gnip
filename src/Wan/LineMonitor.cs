using Microsoft.Extensions.Options;

namespace Gnip;

/// <summary>
/// Detects which configured WAN line is currently carrying traffic by resolving this host's
/// public egress IP (via OpenDNS) and matching it to a line's CIDR. Records a transition in the
/// store whenever the active line changes, and keeps <see cref="LineState"/> current for the API.
/// A no-op when no lines are configured. Like the ping collector, no per-iteration fault
/// (lookup, DB, or a misbehaving logger) is allowed to tear this loop down.
/// </summary>
public sealed class LineMonitor : BackgroundService
{
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(3);

    private readonly PingStore _store;
    private readonly LineState _state;
    private readonly IOptions<GnipOptions> _opts;
    private readonly ILogger<LineMonitor> _log;

    public LineMonitor(PingStore store, LineState state, IOptions<GnipOptions> opts, ILogger<LineMonitor> log)
    {
        _store = store;
        _state = state;
        _opts = opts;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = _opts.Value;

        // Parse the configured lines once; skip (with a warning) any that are malformed.
        var lines = new List<(string Name, Cidr Cidr)>();
        foreach (var l in o.Lines)
        {
            if (!string.IsNullOrWhiteSpace(l.Name) && Cidr.TryParse(l.Ip, out var c) && c is not null)
                lines.Add((l.Name!, c));
            else
                SafeLog(LogLevel.Warning, null, "Ignoring invalid WAN line config (name={Name} ip={Ip})", l.Name, l.Ip);
        }

        _state.SetConfigured(lines.Count > 0);
        if (lines.Count == 0)
        {
            SafeLog(LogLevel.Information, null, "LineMonitor: no WAN lines configured; not monitoring.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(5, o.LineCheckSeconds));
        SafeLog(LogLevel.Information, null, "LineMonitor started: {Count} line(s), checking every {Sec}s", lines.Count, (int)interval.TotalSeconds);

        // Seed from the last persisted transition so a service restart doesn't log a spurious
        // "change" and the API shows the current line immediately.
        string? lastName = null;
        try
        {
            var last = await _store.GetLastLineEventAsync(stoppingToken);
            if (last is not null)
            {
                lastName = last.Name;
                _state.Update(last.Name, last.Ip, last.Ts, false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        catch (Exception ex) { SafeLog(LogLevel.Warning, ex, "Could not read last WAN line event"); }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var ip = await EgressIpResolver.ResolveAsync((ushort)Random.Shared.Next(1, 65536), LookupTimeout, stoppingToken);
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                if (ip is null)
                {
                    // Keep the last known line; just flag the lookup as currently failing.
                    var cur = _state.Current;
                    _state.Update(cur.Name, cur.Ip, cur.SinceMs, false);
                    SafeLog(LogLevel.Debug, null, "Egress IP lookup failed");
                }
                else
                {
                    var ipStr = ip.ToString();
                    string name = "Unknown"; // resolved, but matched no configured line
                    foreach (var (n, c) in lines)
                        if (c.Contains(ip)) { name = n; break; }

                    if (name != lastName)
                    {
                        await _store.InsertLineEventAsync(nowMs, name, ipStr, stoppingToken);
                        if (lastName is null)
                            SafeLog(LogLevel.Information, null, "WAN line: {Line} ({Ip})", name, ipStr);
                        else
                            SafeLog(LogLevel.Warning, null, "WAN line changed: {Old} -> {New} ({Ip})", lastName, name, ipStr);
                        _state.Update(name, ipStr, nowMs, true);
                        lastName = name;
                    }
                    else
                    {
                        // Same line: refresh ip/ok but preserve the original "since".
                        var since = _state.Current.SinceMs;
                        _state.Update(name, ipStr, since == 0 ? nowMs : since, true);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SafeLog(LogLevel.Error, ex, "LineMonitor iteration failed; continuing");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        SafeLog(LogLevel.Information, null, "LineMonitor stopped.");
    }

    private void SafeLog(LogLevel level, Exception? ex, string message, params object?[] args)
    {
        try { _log.Log(level, ex, message, args); }
        catch { /* logging is best-effort; it must never crash the monitor */ }
    }
}
