# Resource browser regression

Requires Windows, an interactive desktop, and the .NET 10 SDK.

```powershell
dotnet run --project tests/ResourceBrowserRegression -c Release
```

An optional first argument selects a screenshot output directory:

```powershell
dotnet run --project tests/ResourceBrowserRegression -c Release -- D:/ai/1mcqdq/artifacts/resource-browser
```

Search tests use fake HTTP responses and a fake clock. They do not contact Modrinth
or load/save launcher, account, instance, or pet settings.

The WPF check loads the real resource list from the compiled launcher window,
hosts it temporarily off-screen, checks virtualization, scrolling and row commands,
and optionally renders a PNG. It closes its windows on completion.

Coverage includes category caching, game filtering, single list publication,
search debounce, instance/version/loader cache keys, cancellation, stale failures,
bounded LRU eviction, expiration, empty results and preservation of row action state.

Printed switch timings measure this test's UI-thread work and forced layout,
not keyboard-to-display latency or live API response times. The legacy comparison
uses the same row template and viewport with the old non-virtualized collection/list
shape; it is not a benchmark of a separately built baseline launcher. Avalonia compilation
is covered by the solution build; its native rendering needs separate verification.
