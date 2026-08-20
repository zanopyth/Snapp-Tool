# SnapTool

A lightweight, no-install screenshot capture and annotation tool for Windows — a simpler,
purpose-built alternative to ShareX or Snagit. Capture a region, the full screen, or a short
GIF, then mark it up with an Excalidraw-style annotation editor, all from one small tray-resident
app.

## Features

**Capture**
- Region capture, full-screen capture, and GIF region recording, each on its own global hotkey
  (works even while the window isn't focused)
- Hotkeys are user-changeable from Settings — no config file editing required
- Region selection highlights the window/control under your cursor and snaps to it, or drag for a
  free-form rectangle
- GIF recording shows a live on-screen border plus a pause/resume control bar

**Annotation editor**
- Rectangle, ellipse, line, arrow, highlight, freehand pen, and text tools
- Shapes render in a hand-drawn "sketchy" style; arrows are curvable via a draggable bend handle
  and have independently draggable start/end points
- Select tool with click-to-select, rubber-band multi-select, drag-to-move, and delete
- Hand/pan tool for navigating large screenshots
- Copy, save, or save-as straight from the toolbar

**Screenshot history**
- Thumbnail grid of everything you've captured, with Explorer-style multi-select
  (click, Ctrl+click, Shift+click, rubber-band drag)
- Open in editor, show in Explorer, copy, or delete — single or bulk

**Runs quietly**
- Lives in the system tray; closing the window minimizes it instead of quitting
- Only one instance ever runs — relaunching just brings the existing window forward
- Dark, terminal-style interface throughout

## Download

Grab the latest `SnapTool.exe` from [Releases](https://github.com/zanopyth/Snapp-Tool/releases).
It's a single self-contained executable — no installer, no .NET runtime required, no admin
rights needed. Download it, run it.

## Building from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
dotnet build
```

The dev build lands at `bin\SnapTool.exe` (also launchable via `Run SnapTool.bat`). This build is
framework-dependent, so it needs the .NET 8 Desktop Runtime installed on whatever machine runs it.

To produce a standalone, self-contained single-file exe like the ones in Releases:

```
Publish SnapTool.bat
```

which runs:

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Output: `publish\SnapTool.exe`.

## Default hotkeys

| Action | Hotkey |
|---|---|
| Capture region | `Ctrl+Shift+S` |
| Capture full screen | `Ctrl+Shift+F` |
| Record region (GIF) | `Ctrl+Shift+G` |

All three are reassignable from the app's Settings panel.

## Tech

C# / .NET 8, WinForms. No external dependencies — capture, GIF encoding, and the sketchy-style
annotation rendering are all done with GDI+.

## License

[MIT](LICENSE)
