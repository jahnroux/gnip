# releases

Distributable builds (installer + portable exe) are staged here by [`scripts/release.ps1`](../scripts/release.ps1).

## Cut a release

1. Bump `Version` in [`installer/gnip.wxs`](../installer/gnip.wxs) (e.g. `1.2.0.0`) — it is the single source of truth for the release version.
2. From the repo root, run:

   ```powershell
   .\scripts\release.ps1
   ```

It builds `gnip.msi` (a self-contained service + tray — no .NET install needed on the
target) plus the portable single-file exe, and stages versioned copies of both here as
`gnip-<version>.msi` and `gnip-<version>-portable.exe`.

> Close **GnipTray** first if it is running out of `bin\publish\win-x64` — the publish step
> cannot overwrite a running exe and the build will fail on the copy.

## Distributing

Both artifacts are **git-ignored** (`releases/*.msi`, `releases/*.exe`) — the installer is
~100 MB and the portable exe ~47 MB, since each bundles the .NET runtime, so neither belongs
in git history. Hand a file off directly, or publish via **GitHub Releases**, where assets are
stored outside the repo:

```powershell
gh release create v1.2.0 `
  releases\gnip-1.2.0.msi `
  releases\gnip-1.2.0-portable.exe `
  --title "gnip 1.2.0" --notes "..."
```

## Installing

**Installed service** — double-click `gnip-<version>.msi`. It installs the gnip service + tray
to `C:\Program Files\gnip`, auto-starts both (service + tray at login), and keeps data in
`C:\ProgramData\gnip`. Existing installs are upgraded in place; uninstall from Add/Remove
Programs (your data is kept).

**Portable** — drop `gnip-<version>-portable.exe` anywhere and run it. No install, no admin, no
.NET runtime, no sibling files; it serves the UI on <http://localhost:5099> and writes `gnip.db`
and `gnip.settings.json` next to itself. Target host, thresholds and WAN lines are all set from
the Settings panel. Pass `--urls` to change the address.

## Version history

| Version | Highlights |
|---|---|
| 1.2.0 | WAN lines editable from the Settings UI (no more admin-only appsettings edit); portable single-file exe with embedded UI |
| 1.1.0 | WAN line awareness (active-line detection + failover markers); repo restructure |
| 1.0.1 | Collector-freeze fix (logging can no longer kill the background loop); Event Log source registration; tray auto-start at login |
| 1.0.0 | Initial installer: service + tray, auto-start, crash-recovery, setup wizard |
