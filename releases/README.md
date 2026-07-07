# releases

Distributable installers are staged here by [`scripts/release.ps1`](../scripts/release.ps1).

## Cut a release

1. Bump `Version` in [`installer/gnip.wxs`](../installer/gnip.wxs) (e.g. `1.1.0.0`).
2. From the repo root, run:

   ```powershell
   .\scripts\release.ps1
   ```

It builds `gnip.msi` (a self-contained service + tray — no .NET install needed on the
target) and stages a versioned copy here as `gnip-<version>.msi`.

## Distributing

The `.msi` files are **git-ignored** — each is ~100 MB (they bundle the .NET runtime), so
they don't belong in git history. Hand the file off directly, or publish it via **GitHub
Releases**:

```powershell
gh release create v1.1.0 releases\gnip-1.1.0.msi --title "gnip 1.1.0" --notes "..."
```

## Installing

Double-click `gnip-<version>.msi`. It installs the gnip service + tray to
`C:\Program Files\gnip`, auto-starts both (service + tray at login), and keeps data in
`C:\ProgramData\gnip`. Existing installs are upgraded in place; uninstall from Add/Remove
Programs (your data is kept).

## Version history

| Version | Highlights |
|---|---|
| 1.1.0 | WAN line awareness (active-line detection + failover markers); repo restructure |
| 1.0.1 | Collector-freeze fix (logging can no longer kill the background loop); Event Log source registration; tray auto-start at login |
| 1.0.0 | Initial installer: service + tray, auto-start, crash-recovery, setup wizard |
