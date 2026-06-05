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
        using var ping = new Ping();
        using var wake = new SemaphoreSlim(0, 1);
        void onChanged() { try { wake.Release(); } catch (SemaphoreFullException) { } }
        _settings.Changed += onChanged;

        var last = _settings.Current;
        _log.LogInformation("Collector started: host={Host} interval={Interval}s timeout={Timeout}ms",
            last.Host, last.IntervalSeconds, last.TimeoutMs);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var s = _settings.Current;
                if (s.Host != last.Host || s.IntervalSeconds != last.IntervalSeconds || s.TimeoutMs != last.TimeoutMs)
                {
                    _log.LogInformation("Collector reconfigured: host={Host} interval={Interval}s timeout={Timeout}ms",
                        s.Host, s.IntervalSeconds, s.TimeoutMs);
                    last = s;
                }

                var sw = Stopwatch.StartNew();
                await ProbeOnceAsync(ping, s, stoppingToken);

                // Sleep the remainder of the interval, but wake early if settings change.
                var wait = TimeSpan.FromMilliseconds(Math.Max(100L, (long)s.IntervalSeconds * 1000)) - sw.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    try { await wake.WaitAsync(wait, stoppingToken); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            _settings.Changed -= onChanged;
        }

        _log.LogInformation("Collector stopped.");
    }

    private async Task ProbeOnceAsync(Ping ping, GnipSettings.Snapshot s, CancellationToken ct)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        double? rtt = null;
        ProbeStatus status;
        IPStatus replyStatus = IPStatus.Unknown;

        try
        {
            var reply = await ping.SendPingAsync(s.Host, TimeSpan.FromMilliseconds(s.TimeoutMs), cancellationToken: ct);
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
        catch (OperationCanceledException)
        {
            throw; // Shutdown requested mid-probe; let ExecuteAsync end the loop.
        }
        catch (Exception ex)
        {
            // A single bad probe (bad host, transient error) must never tear down the loop.
            status = ProbeStatus.Error;
            _log.LogWarning(ex, "Ping to {Host} failed", s.Host);
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
            _log.LogError(ex, "Failed to persist sample");
        }

        // Push to live subscribers even if the DB write failed — the graph should still update.
        _hub.Publish(sample);

        if (status == ProbeStatus.Success)
            _log.LogInformation("{Host} rtt={Rtt}ms", s.Host, rtt);
        else
            _log.LogWarning("{Host} {Status} ({Reply})", s.Host, status, replyStatus);
    }
}
