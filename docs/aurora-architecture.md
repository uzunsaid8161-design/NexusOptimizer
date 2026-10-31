# Aurora architecture (NexusOptimizer 2.0) — working notes

Status: **stage 1–2 of the upgrade** implemented and covered by tests (34 green).
This document records the decisions that shape the rest of the build.

## Layering

```
NexusOptimizer.Core        pure domain + math (no WPF, no DB, no Windows APIs)
NexusOptimizer (WPF app)   presentation, services, infrastructure glue
NexusOptimizer.Core.Tests  xunit v3, runs in-process (no vstest pipes)
```

`Core` must stay platform-neutral so tests run without hardware, admin rights or ETW.
Anything touching registry/services/ETW lives in the app project behind interfaces.

## Implemented in Core

| Area | Type | Notes |
|---|---|---|
| Frame analytics | `FrametimeHistogram` | 0.01 ms buckets, fixed ~400 KB memory, percentiles from cumulative counts |
| Lows | `FrametimeHistogram.AverageWorstMs` | 1 % / 0.1 % low = FPS of the **average of the worst frames**, not a percentile of FPS |
| Outliers | `FrametimeHistogram.Discarded` | frames ≥ 1000 ms counted separately (suspend artefacts) |
| Stutter | `StutterDetector` | frame > 2 × rolling median (120 frames); micro < 200 ms, hang ≥ 200 ms |
| Session metrics | `SessionAggregator` | mean/median FPS, p95/p99, stutters/min, over-budget share, stability index |
| Snapshots | `SnapshotDocument`, `SnapshotCodec` | SHA-256 checksum over content, schema version, JSON envelope |
| Rollback | `RollbackPlanner` | reverse order, preview text for the confirmation dialog |
| Crash safety | `WriteAheadJournal` | Prepared → Applied → Closed; open records force rollback at next start |
| CLI | `StartupOptions` | `--capture --duration --game --profile --export --safe --silent --diagnose` |
| Errors | `Result<T>` | expected failures as values, exceptions only for bugs |

## Decisions and rejected alternatives

1. **Histogram instead of sorted array.** A 3-hour session at 300 FPS is ~3.2 M frames;
   sorting costs ~26 MB and blocks. Rejected: storing raw samples in memory.
2. **Lows from averaged worst frames.** Percentile-of-FPS weights spikes incorrectly and
   disagrees with PresentMon/CapFrameX. Rejected: `1000 / P99(frametime)`.
3. **Rolling median for stutters.** A mean is destroyed by single 500 ms hitches.
4. **Snapshot with checksum + schema version.** Rollback of a corrupted snapshot is worse
   than no rollback; the codec refuses mismatches.
5. **WAL journal before every action.** A crash mid-plan is the realistic failure mode;
   `Closed` is written only after the whole plan succeeded.
6. **xunit v3 (in-process).** The classic vstest host needs named pipes, which are blocked
   in this sandbox; MTP runs the tests in the same process.

## Known constraints of this build environment

- **ETW real-time sessions require administrator rights.** The capture service must
  degrade to PerformanceCounter-only mode with a visible state, never crash.
- **Fullscreen-exclusive overlays are not achievable** with a plain topmost window; every
  tool that reliably draws there injects a DLL. Aurora will ship a WS_EX_LAYERED
  composition overlay that works for borderless/windowed games and documents the limit
  instead of claiming exclusive support.
- **Single-file extraction:** `%TEMP%\.net` can be blocked; the folder build and
  `DOTNET_BUNDLE_EXTRACT_BASE_DIR` are documented in the main README.

## Next stages

3. ETW capture service (DXGI/D3D9 present events, PID + LUID mapping, dedupe, ring buffer,
   channel → UI batch dispatcher) with a synthetic replay harness.
4. SQLite migrations + snapshot store + retention/aggregation.
5. Optimizer actions on top of `SnapshotDocument` + journal + safe mode.
6. UI integration (replace compositor sampling, surface sensor/ETW state, diagnose panel).
