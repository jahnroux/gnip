using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Gnip;

/// <summary>
/// SQLite-backed sample store. There is exactly one writer (the collector loop),
/// so writes go through a single long-lived connection guarded by a semaphore.
/// Reads use short-lived pooled connections; WAL mode lets them run without
/// blocking the writer.
/// </summary>
public sealed class PingStore : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly string _connStr;
    private readonly ILogger<PingStore> _log;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private SqliteConnection? _writeConn;
    private bool _initialized;

    public PingStore(IOptions<GnipOptions> opts, ILogger<PingStore> log, IHostEnvironment env)
    {
        _log = log;
        _dbPath = DataPaths.Resolve(env, opts.Value.DbPath);
        _connStr = new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();
    }

    /// <summary>Open the write connection, enable WAL, and create the schema. Idempotent and thread-safe.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        await _writeLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            // SQLite creates the database file but not its parent directories.
            var dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // Open into a local and only publish the field after init fully succeeds, so a
            // failure part-way through never strands an open connection in the field.
            var conn = new SqliteConnection(_connStr);
            try
            {
                await conn.OpenAsync(ct);
                await conn.ExecuteAsync(
                    """
                    PRAGMA journal_mode=WAL;
                    PRAGMA synchronous=NORMAL;
                    PRAGMA busy_timeout=5000;

                    CREATE TABLE IF NOT EXISTS samples (
                        id        INTEGER PRIMARY KEY,
                        target_id INTEGER NOT NULL DEFAULT 1,
                        ts        INTEGER NOT NULL,
                        rtt_ms    REAL,
                        status    INTEGER NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS ix_samples_ts ON samples(ts);
                    """);
            }
            catch
            {
                await conn.DisposeAsync();
                throw;
            }

            _writeConn = conn;
            _initialized = true;
            _log.LogInformation("gnip store ready at {Db}", _dbPath);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Persist a single sample. Serialized against the single writer connection.</summary>
    public async Task InsertAsync(PingSample s, CancellationToken ct = default)
    {
        if (_writeConn is null)
            throw new InvalidOperationException("PingStore.InitializeAsync must be called before InsertAsync.");

        await _writeLock.WaitAsync(ct);
        try
        {
            await _writeConn.ExecuteAsync(
                "INSERT INTO samples (ts, rtt_ms, status) VALUES (@Ts, @RttMs, @Status);",
                new { s.Ts, s.RttMs, Status = (int)s.Status });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Most recent samples, newest first. Debug/scaffolding endpoint for M0.</summary>
    public async Task<IReadOnlyList<PingSample>> GetRecentAsync(int limit, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        // Read into a plain row DTO (Dapper maps these via properties reliably), then
        // project to the domain record. Dapper's constructor-based path does not
        // handle the enum/nullable positional-record parameters cleanly.
        var rows = await conn.QueryAsync<SampleRow>(
            "SELECT ts AS Ts, rtt_ms AS RttMs, status AS Status FROM samples ORDER BY ts DESC LIMIT @limit;",
            new { limit });
        return rows.Select(r => new PingSample(r.Ts, r.RttMs, (ProbeStatus)r.Status)).ToList();
    }

    /// <summary>Samples at or after <paramref name="sinceMs"/>, oldest first (initial live-window fill).</summary>
    public async Task<IReadOnlyList<PingSample>> GetSinceAsync(long sinceMs, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<SampleRow>(
            "SELECT ts AS Ts, rtt_ms AS RttMs, status AS Status FROM samples WHERE ts >= @sinceMs ORDER BY ts ASC;",
            new { sinceMs });
        return rows.Select(r => new PingSample(r.Ts, r.RttMs, (ProbeStatus)r.Status)).ToList();
    }

    /// <summary>
    /// Downsample a time range into at most <paramref name="buckets"/> buckets, each carrying
    /// min/avg/max RTT and a loss fraction. Bucket width is never finer than
    /// <paramref name="minBucketMs"/> (the sample interval), so the payload size is bounded by
    /// the requested bucket count regardless of how wide the range is.
    /// </summary>
    public async Task<SeriesResult> GetSeriesAsync(long fromMs, long toMs, int buckets, long minBucketMs, CancellationToken ct = default)
    {
        buckets = Math.Clamp(buckets, 1, 5000);
        var bucketMs = Math.Max(Math.Max(1, minBucketMs), (long)Math.Ceiling((toMs - fromMs) / (double)buckets));
        if (toMs <= fromMs)
            return new SeriesResult(bucketMs, [], [], [], [], []);

        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        var rows = (await conn.QueryAsync<SeriesRow>(
            """
            SELECT (ts / @bucketMs) * @bucketMs AS T,
                   AVG(rtt_ms) AS Avg,
                   MIN(rtt_ms) AS Min,
                   MAX(rtt_ms) AS Max,
                   COUNT(*)    AS Total,
                   SUM(CASE WHEN rtt_ms IS NULL THEN 1 ELSE 0 END) AS Lost
            FROM samples
            WHERE ts >= @from AND ts < @to
            GROUP BY ts / @bucketMs
            ORDER BY T;
            """,
            new { bucketMs, from = fromMs, to = toMs })).AsList();

        int n = rows.Count;
        var t = new long[n];
        var avg = new double?[n];
        var min = new double?[n];
        var max = new double?[n];
        var loss = new double[n];
        for (int i = 0; i < n; i++)
        {
            var r = rows[i];
            t[i] = r.T;
            avg[i] = r.Avg;
            min[i] = r.Min;
            max[i] = r.Max;
            loss[i] = r.Total > 0 ? (double)r.Lost / r.Total : 0;
        }
        return new SeriesResult(bucketMs, t, avg, min, max, loss);
    }

    /// <summary>Delete samples older than <paramref name="cutoffMs"/>; truncates the WAL afterward to cap file size. Returns rows removed.</summary>
    public async Task<int> DeleteOlderThanAsync(long cutoffMs, CancellationToken ct = default)
    {
        if (_writeConn is null)
            throw new InvalidOperationException("PingStore.InitializeAsync must be called before DeleteOlderThanAsync.");

        await _writeLock.WaitAsync(ct);
        try
        {
            var removed = await _writeConn.ExecuteAsync("DELETE FROM samples WHERE ts < @cutoffMs;", new { cutoffMs });
            if (removed > 0)
                await _writeConn.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE);");
            return removed;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Flat row shape for Dapper materialization.</summary>
    private sealed class SampleRow
    {
        public long Ts { get; set; }
        public double? RttMs { get; set; }
        public int Status { get; set; }
    }

    /// <summary>Flat row shape for a downsampled bucket.</summary>
    private sealed class SeriesRow
    {
        public long T { get; set; }
        public double? Avg { get; set; }
        public double? Min { get; set; }
        public double? Max { get; set; }
        public long Total { get; set; }
        public long Lost { get; set; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_writeConn is not null)
            await _writeConn.DisposeAsync();
        _writeLock.Dispose();
    }
}
