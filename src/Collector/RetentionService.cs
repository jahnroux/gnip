namespace Gnip;

/// <summary>Periodically prunes samples older than the configured retention window so the database stays bounded.</summary>
public sealed class RetentionService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    private readonly PingStore _store;
    private readonly GnipSettings _settings;
    private readonly ILogger<RetentionService> _log;

    public RetentionService(PingStore store, GnipSettings settings, ILogger<RetentionService> log)
    {
        _store = store;
        _settings = settings;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let startup settle, then sweep on a fixed cadence. A sweep never throws (see
        // SweepAsync) and a logging failure can't escape, so the only thing that ends this
        // loop is a genuine shutdown.
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            await SweepAsync(stoppingToken);
            try { await Task.Delay(SweepInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            var hours = _settings.Current.RetentionHours;
            var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)hours * 3600 * 1000;
            var removed = await _store.DeleteOlderThanAsync(cutoff, ct);
            if (removed > 0)
                SafeLog(LogLevel.Information, null, "Retention: pruned {Count} samples older than {Hours}h", removed, hours);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown — the loop's delay will observe it and exit.
        }
        catch (Exception ex)
        {
            SafeLog(LogLevel.Error, ex, "Retention sweep failed");
        }
    }

    /// <summary>Log without letting a misbehaving log provider escape and fault the service.</summary>
    private void SafeLog(LogLevel level, Exception? ex, string message, params object?[] args)
    {
        try { _log.Log(level, ex, message, args); }
        catch { /* logging is best-effort */ }
    }
}
