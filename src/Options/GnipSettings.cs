using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Gnip;

/// <summary>A partial settings update (any null field is left unchanged). Also the on-disk shape.</summary>
public sealed record ConfigUpdate(
    string? Host,
    int? IntervalSeconds,
    int? TimeoutMs,
    int? LiveWindowSeconds,
    int? HighLatencyMs,
    int? RetentionHours);

/// <summary>
/// The runtime source of truth for mutable settings. Seeded from <see cref="GnipOptions"/>
/// (appsettings.json), overlaid with a persisted <c>gnip.settings.json</c> if present, and
/// updated at runtime via the config API. Changes are persisted and broadcast via
/// <see cref="Changed"/> so the collector can react immediately.
/// </summary>
public sealed class GnipSettings
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly object _lock = new();
    private readonly string _file;
    private readonly ILogger<GnipSettings> _log;
    private Snapshot _current;

    /// <summary>Raised after a successful runtime update.</summary>
    public event Action? Changed;

    public GnipSettings(IOptions<GnipOptions> opts, IHostEnvironment env, ILogger<GnipSettings> log)
    {
        _log = log;
        var o = opts.Value;
        var dbPath = DataPaths.Resolve(env, o.DbPath);
        var dir = Path.GetDirectoryName(dbPath);
        _file = Path.Combine(string.IsNullOrEmpty(dir) ? env.ContentRootPath : dir, "gnip.settings.json");
        _current = new Snapshot(o.Host, o.IntervalSeconds, o.TimeoutMs, o.LiveWindowSeconds, o.HighLatencyMs, o.RetentionHours);
        LoadOverrides();
    }

    public Snapshot Current
    {
        get { lock (_lock) return _current; }
    }

    /// <summary>Apply a partial update (validated), persist it, and notify subscribers. Throws <see cref="ArgumentException"/> on invalid values.</summary>
    public Snapshot Update(ConfigUpdate u)
    {
        Snapshot updated;
        lock (_lock)
        {
            var n = Apply(_current, u);
            Validate(n);
            _current = n;
            Save(n);
            updated = n;
        }
        Changed?.Invoke();
        _log.LogInformation("Settings updated: host={Host} interval={Interval}s timeout={Timeout}ms window={Window}s threshold={Threshold}ms retention={Retention}h",
            updated.Host, updated.IntervalSeconds, updated.TimeoutMs, updated.LiveWindowSeconds, updated.HighLatencyMs, updated.RetentionHours);
        return updated;
    }

    private static Snapshot Apply(Snapshot c, ConfigUpdate u) => c with
    {
        Host = u.Host ?? c.Host,
        IntervalSeconds = u.IntervalSeconds ?? c.IntervalSeconds,
        TimeoutMs = u.TimeoutMs ?? c.TimeoutMs,
        LiveWindowSeconds = u.LiveWindowSeconds ?? c.LiveWindowSeconds,
        HighLatencyMs = u.HighLatencyMs ?? c.HighLatencyMs,
        RetentionHours = u.RetentionHours ?? c.RetentionHours,
    };

    public static void Validate(Snapshot s)
    {
        if (string.IsNullOrWhiteSpace(s.Host)) throw new ArgumentException("Host must be a non-empty host name or IP address.");
        if (s.IntervalSeconds < 1) throw new ArgumentException("IntervalSeconds must be >= 1.");
        if (s.TimeoutMs < 1) throw new ArgumentException("TimeoutMs must be >= 1.");
        if (s.LiveWindowSeconds < 5) throw new ArgumentException("LiveWindowSeconds must be >= 5.");
        if (s.HighLatencyMs < 1) throw new ArgumentException("HighLatencyMs must be >= 1.");
        if (s.RetentionHours < 1) throw new ArgumentException("RetentionHours must be >= 1.");
    }

    private void LoadOverrides()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var u = JsonSerializer.Deserialize<ConfigUpdate>(File.ReadAllText(_file), Json);
            if (u is null) return;
            var n = Apply(_current, u);
            Validate(n);
            _current = n;
            _log.LogInformation("Loaded settings overrides from {File}", _file);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ignoring invalid settings file {File}; using defaults", _file);
        }
    }

    private void Save(Snapshot s)
    {
        try
        {
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json));
            File.Move(tmp, _file, overwrite: true); // atomic-ish replace
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to persist settings to {File}", _file);
        }
    }

    public sealed record Snapshot(
        string Host,
        int IntervalSeconds,
        int TimeoutMs,
        int LiveWindowSeconds,
        int HighLatencyMs,
        int RetentionHours);
}
