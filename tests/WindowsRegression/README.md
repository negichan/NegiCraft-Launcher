# Windows performance regression checks

Requires Windows, an interactive desktop, and the .NET 10 SDK. Do not run alongside another launcher/pet instance or hold movement keys during a run. The tests create temporary WPF windows and do not load or save pet settings.

From the repository root:

```powershell
dotnet run --project tests/WindowsRegression -c Release
```

The executable exits nonzero on failure. It checks immediate popover interaction, interrupted animations, unload/reload subscription cleanup, simulated movement/release in software and GPU modes, and the existing physics self-test.

This suite covers only the performance changes and runs against upstream `main` without the settings-window fix in PR #1. Settings ownership, topmost synchronization, and save/cancel tests are outside its scope.

The movement checks are not keyboard-to-display latency measurements. Check fast real A/D + Space input, animated popover clicks, and multi-monitor DPI transitions before merging the performance PR.
