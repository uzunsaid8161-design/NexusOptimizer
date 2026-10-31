I built NexusOptimizer, a WPF tool for Windows gamers. The interesting parts for this sub are the internals:

- **MVVM + DI**: CommunityToolkit.Mvvm source generators, Microsoft.Extensions.DependencyInjection.
- **Motion system**: central tokens in a ResourceDictionary, a MotionService API and attached properties (`Entry="Stagger"`, `Hover="Card"`). Every animation uses `FillBehavior.Stop` with the base value already at the end state, so nothing sticks, and animations are interruptible via `HandoffBehavior.SnapshotAndReplace`. Only `Opacity`, `RenderTransform` and `Effect` are animated.
- **Custom controls**: AnimatedNumber (tabular figures), AnimatedRing (StreamGeometry arc), Sparkline, RippleButton (adorner ripple), ToastHost.
- **Data**: Microsoft.Data.Sqlite with WAL and a change-log table so every tweak can be reverted.
- **Measurement**: PerformanceCounter for CPU/RAM, `GPU Engine` 3D counters for GPU.

Packaging: self-contained single-file publish, ~64 MB, no .NET install needed.

Repo (MIT): https://github.com/uzunsaid8161-design/NexusOptimizer
Download: https://github.com/uzunsaid8161-design/NexusOptimizer/releases/latest

Happy to discuss the motion system or the compositor-based FPS sampling - that one is still a compromise until ETW/DXGI present events are in.
