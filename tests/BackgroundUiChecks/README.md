# Background checks (Windows)

These checks use WPF and the Windows media codecs. No FFmpeg installation is required.
UI settings writes are redirected to the verification directory. The Windows app
targets Windows 10 version 1809 or later to access the built-in media APIs.

```powershell
dotnet run --project tests/BackgroundUiChecks -c Release
dotnet run --project tests/BackgroundUiChecks -c Release -- --video "D:\wallpaper.mp4"
dotnet run --project tests/BackgroundUiChecks -c Release -- --video "D:\wallpaper.mp4" --quality original --output bin/verification/background-original
dotnet run --project tests/BackgroundUiChecks -c Release -- --video "D:\wallpaper.mp4" --image "D:\wallpaper.jpg" --extra-video "D:\second.mp4" --output bin/verification/background-gallery
```

`--video` also checks encoding bounds, cache reuse, cancellation, playback during drag,
minimize/restore, switching media while a preparation is pending, and process I/O.
Screenshots and `performance.json` go to `--output` (default:
`bin/verification/background-ui`). Process I/O includes cached reads, and input dispatch
timing is not a measurement of displayed FPS. Compare separate cold processes for each
quality on the same machine and media; first-time encoding has a separate memory peak.

Gallery checks cover bounded image/video posters, shared bitmap reuse, source changes,
portrait images, missing/corrupt files, source handle release, real thumbnail/remove
button invocation, current-background state and responsive layouts at widths 1080 and
1500. `--image` and `--extra-video` supply optional real media for gallery screenshots.
Previews are static, decoded off the UI thread with at most two requests in progress;
the shared memory cache retains at most 48 images, each with a maximum edge of 384
pixels. Windows supplies video posters without additional live WPF video players.
Gallery scrolling uses the settings page's single scrollbar.

`--interactive` leaves a sandboxed preview open for real mouse drag/focus testing.
Close that preview to finish the checks.

Playback copies use Windows H.264/AAC encoding at up to 720p/30 fps (efficient)
or 1080p/30 fps (clear), preserve aspect ratio, and retain source audio when present.
Original quality bypasses encoding. Cache keys include the source path, file length,
modification time, quality and format revision; completed copies are reused and
pruned toward a 256 MiB budget (the current copy is retained). Removing a saved item only removes its reference and
managed playback copies, never the original media.
