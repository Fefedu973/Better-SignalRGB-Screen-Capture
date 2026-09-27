# MainViewModel behavior tests

Run on Windows with the repository's .NET SDK:

```powershell
dotnet run --project tests/BetterSignalRGB.ViewModelTests/BetterSignalRGB.ViewModelTests.csproj
```

This console harness links the production `MainViewModel` partials, `SourceItem`,
`UndoRedoManager`, geometry and layer helpers, and the actual CommunityToolkit.Mvvm
source generator. It does not duplicate command implementations. Capture, streaming
and settings are deterministic in-memory fakes; only the WinUI bitmap type and
dispatcher boundary are stubbed. Tests do not capture or persist user data.

Covered behavior includes startup preferences and partial read failures; recording,
pause, resume, refresh and stop; shutdown and source edits racing a blocked native
start; a partial paste rejecting Undo while preserving its history; history command
notifications; runtime capture and server errors; source availability retry and
cancellation; negative screen-region undo/redo; multi-selection layer order;
content-only mirrors; rejected/nonfinite/no-op inspector changes; cropped and grouped
keyboard movement; detached debounced saves; and copy/paste/deletion cleanup.

The availability scenario exercises the production five-second retry interval. A full
run currently takes approximately 15 seconds. A failure exits with code 1 and names
the failing scenario. Native capture/encoding and compositor/network tests live in
their separate harnesses; this suite does not validate WinUI rendering or WebView2.
