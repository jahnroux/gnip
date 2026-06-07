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
the target), `GnipTray.exe` (the system-tray controller), plus `wwwroot\` and `appsettings.json`.
Copy the whole folder to any Windows x64 machine and run `gnip.exe`. Data files (`gnip.db`,
`gnip.settings.json`) are created next to it. For a Linux build
(`.\publish.ps1 -Runtime linux-x64`) and full deployment steps, see [DEPLOY.md](DEPLOY.md).

## Run as a Windows Service

Easiest — the installer self-elevates, creates the data dir, registers the service with
crash-recovery, and starts it:

```powershell
.\publish.ps1                  # build gnip.exe + GnipTray.exe into bin\publish\win-x64
.\install-service.ps1          # install + start the service (localhost:5099)
.\install-service.ps1 -Url http://0.0.0.0:5099   # ...or expose on the network
.\install-service.ps1 -Uninstall                 # stop + remove (data is kept)
```

Data lives in `C:\ProgramData\gnip\` (writable; the install folder may be read-only). ICMP needs no
special privilege on Windows, so the default LocalSystem account is fine. The app supports the
service lifetime out of the box (`UseWindowsService()`), so logs go to the Windows Event Log.

### System-tray controller (optional)

`GnipTray.exe` (published alongside `gnip.exe`) puts a status dot in the tray — green = running,
grey = stopped, amber = changing, red = not installed. Right-click for **Open dashboard**,
**Start / Stop / Restart** (these prompt for elevation, since controlling a service needs admin),
**Open data folder**, **View logs** (Event Viewer), and a **Start tray at login** toggle.
Double-click the icon to open the dashboard.

### MSI installer

Prefer a double-click installer? Build `gnip.msi` (needs the free WiX 5 toolset):

```powershell
dotnet tool install --global wix --version 5.0.2
wix extension add -g WixToolset.Util.wixext/5.0.2
.\build-msi.ps1            # publishes, then builds bin\gnip.msi
```

Double-clicking `gnip.msi` runs a setup wizard (welcome → license → choose folder → confirm →
finish, with a **Launch gnip Tray now** option). It installs the service + tray to
`C:\Program Files\gnip`, registers the auto-start `gnip` service with crash-recovery, creates
`C:\ProgramData\gnip` for data, and adds Start Menu shortcuts (gnip Tray, gnip Dashboard).
Uninstall from Add/Remove Programs — your data in ProgramData is kept.

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
