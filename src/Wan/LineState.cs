namespace Gnip;

/// <summary>
/// Current active-WAN-line state: written by <see cref="LineMonitor"/>, read by the API.
/// Thread-safe. <see cref="Snapshot.LookupOk"/> reflects whether the most recent egress-IP
/// lookup succeeded; on a transient failure the last known line is retained.
/// </summary>
public sealed class LineState
{
    private readonly object _lock = new();
    private bool _configured;
    private string? _name;
    private string? _ip;
    private long _sinceMs;
    private bool _lookupOk;

    public void SetConfigured(bool configured)
    {
        lock (_lock) { _configured = configured; }
    }

    public void Update(string? name, string? ip, long sinceMs, bool lookupOk)
    {
        lock (_lock)
        {
            _name = name;
            _ip = ip;
            _sinceMs = sinceMs;
            _lookupOk = lookupOk;
        }
    }

    public Snapshot Current
    {
        get { lock (_lock) { return new Snapshot(_configured, _name, _ip, _sinceMs, _lookupOk); } }
    }

    public record Snapshot(bool Configured, string? Name, string? Ip, long SinceMs, bool LookupOk);
}
