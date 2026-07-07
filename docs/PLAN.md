> *"We're building something here, detective. We're building it from scratch."* — Lester Freamon, **The Wire**

# gnip — design & build plan

*A lightweight PingPlotter-style latency/loss monitor. "gnip" = ping spelled backwards.*

## What it is

A small backend service that pings one target on a fixed interval, stores the
results, and serves a web UI showing a live latency/loss graph plus a seekable
history. Built to run on any Windows machine and be viewed from a browser.

## Locked decisions

| Decision | Choice | Why |
|---|---|---|
| Language/runtime | **C# / .NET 8 (LTS)** | Built-in *unprivileged* ICMP on Windows via `System.Net.NetworkInformation.Ping`; ASP.NET Core minimal API + `BackgroundService` is a near-perfect fit; publishes to one self-contained `.exe`. (.NET 8 is what's installed on this machine; .NET 10 would work identically.) |
| Deployment | **Backend service + web UI** | Host anywhere, browse from anywhere. |
| Targets | **Single target to start** | Keep scope tight. Schema leaves a cheap seam for multi-target later. |
| Probe | **ICMP echo only** | Matches the requirement; no traceroute, no TCP/HTTP probes (yet). |

## Stack

- **.NET 8** minimal API (`dotnet new web`). Packages: `Microsoft.Data.Sqlite` + `Dapper`.
- **ICMP**: `Ping.SendPingAsync(host, timeoutMs)` — no admin required on Windows.
- **Collector**: a `BackgroundService` using `PeriodicTimer` for drift-free fixed-interval pings.
- **Storage**: SQLite via `Microsoft.Data.Sqlite` + `Dapper` for terse queries.
- **Live push**: Server-Sent Events (`text/event-stream`); fan-out to connected clients via `System.Threading.Channels`.
- **Frontend**: static `wwwroot` — `index.html` + `app.js` + vendored **uPlot** (no npm/build step). uPlot is tiny and built for high-volume time-series.
- **Packaging**: `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`.

## Data model

```sql
CREATE TABLE samples (
  id        INTEGER PRIMARY KEY,
  target_id INTEGER NOT NULL DEFAULT 1,  -- cheap seam for multi-target later
  ts        INTEGER NOT NULL,            -- unix epoch ms (UTC)
  rtt_ms    REAL,                        -- NULL = loss / timeout
  status    INTEGER NOT NULL             -- 0=success 1=timed-out 2=unreachable ...
);
CREATE INDEX ix_samples_ts ON samples(ts);
```

**Why this scales fine:** at 1 sample/sec, ~16 bytes each → 1h ≈ 3,600 rows,
1 day ≈ 86k, 1 week ≈ 600k. SQLite handles millions trivially. The only lever
needed for very long windows is retention/rollup (see M3 / M4).

## API

| Endpoint | Purpose |
|---|---|
| `GET /api/series?from=&to=&buckets=1000` | History, **server-side downsampled** to N buckets (min/avg/max rtt + loss% per bucket). Payload size is fixed regardless of window width. |
| `GET /api/live` (SSE) | Pushes each new sample as collected `{ts, rtt, status}`. |
| `GET /api/status` | Current target, uptime, last sample, rolling loss%. |
| `GET /api/config` / `PUT /api/config` | `{ host, intervalSeconds, timeoutMs, retentionHours }`. |
| `GET /` | Serves the web UI. |

**Downsampling query** (the core trick — the chart never needs more points than it has pixels):

```sql
SELECT MIN(ts) AS t,
       AVG(rtt_ms) AS avg_rtt, MIN(rtt_ms) AS min_rtt, MAX(rtt_ms) AS max_rtt,
       COUNT(*) AS total,
       SUM(CASE WHEN rtt_ms IS NULL THEN 1 ELSE 0 END) AS lost
FROM samples
WHERE ts BETWEEN @from AND @to
GROUP BY ts / @bucketMs
ORDER BY t;
```
`bucketMs = max(intervalMs, (to-from)/buckets)`.

## Frontend behavior

- **Live mode (default):** subscribe to SSE, append to a rolling buffer sized to the
  live window (default 10 min), uPlot auto-scrolls.
- **History mode:** range buttons (5m / 15m / 1h / 2h / 6h / 24h) + drag-to-pan + a
  date-time jump. Fetches `/api/series`; renders avg line with a shaded min–max band.
- **Loss + latency cues:** loss shown as red markers / shaded columns on a thin track
  under the latency chart; a configurable horizontal "high latency" threshold line.

## Defaults (configurable)

- Ping interval: **1 s** · timeout: **1000 ms** · live window: **10 min** · retention: **48 h**.

## Milestones (primary requirement lands first)

- **M0 — Skeleton:** project + ping loop + SQLite writes + console output. Proves the data path end-to-end.
- **M1 — Live graph (PRIMARY req):** SSE + uPlot rolling window. Glance → see loss/latency over the last N minutes.
- **M2 — History (SECONDARY req):** `/api/series` downsampling + seek/range UI.
- **M3 — Config, retention, packaging:** settings endpoint/UI, retention cleanup timer, single-file `.exe`, run-as-Windows-Service notes (`UseWindowsService()`).
- **M4 — Later / optional:** multiple targets, rollups for long windows, TCP/HTTP probes, alert thresholds + notifications, auth.

## Open notes

- **Security:** M1–M3 have no auth. If exposing beyond localhost, bind to `127.0.0.1` behind a reverse proxy, or add basic auth, before opening a firewall port.
- **Linux portability:** the architecture is cross-platform, but on Linux `Ping` may require `CAP_NET_RAW` or fall back to the `ping` binary. Revisit only if Linux hosting becomes a real goal.
- **DB path / content root:** a relative `DbPath` resolves against the *content root*, which for `WebApplication` defaults to the current working directory — not the exe folder. Observed in M0: running `bin\Release\net8.0\gnip.exe` from the repo root created `gnip.db` at the repo root. M3 should resolve `DbPath` against a deliberate data directory (e.g. next to the exe or a per-user app-data path) so a packaged exe behaves predictably wherever it's launched.

## Status

- **M0 — done.** Project scaffolded; ping → SQLite (WAL) → `/api/recent` read-back verified end to end against 8.8.8.8 (~28ms). Adversarial code review (3 lenses → refute-verify) ran before sign-off and surfaced 5 in-scope fixes, all applied + verified:
  1. `InitializeAsync` made idempotent/thread-safe (double-checked `_writeLock`) and exception-safe (open-into-local, dispose on failure).
  2. Ping I/O is now cancellable (.NET 8 `SendPingAsync` token overload) for prompt shutdown.
  3. Per-probe catch broadened so no single bad probe can kill the collector loop.
  4. Startup config validation (`ValidateOnStart`) with clean fail-fast: bad config → `crit` log + exit code 1, no unhandled crash.
  5. DB parent directory auto-created; `IntervalSeconds*1000` overflow removed (`long`).
- **M1 — done.** Live graph delivered (the primary requirement). `SampleHub` fan-out broadcaster (bounded channels, drop-oldest) → `/api/live` SSE; `/api/window` for initial fill; `/api/config` for frontend bootstrap; static `wwwroot` (vendored uPlot 1.6.32) with a rolling auto-scroll window, dashed threshold line, red loss bands, and a live min/avg/max/loss% stats bar. Verified: all endpoints return correct data, SSE streams ~1/s, page + assets serve `200`, and the chart renders (see `m1-selftest.png`). Config validation extended to `LiveWindowSeconds`/`HighLatencyMs`.
  - **Env note:** headless Edge/Chromium can't reach `localhost`/loopback on this machine (corporate proxy policy overrides `--no-proxy-server`), so server-driven screenshots fail with `ERR_CONNECTION_REFUSED` even though the server is provably up (PowerShell/curl reach it fine). View the live UI in a normal browser at `http://localhost:5099`; `file://` render checks work.
- **M2 — done.** Seekable history delivered (the secondary requirement). `/api/series?from=&to=&buckets=` downsamples any range server-side to ≤N buckets (min/avg/max RTT + loss fraction; bucket width never finer than the sample rate), so the payload is bounded regardless of window width. Frontend: `Live` + `15m/1h/6h/24h` range buttons, ◀/▶ seek, zoom-out, and drag-to-select a range; the chart is unified (live + history share one config) showing the avg line with a min/max band and loss bands. Verified: downsampling math (fine 1s buckets vs coarse 5s buckets with real min/avg/max aggregation), payload shape, the deployed page/JS, and the history render (see `m2-history.png`).
- **M3 — done.** Now a hostable product. Runtime settings via `GnipSettings` (mutable, thread-safe, persisted to `gnip.settings.json` next to the DB): `GET/PUT /api/config` + a ⚙ Settings panel change host/interval/timeout/window/threshold/retention **live** — the collector re-points without a restart (verified: PUT applied live, invalid → 400, persisted across restart). `RetentionService` prunes samples older than `RetentionHours` every 5 min and WAL-truncates (verified: pruned 466 old rows, kept recent). `builder.Host.UseWindowsService()` for SCM hosting; `publish.ps1` produces a ~47 MB self-contained single-file `gnip.exe` (+ `wwwroot/` + `appsettings.json`) — verified it runs from its own folder, serves the UI, pings, and creates the DB beside it. `README.md` covers run/config/publish/service.
  - **Settings precedence:** persisted `gnip.settings.json` overrides appsettings/CLI for the fields it contains (delete it to revert).
  - **Deployment:** also added `UseSystemd()` for first-class Linux service hosting; `publish.ps1 -Runtime linux-x64` cross-builds a self-contained Linux binary (validated). Local-Windows + Linux-server (systemd unit, `CAP_NET_RAW`, data dir, firewall, nginx reverse proxy for the SSE stream) steps are in `DEPLOY.md`.
- **Windows ops tooling (post-M3).** `install-service.ps1` — self-elevating installer (`New-Service` → `C:\ProgramData\gnip` DbPath, crash-recovery via `sc failure`, `-Uninstall`). `GnipTray.exe` — a WinForms (`net8.0-windows`) system-tray controller: status dot + Start/Stop/Restart (elevated `--svc` helper), Open dashboard / data folder / logs, "Start tray at login" toggle. Kept as a separate project (`tray/GnipTray.csproj`, excluded from the web project via `DefaultItemExcludes`) so the service stays lean/cross-platform; `gnip.sln` ties them; `publish.ps1` builds both on win-x64. Also added `UseSystemd()` (Linux) and dropped the per-ping success log to `Debug` for headless runs. Adversarial review (16 agents) → 8 distinct fixes applied (tray restart edge-cases + broad catch; installer fire-and-forget exit code, UAC-cancel, async-`sc delete` 1072 race, `$LASTEXITCODE` checks). Build-verified + script parse-checked; GUI/service-install not runtime-tested in the build sandbox.
- **MSI installer (post-M3).** `installer/gnip.wxs` (WiX 5) + `build-msi.ps1` produce `bin\gnip.msi`: installs service + tray to `C:\Program Files\gnip`, registers the auto-start service with crash-recovery (`util:ServiceConfig`), creates `C:\ProgramData\gnip`, adds Start Menu shortcuts (Tray + Dashboard), clean uninstall (data kept). Includes a **WixUI_InstallDir wizard** (welcome → license → choose folder → confirm → progress → finish) with a "Launch gnip Tray now" finish checkbox (`WixToolset.UI.wixext` + `installer/license.rtf`). Build-clean + payload verified via admin-extract + wizard dialogs confirmed in the MSI tables; install/GUI not runtime-tested. (WiX 7+ requires a paid EULA — pinned to the free WiX 5.0.2.)
- **Next: M4 (optional)** — multiple targets, long-window rollups, alert thresholds + notifications, auth for exposed instances.
