# Loader Checks

Run the deterministic HTTP/cache checks with the .NET 10 SDK:

```powershell
dotnet run --project tests/LoaderCatalogChecks -c Release
```

This covers shared requests, Minecraft version filtering, cancellation isolation,
source changes, failed-request eviction, timeout fallback, and NeoForge version IDs.
HTTP responses are supplied by an in-memory handler.

On Windows, verify the WPF install sheet with:

```powershell
dotnet run --project tests/LoaderUiChecks -c Release -- bin/verification/loader-ui
```

The UI checks use an offscreen window and write PNGs for loading, error, populated,
and minimum-size states. They verify selection cancellation, spinner/skeleton
animation, animation cleanup, virtualization, and selection of recycled rows.
They load the local launcher settings and may prefetch public metadata. They do
not install a game.
