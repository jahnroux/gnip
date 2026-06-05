# gnip

A lightweight PingPlotter-style latency/loss monitor: pings a host on an interval, stores the
results in SQLite, and serves a web UI with a **live** auto-scrolling graph and a **seekable
history**. C# / .NET 8, single self-contained backend, no external services.

> "gnip" = ping spelled backwards. Design notes: [PLAN.md](PLAN.md) · Deployment (local + Linux): [DEPLOY.md](DEPLOY.md).

## Run (development)

```powershell
dotnet run -c Release --urls http://localhost:5099
```

Then open **http://localhost:5099**.

## Configure

Defaults live in [appsettings.json](appsettings.json) under the `Gnip` section:

| Key | Default | Meaning |
|---|---|---|
| `Host` | `8.8.8.8` | Host name or IP to ping |
| `IntervalSeconds` | `1` | Seconds between pings |
| `TimeoutMs` | `1000` | Per-ping timeout |
| `DbPath` | `gnip.db` | SQLite file (relative to the content root, or an absolute path) |
| `RetentionHours` | `48` | Samples older than this are pruned every 5 minutes |
| `LiveWindowSeconds` | `600` | Width of the live rolling window |
| `HighLatencyMs` | `100` | Threshold reference line on the graph |

You can change everything except `DbPath` **at runtime** from the **⚙ Settings** panel in the UI
(or `PUT /api/config`). Runtime changes are persisted to `gnip.settings.json` (next to the DB) and
applied immediately — the collector re-points without a restart.

**Precedence:** `gnip.settings.json` (your runtime changes) overrides `appsettings.json` *and* any
`--Gnip:*` command-line values for the fields it contains. Delete that file to fall back to them.

Override any setting on the command line too, e.g.:

```powershell
gnip.exe --urls http://0.0.0.0:5099 --Gnip:Host=1.1.1.1 --Gnip:LiveWindowSeconds=300
```

## Build a portable release

```powershell
.\publish.ps1
```

Produces `bin\publish\win-x64\` containing `gnip.exe` (self-contained — no .NET install needed on
the target), plus `wwwroot\` and `appsettings.json`. Copy the whole folder to any Windows x64
machine and run `gnip.exe`. Data files (`gnip.db`, `gnip.settings.json`) are created next to it.
For a Linux build (`.\publish.ps1 -Runtime linux-x64`) and full deployment steps, see
[DEPLOY.md](DEPLOY.md).

## Run as a Windows Service

The app supports the service lifetime out of the box (running under the SCM sets the working
directory to the exe's folder automatically). From an **elevated** prompt:

```powershell
sc.exe create gnip binPath= "\"C:\gnip\gnip.exe\" --urls http://0.0.0.0:5099 --Gnip:DbPath=C:\ProgramData\gnip\gnip.db" start= auto
sc.exe start gnip
```

Use an **absolute `DbPath` in a writable location** (e.g. `C:\ProgramData\gnip\`) since the install
folder may be read-only. Remove with `sc.exe delete gnip`.

## Exposing it

To reach gnip from other machines, bind to all interfaces (`--urls http://0.0.0.0:5099`) and allow
the port through the firewall. **There is no authentication yet** — only expose it on a trusted
network, or put it behind a reverse proxy / VPN.

## HTTP API

| Endpoint | Purpose |
|---|---|
| `GET /api/config` · `PUT /api/config` | Read / update settings (partial JSON body; persisted) |
| `GET /api/window?seconds=` | Recent samples (initial live fill) |
| `GET /api/series?from=&to=&buckets=` | Downsampled min/avg/max/loss buckets for a range |
| `GET /api/live` | Server-Sent Events stream of new samples |
| `GET /api/recent?limit=` | Most recent raw samples (debug) |
