using System.Text.Json;
using Gnip;
using Microsoft.Extensions.Options;

// Anchor the content root to the executable's own directory rather than the current working
// directory, so wwwroot / appsettings.json / a relative DbPath resolve next to the exe no matter
// where it's launched from (as a service, xcopy-deployed, or run from another folder).
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Integrate with the host's service manager when launched by it; both are no-ops otherwise.
builder.Host.UseWindowsService(); // Windows SCM
builder.Host.UseSystemd();        // Linux systemd (Type=notify + journald logging)

builder.Services.AddOptions<GnipOptions>()
    .Bind(builder.Configuration.GetSection(GnipOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.Host), "Gnip:Host must be a non-empty host name or IP address.")
    .Validate(o => o.IntervalSeconds >= 1, "Gnip:IntervalSeconds must be >= 1.")
    .Validate(o => o.TimeoutMs >= 1, "Gnip:TimeoutMs must be >= 1.")
    .Validate(o => o.RetentionHours >= 1, "Gnip:RetentionHours must be >= 1.")
    .Validate(o => o.LiveWindowSeconds >= 5, "Gnip:LiveWindowSeconds must be >= 5.")
    .Validate(o => o.HighLatencyMs >= 1, "Gnip:HighLatencyMs must be >= 1.")
    .ValidateOnStart();

builder.Services.AddSingleton<PingStore>();
builder.Services.AddSingleton<GnipSettings>();
builder.Services.AddSingleton<SampleHub>();
builder.Services.AddSingleton<LineState>();
builder.Services.AddHostedService<PingCollector>();
builder.Services.AddHostedService<RetentionService>();
builder.Services.AddHostedService<LineMonitor>();

var app = builder.Build();

// Validate config (incl. persisted overrides) and ensure the DB exists before collectors start.
try
{
    _ = app.Services.GetRequiredService<GnipSettings>().Current;
    await app.Services.GetRequiredService<PingStore>().InitializeAsync();
}
catch (OptionsValidationException ex)
{
    foreach (var failure in ex.Failures)
        app.Logger.LogCritical("Invalid configuration: {Failure}", failure);
    return 1;
}

app.UseDefaultFiles();
app.UseStaticFiles();

var jsonOpts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
static object ConfigDto(GnipSettings.Snapshot s) => new
{
    host = s.Host,
    intervalSeconds = s.IntervalSeconds,
    timeoutMs = s.TimeoutMs,
    liveWindowSeconds = s.LiveWindowSeconds,
    highLatencyMs = s.HighLatencyMs,
    retentionHours = s.RetentionHours,
};

// Current settings for the frontend.
app.MapGet("/api/config", (GnipSettings settings) => Results.Json(ConfigDto(settings.Current)));

// Update settings at runtime (partial; persisted; applied live). 400 on invalid values.
app.MapPut("/api/config", (GnipSettings settings, ConfigUpdate update) =>
{
    try { return Results.Json(ConfigDto(settings.Update(update))); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// Samples within the last N seconds, oldest first — initial fill for the live chart.
app.MapGet("/api/window", async (PingStore store, GnipSettings settings, int? seconds, HttpContext ctx) =>
{
    var secs = Math.Clamp(seconds ?? settings.Current.LiveWindowSeconds, 1, 86_400);
    var sinceMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)secs * 1000;
    return Results.Json(await store.GetSinceAsync(sinceMs, ctx.RequestAborted));
});

// Downsampled history for an arbitrary [from, to] range.
app.MapGet("/api/series", async (PingStore store, GnipSettings settings, long? from, long? to, int? buckets, HttpContext ctx) =>
{
    var s = settings.Current;
    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var toMs = to ?? nowMs;
    var fromMs = from ?? (toMs - (long)s.LiveWindowSeconds * 1000);
    var minBucketMs = (long)s.IntervalSeconds * 1000;
    var n = Math.Clamp(buckets ?? 1000, 1, 5000);
    return Results.Json(await store.GetSeriesAsync(fromMs, toMs, n, minBucketMs, ctx.RequestAborted));
});

// Live stream of new samples via Server-Sent Events.
app.MapGet("/api/live", async (HttpContext ctx, SampleHub hub) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no"; // don't let a proxy buffer the stream

    var ct = ctx.RequestAborted;
    using var sub = hub.Subscribe();
    try
    {
        await ctx.Response.WriteAsync(": connected\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);

        await foreach (var sample in sub.Reader.ReadAllAsync(ct))
        {
            await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(sample, jsonOpts)}\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException)
    {
        // Client disconnected — normal.
    }
});

// Debug endpoint: most recent samples, newest first.
app.MapGet("/api/recent", async (PingStore store, int? limit, HttpContext ctx) =>
    Results.Json(await store.GetRecentAsync(Math.Clamp(limit ?? 50, 1, 1000), ctx.RequestAborted)));

// Currently-active WAN line (egress-IP detection). `configured` is false when no lines are set.
app.MapGet("/api/line", (LineState lineState, IOptions<GnipOptions> opts) =>
{
    var s = lineState.Current;
    var lines = opts.Value.Lines;
    return Results.Json(new
    {
        configured = s.Configured,
        current = s.Name,
        ip = s.Ip,
        sinceMs = s.SinceMs,
        lookupOk = s.LookupOk,
        primary = lines.Count > 0 ? lines[0].Name : null,
        lines = lines.Select(l => new { name = l.Name, ip = l.Ip }),
    });
});

// WAN-line transitions within a range, for drawing failover markers on the chart.
app.MapGet("/api/line/events", async (PingStore store, GnipSettings settings, long? from, long? to, HttpContext ctx) =>
{
    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var toMs = to ?? nowMs;
    var fromMs = from ?? (toMs - (long)settings.Current.LiveWindowSeconds * 1000);
    return Results.Json(await store.GetLineEventsAsync(fromMs, toMs, ctx.RequestAborted));
});

try
{
    app.Run();
}
catch (OperationCanceledException)
{
    // A Windows Service stop can surface as an OperationCanceledException out of the host's
    // shutdown path (WindowsServiceLifetime.StopAsync). That's a normal stop, not a crash —
    // swallow it so the process exits 0 instead of tripping SCM's crash-recovery restart.
    app.Logger.LogInformation("Host stopped.");
}
return 0;
