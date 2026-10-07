# Windows performance regression checks

Requires Windows, an interactive desktop, and the .NET 10 SDK. Do not run alongside another launcher/pet instance or hold movement keys during a run. The tests create temporary WPF windows and do not load or save pet settings.

From the repository root:

```powershell
dotnet run --project tests/WindowsRegression -c Release
```

The executable exits nonzero on failure. It checks immediate popover interaction, interrupted animations, unload/reload subscription cleanup, pet mouse gestures, simulated movement/release in software and GPU modes, and the existing physics self-test.

Mouse checks raise routed events on the real WPF pet root, including double-click counts,
drag threshold crossing, normal release and capture loss. Mouse gestures must not toggle
manual sneak, and explicit menu/Shift crouching must remain intact. The test offsets the
recorded press position to trigger a drag without moving the user's cursor.

Settings ownership, topmost synchronization, and save/cancel tests are outside its scope.
Avalonia mouse bindings are not exercised by this WPF suite.

The movement checks are not keyboard-to-display latency measurements. Check fast real A/D + Space input, animated popover clicks, and multi-monitor DPI transitions before merging the performance PR.
