# NexusOptimizer v1.0.0 — first public preview

A Windows gaming optimizer with a live FPS monitor, real system telemetry and reversible tweaks.
Single-file, self-contained — **no .NET installation required**.

## Download
`NexusOptimizer.exe` (64 MB, win-x64, .NET 8 self-contained)

## What's inside
- **Live FPS** with MIN / AVG / MAX and 1 % / 0.1 %-lows computed from real frame times
- **60 s FPS chart** with gradient fill + frame-time view
- **System ring charts** for CPU / GPU / RAM (PerformanceCounter + GPU Engine counters)
- **Optimizer** with 8 reversible tweaks, risk badges, apply-all / reset-all and SQLite change log
- **Custom motion system** (tokens, service, behaviors, animated numbers/rings/sparklines)
- **Window & borderless fullscreen** (F11 / Esc), hover-expanding sidebar, single-instance guard
- German UI, English documentation

## First run
Windows SmartScreen may warn because the binary is unsigned — choose *More info → Run anyway*.
Optimizer tweaks that need administrator rights are visibly disabled in standard mode; the app never crashes on access violations.

## Known limitations
- FPS is sampled from the WPF compositor, not from per-game Present events (ETW is planned).
- Statistics, settings and the in-game overlay are placeholders in this preview.
- If the single-file host fails with *"Failed to create default extraction directory … error code: 5"*,
  use the folder build (`dotnet publish … -p:PublishSingleFile=false`) or set `DOTNET_BUNDLE_EXTRACT_BASE_DIR`.

Full details: see the [README](https://github.com/uzunsaid8161-design/NexusOptimizer#readme).
