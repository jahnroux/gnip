using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Gnip;

/// <summary>
/// One WAN line in the runtime settings: a display name and the public IP/CIDR its traffic
/// egresses from. Immutable, unlike the <see cref="LineConfig"/> shape the config binder needs.
/// </summary>
public sealed record LineDef(string Name, string Ip);

/// <summary>A partial settings update (any null field is left unchanged). Also the on-disk shape.</summary>
public sealed record ConfigUpdate(
    string? Host,
    int? IntervalSeconds,
    int? TimeoutMs,
    int? LiveWindowSeconds,
    int? HighLatencyMs,
    int? RetentionHours,
    int? LineCheckSeconds,
    IReadOnlyList<LineDef>? Lines);

/// <summary>
/// The runtime source of truth for mutable settings. Seeded from <see cref="GnipOptions"/>
/// (appsettings.json), overlaid with a persisted <c>gnip.settings.json</c> if present, and
/// updated at runtime via the config API. Changes are persisted and broadcast via
/// <see cref="Changed"/> so the collector and the line monitor can react immediately.
/// </summary>
public sealed class GnipSettings
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Upper bound on configured WAN lines — a sanity cap on API input, not a real limit.</summary>
    public const int MaxLines = 16;

    /// <summary>
    /// Reserved: <see cref="LineMonitor"/> reports this name when the egress IP resolved but
    /// matched no configured line, so a real line may not claim it.
    /// </summary>
    public const string UnknownLineName = "Unknown";

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
        _current = new Snapshot(o.Host, o.IntervalSeconds, o.TimeoutMs, o.LiveWindowSeconds, o.HighLatencyMs,
            o.RetentionHours, o.LineCheckSeconds, SeedLines(o.Lines));
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
        _log.LogInformation("Settings updated: host={Host} interval={Interval}s timeout={Timeout}ms window={Window}s threshold={Threshold}ms retention={Retention}h lines={Lines} lineCheck={LineCheck}s",
            updated.Host, updated.IntervalSeconds, updated.TimeoutMs, updated.LiveWindowSeconds, updated.HighLatencyMs,
            updated.RetentionHours, updated.Lines.Count, updated.LineCheckSeconds);
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
        LineCheckSeconds = u.LineCheckSeconds ?? c.LineCheckSeconds,
        // null = leave alone; an empty array is a deliberate "no lines", which turns detection off.
        Lines = u.Lines is null ? c.Lines : Normalize(u.Lines),
    };

    /// <summary>Trim whitespace and drop the nulls JSON can smuggle into the array.</summary>
    private static IReadOnlyList<LineDef> Normalize(IReadOnlyList<LineDef> lines) =>
        lines.Where(l => l is not null)
             .Select(l => new LineDef((l.Name ?? "").Trim(), (l.Ip ?? "").Trim()))
             .ToList();

    public static void Validate(Snapshot s)
    {
        if (string.IsNullOrWhiteSpace(s.Host)) throw new ArgumentException("Host must be a non-empty host name or IP address.");
        if (s.IntervalSeconds < 1) throw new ArgumentException("IntervalSeconds must be >= 1.");
        if (s.TimeoutMs < 1) throw new ArgumentException("TimeoutMs must be >= 1.");
        if (s.LiveWindowSeconds < 5) throw new ArgumentException("LiveWindowSeconds must be >= 5.");
        if (s.HighLatencyMs < 1) throw new ArgumentException("HighLatencyMs must be >= 1.");
        if (s.RetentionHours < 1) throw new ArgumentException("RetentionHours must be >= 1.");
        if (s.LineCheckSeconds < 5) throw new ArgumentException("LineCheckSeconds must be >= 5.");
        ValidateLines(s.Lines);
    }

    private static void ValidateLines(IReadOnlyList<LineDef> lines)
    {
        if (lines.Count > MaxLines) throw new ArgumentException($"At most {MaxLines} WAN lines are supported.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines)
        {
            if (string.IsNullOrWhiteSpace(l.Name))
                throw new ArgumentException("Every WAN line needs a name.");
            if (string.Equals(l.Name, UnknownLineName, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"\"{UnknownLineName}\" is reserved: it is what gnip reports when the egress IP matches no configured line. Pick another name.");
            if (!Cidr.TryParse(l.Ip, out _))
                throw new ArgumentException($"WAN line \"{l.Name}\": \"{l.Ip}\" is not a valid IPv4 address or CIDR (e.g. 102.23.95.1 or 41.164.173.112/30).");
            if (!names.Add(l.Name))
                throw new ArgumentException($"Duplicate WAN line name \"{l.Name}\".");
        }
    }

    /// <summary>
    /// Normalize the lines seeded from appsettings.json, dropping any that are malformed rather
    /// than throwing. One stale entry in a file the user may not be able to edit must not block
    /// startup, nor make every later settings update fail validation on a pre-existing problem.
    /// </summary>
    private IReadOnlyList<LineDef> SeedLines(IEnumerable<LineConfig> lines)
    {
        var seeded = new List<LineDef>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines)
        {
            var name = (l.Name ?? "").Trim();
            var ip = (l.Ip ?? "").Trim();
            if (name.Length == 0 || !Cidr.TryParse(ip, out _))
            {
                _log.LogWarning("Ignoring invalid WAN line in appsettings.json (name={Name} ip={Ip})", l.Name, l.Ip);
                continue;
            }
            if (string.Equals(name, UnknownLineName, StringComparison.OrdinalIgnoreCase) || !names.Add(name))
            {
                _log.LogWarning("Ignoring WAN line with a reserved or duplicate name in appsettings.json (name={Name})", l.Name);
                continue;
            }
            if (seeded.Count == MaxLines)
            {
                _log.LogWarning("Ignoring WAN lines beyond the first {Max} in appsettings.json", MaxLines);
                break;
            }
            seeded.Add(new LineDef(name, ip));
        }
        return seeded;
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
        int RetentionHours,
        int LineCheckSeconds,
        IReadOnlyList<LineDef> Lines);
}
