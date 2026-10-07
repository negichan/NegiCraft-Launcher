# Windows performance regression checks

Requires Windows, an interactive desktop, and the .NET 10 SDK. Do not run alongside another launcher/pet instance or hold movement keys during a run. The tests create temporary WPF windows and do not load or save pet settings.

From the repository root:

```powershell
dotnet run --project tests/WindowsRegression -c Release
```

The executable exits nonzero on failure. It checks immediate popover interaction, interrupted animations, unload/reload subscription cleanup, simulated movement/release in software and GPU modes, and the existing physics self-test.

Interception checks cover both WPF constructors' defaults and menu icons, enabling
interaction without swallowing outside clicks, explicit menu toggles, the dispatched
shortcut handler, and mouse-hook cleanup. They briefly install the real hooks, but
invoke click/shortcut handlers directly rather than injecting system input.
They do not verify physical Alt+CapsLock input or native click delivery to another app.
Avalonia runtime behavior is outside this WPF suite's scope.

Settings ownership, topmost synchronization, and save/cancel tests are outside its scope.

The movement checks are not keyboard-to-display latency measurements. Check fast real A/D + Space input, animated popover clicks, and multi-monitor DPI transitions before merging the performance PR.
