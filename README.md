[![Release](https://img.shields.io/github/v/release/uzunsaid8161-design/NexusOptimizer?color=22d3ee)](https://github.com/uzunsaid8161-design/NexusOptimizer/releases/latest)
[![License: MIT](https://img.shields.io/github/license/uzunsaid8161-design/NexusOptimizer?color=8b5cf6)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8-8b5cf6)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-22d3ee)](#)
# NexusOptimizer v1.1.0

A Windows gaming optimizer with live FPS monitoring, real system telemetry and reversible tweaks.
Built with **C# 12 / WPF on .NET 8**, a dark glass UI and a self-contained folder deployment.

**Version 1.1.0 · Windows x64:** [Download ZIP](https://github.com/uzunsaid8161-design/NexusOptimizer/releases/latest/download/NexusOptimizer-win-x64.zip) · [Release notes](https://github.com/uzunsaid8161-design/NexusOptimizer/releases/latest) · **Website:** https://uzunsaid8161-design.github.io/NexusOptimizer/

**Kurzbeschreibung:** NexusOptimizer zeigt Systemlast und FPS der eigenen Oberfläche und bündelt Autostart, lokale Backups, Temp-Cleaner, Prozessverwaltung, Overlay und Monitorsteuerung. Änderungen sind nachvollziehbar; wo unterstützt, lassen sie sich zurücksetzen. Die FPS-Anzeige misst nicht direkt ein Spiel. Die ZIP-Version enthält .NET und braucht keine separate .NET-Installation.

**Windows-Hinweis:** SmartScreen kann bei einer neuen oder nicht öffentlich signierten Version warnen. Die Version wird ohne öffentliches Code-Signing-Zertifikat veröffentlicht.

> The UI is intentionally **German** (target audience); detailed technical documentation is in English.

## Screenshots

![Dashboard demo](docs/img/demo.gif)

![Dashboard](docs/img/dashboard.png)

![Screenshot](docs/img/promo.png)

## Features

| Area | What it does |
|---|---|
| **Live FPS** | Per-frame sampling via `CompositionTarget.Rendering`, rolling one-second FPS, MIN / AVG / MAX, 1 %- and 0.1 %-lows from the slowest real frame intervals in the last 60 seconds. This measures the app compositor, not a game process. |
| **Statistics** | Live FPS, frametime, rolling 60-second lows and system load for the current app session. |
| **Charts** | 60 s FPS history with gradient fill, frame-time graph, session bars |
| **System panel** | CPU (`PerformanceCounter`), GPU (`GPU Engine` 3D counters), RAM – sampled on a background thread, rendered as animated rings |
| **Optimizer** | 15 actions with risk badges and per-change logging. Registry tweaks save original values locally before applying; bulk apply and reset are limited to reversible, low-risk registry actions. RAM cleanup and clipboard clearing are clearly marked one-shot actions and require confirmation. |
| **Startup manager** | Searches and filters Run/RunOnce entries from 32- and 64-bit HKCU/HKLM views plus both Windows Startup folders. Identical registry entries are shown once; changes are backed up and reversible. HKLM and shared-folder entries need administrator rights. |
| **Backup** | Creates and restores local SQLite database backups. A schema check runs before restore, and the current database is copied to a safety file first. |
| **Cleaner** | Reviews files in the current user's Temp folder that have been unchanged for seven days. Confirmed cleanup moves them to the Windows Recycle Bin. |
| **Process manager** | Live process list with search, CPU/RAM sorting, status filters, watch list and session history. |
| **Display control** | Shows detected monitors and driver-reported modes; supports brightness, temporary mode testing with rollback and local display profiles when the monitor/driver exposes them. |
| **Overlay** | Click-through, topmost HUD for app-compositor FPS and selected system readings. It does not steal focus; exclusive fullscreen may hide it. |
| **Settings** | Saves motion level and speed, manages this installation's Windows startup entry, and opens the local data folder. |
| **Sessions** | SQLite schema for `Sessions`, `FpsSamples`, `Optimizations`, `Settings`; WAL mode |
| **Motion system** | Central tokens (`Themes/Motion.xaml`), `MotionService`, attached behaviors (`Entry`, `Hover`, `Press`), controls (`AnimatedNumber`, `AnimatedRing`, `Sparkline`, `CrossFadeHost`, `RippleButton`, `ToastHost`) and a live playground (button **Motion**) |
| **Window modes** | Custom chrome, maximize/restore, borderless fullscreen (F11 / Esc), sidebar that expands on hover |
| **Robustness** | No UAC prompt (`asInvoker`), single-instance guard, Serilog logging, global exception handling; local data uses the Windows Low-integrity app-data folder and migrates existing files without deleting the originals. |

![Optimizer](docs/img/optimizer.png)
![Fullscreen](docs/img/fullscreen.png)

## Tech stack

- .NET 8 (`net8.0-windows`), WPF, C# 12
- MVVM via `CommunityToolkit.Mvvm` source generators, DI via `Microsoft.Extensions.DependencyInjection`
- SQLite via `Microsoft.Data.Sqlite`, logging via `Serilog`
- Publish: self-contained folder deployment

## Build

```powershell
dotnet restore src/NexusOptimizer/NexusOptimizer.csproj
dotnet build src/NexusOptimizer/NexusOptimizer.csproj -c Release

dotnet publish src/NexusOptimizer/NexusOptimizer.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=false `
  -p:IncludeNativeLibrariesForSelfExtract=false -o publish
```

The output folder contains `NexusOptimizer.exe` and its runtime files. This avoids single-file extraction failures on systems that restrict temporary folders.

## Project layout

```
src/NexusOptimizer/
├─ Services/      FpsSampler, SystemMonitor, Database, OptimizerService, MotionService
├─ ViewModels/    Main, Dashboard, Optimizer
├─ Views/         DashboardView, OptimizerView, MotionPlaygroundWindow
├─ Controls/      AnimatedNumber, AnimatedRing, Sparkline, CrossFadeHost, RippleButton, ToastHost
├─ Behaviors/     Animations (Entry / Hover / Press attached properties)
├─ Themes/        Colors, Styles, Motion tokens, Storyboards
└─ Assets/        app.ico, app.png
tools/gen_brand.py   generates logo, .ico and promo image (Pillow)
ci/build.yml         GitHub Actions workflow (requires the `workflow` token scope to be pushed)
```

## Known limitations

- FPS is measured from the **WPF compositor**, not from per-game Present events yet
  (ETW / `Microsoft-Windows-DXGI` with PresentMon fallback is planned). The dashboard labels this source explicitly.
- Benchmarking and per-game Present-event FPS via ETW are still planned. Statistics and the overlay report app-compositor FPS, not per-game Present events.
- The optimizer snapshots and restores original registry values for seven registry tweaks.
  Power, service, TCP and process-priority actions still use their documented reset values rather than a full original-state snapshot.
- Database backups include session, sample, settings and optimization-log tables. Startup-file and optimizer snapshot files are kept separately under local app data.
- Cleaner scope is limited to the current user's Temp folder; Windows Temp and browser caches are not touched.
- HKLM and shared Startup-folder entries are read-only without elevation. User entries can be managed without it.
- The sidebar expansion still animates layout width (all other motion uses
  `RenderTransform` / `Opacity`).

## Build installed shortcut target

The desktop shortcut currently targets `app2/NexusOptimizer.exe`. Publish the existing project there with:

```powershell
dotnet publish src/NexusOptimizer/NexusOptimizer.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=false `
  -p:IncludeNativeLibrariesForSelfExtract=false `
  -o app2
```

## License

MIT – see [LICENSE](LICENSE). Use the tuning features at your own risk.
