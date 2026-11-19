# Avaware Export Bot

A Windows desktop app that makes sure every AVAproject project (`.apj`) under `T:\` has a Microsoft Access export (`.mdb`) with the same name beside it. For each `.apj` without one, it opens a local copy in AVAproject on a hidden Windows desktop, runs File → Export Project → Microsoft Access (.mdb), answers Metric (Millimeters) when AVAproject asks, quits without saving, and moves the finished `.mdb` next to the `.apj`.

Nothing appears on the user's screen while it works: AVAproject runs on its own hidden desktops and is driven with window messages only, never the mouse or keyboard.

## How it runs

- **Start** exports now, whatever you are doing. **Pause** and **Stop** both stop new exports and let running ones finish; Pause keeps idle auto-start armed, Stop turns it off.
- While the app is open and armed, it starts by itself after 60 minutes with no keyboard or mouse input, and stops starting new exports as soon as you touch the mouse or keyboard again.
- It scans the root when it starts running and then every hour. Newest projects export first.
- It runs one worker per CPU core minus one, each with its own AVAproject on its own hidden desktop.
- A failed export is retried on later passes; after 3 failures it is listed under Failures until you press Retry.
- Closing the window hides it to the tray. Exit from the tray menu; with exports running it asks first, and aborted exports go back in the queue.

The `.apj` files on the share are never opened or changed: each export works on a copy in the app's work folder. A `.mdb` that already exists is never overwritten.

## Where it keeps its data

`%LOCALAPPDATA%\AvawareExportBot\`

| Path | What |
|---|---|
| `settings.json` | Settings (root folder, AVAproject path, workers, idle minutes, rescan minutes, max attempts) |
| `state.db` | SQLite: every `.apj` seen, its status and attempts |
| `logs\yyyy-MM-dd.log` | One line per scan, export and state change; kept 30 days |
| `logs\snaps\` | Screenshots of unexpected AVAproject windows from failed exports |
| `work\<worker>\` | Each worker's local copy of the project being exported |

## Build

Requires the .NET 7 SDK (`global.json` pins 7.0.400 or a later 7.0 feature band) and, to run, Windows with AVAproject installed.

```
dotnet build
dotnet test
```

Publish the app as one file:

```
dotnet publish src/AvawareExportBot.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

The output is `src/AvawareExportBot.App/bin/Release/net7.0-windows/win-x64/publish/AvawareExportBot.exe`.

## Projects

| Project | What |
|---|---|
| `src/AvawareExportBot.Core` | Scanner, SQLite state store, run controller, settings, log. No Windows APIs, so its tests run on Linux. |
| `src/AvawareExportBot.Automation` | Hidden desktops, AVAproject launch and the export engine (Win32). |
| `src/AvawareExportBot.App` | The WinForms app and tray icon. |
| `tests/AvawareExportBot.Core.Tests` | xUnit tests of Core. |
| `tools/AvawareExportBot.ExportOne` | `ExportOne <apj> <outdir>`: exports one project to a folder of your choice, for checking the engine by hand. |
