# Deploying gnip

*Prefer it in a browser? Open the styled companion [DEPLOY.html](DEPLOY.html) (dark theme, copy buttons).*

Two paths: stand it up **locally on Windows**, and deploy it to a **Linux server** as a systemd
service. gnip is a single self-contained binary — no .NET install needed on the target.

---

## 1. Local (Windows)

### Run from source (development)

```powershell
dotnet run -c Release --urls http://localhost:5099
```

Open **http://localhost:5099**. Change the target/interval/etc. live from the ⚙ Settings panel.

### Run a portable build (no SDK needed)

```powershell
.\publish.ps1                       # -> bin\publish\win-x64\
.\bin\publish\win-x64\gnip.exe --urls http://localhost:5099
```

`bin\publish\win-x64\` holds `gnip.exe` + `wwwroot\` + `appsettings.json`. Copy that folder
anywhere and run it. Data files (`gnip.db`, `gnip.settings.json`) are created next to the exe.
To install it as a Windows Service, see [README.md](README.md#run-as-a-windows-service).

---

## 2. Linux server

Targets glibc distros (Ubuntu/Debian/RHEL/etc.) via the `linux-x64` runtime. For Alpine/musl,
publish `linux-musl-x64` instead.

### Step 1 — Build the Linux binary

On your Windows dev box (cross-publish works fine):

```powershell
.\publish.ps1 -Runtime linux-x64    # -> bin\publish\linux-x64\  (gnip, wwwroot\, appsettings.json)
```

*(Or, if the server has the .NET 8 SDK, build there: `dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o out`.)*

### Step 2 — Copy it to the server

```bash
# from the dev box
scp -r bin/publish/linux-x64/* user@server:/tmp/gnip/
# on the server
sudo mkdir -p /opt/gnip && sudo cp -r /tmp/gnip/* /opt/gnip/
sudo chmod +x /opt/gnip/gnip
```

### Step 3 — Service user + writable data dir

```bash
sudo useradd --system --no-create-home --shell /usr/sbin/nologin gnip
sudo mkdir -p /var/lib/gnip
sudo chown -R gnip:gnip /var/lib/gnip /opt/gnip
```

### Step 4 — ICMP permission (the important bit)

On Linux, .NET's ping needs the raw-socket capability or it can't send ICMP. Pick one:

- **Recommended** — grant it to the service via the unit below: `AmbientCapabilities=CAP_NET_RAW`.
- Or grant it to the binary directly: `sudo setcap cap_net_raw+ep /opt/gnip/gnip`
- Or rely on the `ping` fallback: `sudo apt install iputils-ping` (works, but slower and less precise).

Without one of these, every probe logs an error / shows as loss.

### Step 5 — systemd unit

Create `/etc/systemd/system/gnip.service`:

```ini
[Unit]
Description=gnip latency monitor
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
User=gnip
Group=gnip
WorkingDirectory=/opt/gnip
ExecStart=/opt/gnip/gnip --urls http://0.0.0.0:5099 --Gnip:DbPath=/var/lib/gnip/gnip.db
Environment=DOTNET_ENVIRONMENT=Production
AmbientCapabilities=CAP_NET_RAW
CapabilityBoundingSet=CAP_NET_RAW
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now gnip
systemctl status gnip
journalctl -u gnip -f          # watch it: you should see "8.8.8.8 rtt=NNms" lines
```

`Type=notify` works because the app calls `UseSystemd()`; logs go to the journal. The absolute
`--Gnip:DbPath` keeps data in the writable `/var/lib/gnip` (the install dir may be read-only), and
`gnip.settings.json` is written there too.

### Step 6 — Firewall

```bash
sudo ufw allow 5099/tcp
```

Then browse to **http://&lt;server-ip&gt;:5099**.

---

## Production hardening (recommended)

gnip has **no built-in authentication**. To expose it safely, bind it to localhost and front it
with a reverse proxy that adds TLS + auth.

1. In the unit, change `--urls http://0.0.0.0:5099` to `--urls http://127.0.0.1:5099`.
2. Example nginx site (note the SSE-friendly settings — without them the live graph stalls):

```nginx
server {
    listen 80;                       # add TLS via certbot / your CA
    server_name gnip.example.com;
    location / {
        proxy_pass http://127.0.0.1:5099;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $remote_addr;
        proxy_buffering off;         # required: /api/live is a Server-Sent Events stream
        proxy_read_timeout 1h;
    }
}
```

gnip already sends `X-Accel-Buffering: no` on the live stream, so nginx won't buffer it. Add HTTP
basic auth (`auth_basic`) or your SSO at the proxy.

---

## Updating

Re-publish, copy the new files over, and restart:

```bash
sudo systemctl stop gnip
sudo cp -r /tmp/gnip/* /opt/gnip/ && sudo chmod +x /opt/gnip/gnip
sudo systemctl start gnip
```

Your data (`/var/lib/gnip/gnip.db`) and runtime settings (`gnip.settings.json`) are untouched.
