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
        try
        {
            // Let startup settle, then sweep on a fixed cadence.
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await SweepAsync(stoppingToken);
                await Task.Delay(SweepInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var hours = _settings.Current.RetentionHours;
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)hours * 3600 * 1000;
        try
        {
            var removed = await _store.DeleteOlderThanAsync(cutoff, ct);
            if (removed > 0)
                _log.LogInformation("Retention: pruned {Count} samples older than {Hours}h", removed, hours);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Retention sweep failed");
        }
    }
}
