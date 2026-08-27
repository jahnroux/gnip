namespace Gnip;

/// <summary>
/// Detects which configured WAN line is currently carrying traffic by resolving this host's
/// public egress IP (via OpenDNS) and matching it to a line's CIDR. Records a transition in the
/// store whenever the active line changes, and keeps <see cref="LineState"/> current for the API.
/// Reads <see cref="GnipSettings"/> live, so lines added or edited from the settings UI take
/// effect immediately (it wakes early on a settings change rather than waiting out the interval).
/// Idles — rather than exiting — while no lines are configured, so the feature can be switched on
/// at runtime. Like the ping collector, no per-iteration fault (lookup, DB, or a misbehaving
/// logger) is allowed to tear this loop down.
/// </summary>
public sealed class LineMonitor : BackgroundService
{
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Re-check cadence while no lines are configured; a safety net behind the wake signal.</summary>
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(60);

    private readonly PingStore _store;
    private readonly LineState _state;
    private readonly GnipSettings _settings;
    private readonly ILogger<LineMonitor> _log;

    public LineMonitor(PingStore store, LineState state, GnipSettings settings, ILogger<LineMonitor> log)
    {
        _store = store;
        _state = state;
        _settings = settings;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var wake = new SemaphoreSlim(0, 1);
        void onChanged() { try { wake.Release(); } catch (SemaphoreFullException) { } }
        _settings.Changed += onChanged;

        // Seed from the last persisted transition so a service restart does not log a spurious
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

        var lines = new List<(string Name, Cidr Cidr)>();
        string? signature = null; // the line list this `lines` was parsed from

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var s = _settings.Current;

                    // Re-parse only when the configured list actually changed.
                    var sig = string.Join(" ", s.Lines.Select(l => l.Name + "=" + l.Ip));
                    if (sig != signature)
                    {
                        var reconfigured = signature is not null;
                        signature = sig;
                        lines = Parse(s.Lines);
                        _state.SetConfigured(lines.Count > 0);

                        if (lines.Count == 0)
                        {
                            // Forget the last known line: with nothing configured there is nothing
                            // to report, and re-adding lines should read as a fresh detection
                            // rather than a failover away from a line that no longer exists.
                            lastName = null;
                            _state.Update(null, null, 0, false);
                            SafeLog(LogLevel.Information, null, "LineMonitor: no WAN lines configured; detection off.");
                        }
                        else
                        {
                            SafeLog(LogLevel.Information, null, "LineMonitor {Verb}: {Count} line(s), checking every {Sec}s",
                                reconfigured ? "reconfigured" : "started", lines.Count, Math.Max(5, s.LineCheckSeconds));
                        }
                    }

                    if (lines.Count == 0)
                    {
                        // Nothing to detect — wait for a settings change (or poll occasionally).
                        await wake.WaitAsync(IdleInterval, stoppingToken);
                        continue;
                    }

                    lastName = await CheckOnceAsync(lines, lastName, stoppingToken);

                    await wake.WaitAsync(TimeSpan.FromSeconds(Math.Max(5, s.LineCheckSeconds)), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    SafeLog(LogLevel.Error, ex, "LineMonitor iteration failed; continuing");
                    try { await Task.Delay(1000, stoppingToken); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
        finally
        {
            _settings.Changed -= onChanged;
        }

        SafeLog(LogLevel.Information, null, "LineMonitor stopped.");
    }

    /// <summary>
    /// Resolve the egress IP once and reconcile it against the configured lines, recording a
    /// transition if the active line changed. Returns the name of the active line.
    /// </summary>
    private async Task<string?> CheckOnceAsync(List<(string Name, Cidr Cidr)> lines, string? lastName, CancellationToken ct)
    {
        var ip = await EgressIpResolver.ResolveAsync((ushort)Random.Shared.Next(1, 65536), LookupTimeout, ct);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (ip is null)
        {
            // Keep the last known line; just flag the lookup as currently failing.
            var cur = _state.Current;
            _state.Update(cur.Name, cur.Ip, cur.SinceMs, false);
            SafeLog(LogLevel.Debug, null, "Egress IP lookup failed");
            return lastName;
        }

        var ipStr = ip.ToString();
        var name = GnipSettings.UnknownLineName; // resolved, but matched no configured line
        foreach (var (n, c) in lines)
            if (c.Contains(ip)) { name = n; break; }

        if (name != lastName)
        {
            await _store.InsertLineEventAsync(nowMs, name, ipStr, ct);
            if (lastName is null)
                SafeLog(LogLevel.Information, null, "WAN line: {Line} ({Ip})", name, ipStr);
            else
                SafeLog(LogLevel.Warning, null, "WAN line changed: {Old} -> {New} ({Ip})", lastName, name, ipStr);
            _state.Update(name, ipStr, nowMs, true);
            return name;
        }
        else
        {
            // Same line: refresh ip/ok but preserve the original "since".
            var since = _state.Current.SinceMs;
            _state.Update(name, ipStr, since == 0 ? nowMs : since, true);
            return lastName;
        }
    }

    /// <summary>
    /// Parse the configured lines, skipping (with a warning) any that are malformed. Settings
    /// validation rejects bad input from the API, so this only fires on a hand-edited file.
    /// </summary>
    private List<(string Name, Cidr Cidr)> Parse(IReadOnlyList<LineDef> configured)
    {
        var lines = new List<(string Name, Cidr Cidr)>();
        foreach (var l in configured)
        {
            if (!string.IsNullOrWhiteSpace(l.Name) && Cidr.TryParse(l.Ip, out var c) && c is not null)
                lines.Add((l.Name, c));
            else
                SafeLog(LogLevel.Warning, null, "Ignoring invalid WAN line config (name={Name} ip={Ip})", l.Name, l.Ip);
        }
        return lines;
    }

    private void SafeLog(LogLevel level, Exception? ex, string message, params object?[] args)
    {
        try { _log.Log(level, ex, message, args); }
        catch { /* logging is best-effort; it must never crash the monitor */ }
    }
}
