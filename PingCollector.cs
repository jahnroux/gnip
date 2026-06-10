using System.Diagnostics;
using System.Net.NetworkInformation;

namespace Gnip;

/// <summary>
/// Background loop that pings the configured host on a fixed cadence and persists + broadcasts
/// each result. Reads <see cref="GnipSettings"/> live, so host/interval/timeout changes apply
/// immediately (it wakes early when settings change rather than waiting out the current interval).
/// Uses the Win32 IP Helper API under the hood (no elevation needed on Windows).
/// </summary>
public sealed class PingCollector : BackgroundService
{
    private readonly PingStore _store;
    private readonly SampleHub _hub;
    private readonly GnipSettings _settings;
    private readonly ILogger<PingCollector> _log;

    public PingCollector(PingStore store, SampleHub hub, GnipSettings settings, ILogger<PingCollector> log)
    {
        _store = store;
        _hub = hub;
        _settings = settings;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var wake = new SemaphoreSlim(0, 1);
        void onChanged() { try { wake.Release(); } catch (SemaphoreFullException) { } }
        _settings.Changed += onChanged;

        var last = _settings.Current;
        SafeLog(LogLevel.Information, null, "Collector started: host={Host} interval={Interval}s timeout={Timeout}ms",
            last.Host, last.IntervalSeconds, last.TimeoutMs);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var s = _settings.Current;
                    if (s.Host != last.Host || s.IntervalSeconds != last.IntervalSeconds || s.TimeoutMs != last.TimeoutMs)
                    {
                        SafeLog(LogLevel.Information, null, "Collector reconfigured: host={Host} interval={Interval}s timeout={Timeout}ms",
                            s.Host, s.IntervalSeconds, s.TimeoutMs);
                        last = s;
                    }

                    var sw = Stopwatch.StartNew();
                    await ProbeOnceAsync(s, stoppingToken);

                    // Sleep the remainder of the interval, but wake early if settings change.
                    var wait = TimeSpan.FromMilliseconds(Math.Max(100L, (long)s.IntervalSeconds * 1000)) - sw.Elapsed;
                    if (wait > TimeSpan.Zero)
                        await wake.WaitAsync(wait, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break; // genuine shutdown — leave the loop
                }
                catch (Exception ex)
                {
                    // One bad iteration — a failed probe, a DB hiccup, or even a logging
                    // provider that throws (the Windows EventLog source can raise
                    // FileNotFoundException) — must NEVER tear down the collector. Log
                    // defensively and continue after a brief pause so a persistent fault
                    // can't turn into a tight CPU loop.
                    SafeLog(LogLevel.Error, ex, "Collector iteration failed; continuing");
                    try { await Task.Delay(1000, stoppingToken); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
        finally
        {
            _settings.Changed -= onChanged;
        }

        SafeLog(LogLevel.Information, null, "Collector stopped.");
    }

    /// <summary>
    /// Write a log entry without ever letting a misbehaving log provider escape. The Windows
    /// EventLog provider can throw (e.g. an unregistered event source surfaces as
    /// FileNotFoundException) — and a background loop must never die because it tried to log.
    /// </summary>
    private void SafeLog(LogLevel level, Exception? ex, string message, params object?[] args)
    {
        try { _log.Log(level, ex, message, args); }
        catch { /* logging is best-effort; it must never crash the collector */ }
    }

    private async Task ProbeOnceAsync(GnipSettings.Snapshot s, CancellationToken ct)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        double? rtt = null;
        ProbeStatus status = ProbeStatus.Error;
        IPStatus replyStatus = IPStatus.Unknown;

        // The OS ping timeout is not a hard guarantee on the async path: a stuck ICMP
        // completion can wedge this await indefinitely while the rest of the host keeps
        // running (observed in the field — the collector silently stopped writing samples
        // even though the web server stayed up). So bound every probe with an independent
        // watchdog and a fresh Ping instance; the loop can never block longer than this,
        // no matter how the underlying ping behaves.
        var hardTimeout = TimeSpan.FromMilliseconds(Math.Max(3_000L, (long)s.TimeoutMs * 3));
        var ping = new Ping();
        var pingTask = ping.SendPingAsync(s.Host, TimeSpan.FromMilliseconds(s.TimeoutMs));

        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var finished = await Task.WhenAny(pingTask, Task.Delay(hardTimeout, watchdog.Token));
        watchdog.Cancel(); // stop the timer if the ping returned first

        if (finished != pingTask)
        {
            // Shutdown, or the watchdog tripped on a stuck probe.
            ct.ThrowIfCancellationRequested(); // genuine shutdown -> let ExecuteAsync end the loop
            status = ProbeStatus.Error;
            SafeLog(LogLevel.Warning, null, "Ping to {Host} exceeded watchdog ({Ms}ms); abandoning the probe and continuing",
                s.Host, (int)hardTimeout.TotalMilliseconds);
            // Don't block the loop on the stuck probe; dispose it once (if) it ever settles.
            _ = pingTask.ContinueWith(t => { _ = t.Exception; ping.Dispose(); }, TaskScheduler.Default);
        }
        else
        {
            try
            {
                var reply = await pingTask;
                replyStatus = reply.Status;
                status = reply.Status switch
                {
                    IPStatus.Success => ProbeStatus.Success,
                    IPStatus.TimedOut => ProbeStatus.TimedOut,
                    _ => ProbeStatus.Unreachable,
                };
                if (status == ProbeStatus.Success)
                    rtt = reply.RoundtripTime;
            }
            catch (Exception ex)
            {
                // A single bad probe (bad host, transient error) must never tear down the loop.
                status = ProbeStatus.Error;
                SafeLog(LogLevel.Warning, ex, "Ping to {Host} failed", s.Host);
            }
            finally
            {
                ping.Dispose();
            }
        }

        var sample = new PingSample(ts, rtt, status);
        try
        {
            await _store.InsertAsync(sample, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SafeLog(LogLevel.Error, ex, "Failed to persist sample");
        }

        // Push to live subscribers even if the DB write failed — the graph should still update.
        _hub.Publish(sample);

        // Per-probe outcomes (success AND loss/timeout) are Debug: they're operational data
        // shown in the graph, not Event Log material. Keeping them below the EventLog threshold
        // also avoids hammering the Windows Event Log once per second during an outage.
        SafeLog(LogLevel.Debug, null, "{Host} {Status} rtt={Rtt}ms ({Reply})", s.Host, status, rtt, replyStatus);
    }
}
