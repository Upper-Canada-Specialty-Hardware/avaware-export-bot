# Avaware Export Bot

A Windows desktop app that makes sure every AVAproject project (`.apj`) under `T:\` has a Microsoft Access export (`.mdb`) with the same name beside it. For each `.apj` without one, it opens a local copy in AVAproject on a hidden Windows desktop, runs File → Export Project → Microsoft Access (.mdb), answers Metric (Millimeters) when AVAproject asks, quits without saving, and moves the finished `.mdb` next to the `.apj`.

Nothing appears on the user's screen while it works: AVAproject runs on its own hidden desktops and is driven with window messages only, never the mouse or keyboard.

## Install

Needs Windows with AVAproject and the [.NET 7 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/7.0) (x64). No admin rights are needed. In PowerShell:

```powershell
irm https://raw.githubusercontent.com/Upper-Canada-Specialty-Hardware/avaware-export-bot/main/install.ps1 | iex
```

[`install.ps1`](install.ps1) downloads the latest release's exe and its SHA-256. It refuses a download that doesn't match, installs to `%LOCALAPPDATA%\Programs\AvawareExportBot\AvawareExportBot.exe` and adds an "Avaware Export Bot" Start menu shortcut. Run it again to reinstall; exit the app from its tray menu first.

## Updates

The app keeps itself up to date from this repository's releases:

- It checks a minute after it starts and every 6 hours after that. **Check for updates** in the tray menu checks right away.
- A newer release is downloaded in the background and installed only if its SHA-256 matches the release's checksum file.
- Once an update is downloaded, the app starts no new exports or scans. Running exports finish, and then it restarts as the new version. It comes back in the same state (Running, Armed or Stopped) with its window shown or hidden as before, and a tray balloon says it was updated. A running export is never cut off.
- If the new version doesn't start within a minute, the app puts the old exe back, restarts it and never tries that version again. A newer release is still installed.
- **Update automatically** in Settings turns this off; **Check for updates** still works.
- Only exes built by the release workflow update themselves. A build you make yourself shows "Dev build, updates off".

## Releases

**Every merge to `main` is released.** [`release.yml`](.github/workflows/release.yml) runs on the merge, then:

- It picks the next patch version: the highest `vX.Y.Z` tag plus one (`v0.1.0` first, then `v0.1.1`, `v0.1.2`, …).
- It runs the tests and builds the exe with that version.
- It publishes the release, with `AvawareExportBot.exe`, `AvawareExportBot.exe.sha256` and notes generated from the merged pull requests, within a few minutes.
- Installed apps pick it up at their next check (within 6 hours) or from **Check for updates**.

If a run fails (a failing test, say), nothing is published and no version number is used up: the next merge takes the same number.

**To bump the minor or major version**, publish a release by hand with a higher tag:

```
gh release create v0.2.0 --generate-notes
```

The same workflow builds it and attaches the exe and checksum, and later merges continue from it (`v0.2.1`, …). You can also publish it in the GitHub web UI. Tags must be `vX.Y.Z` and versions only go up; a tag lower than the latest is ignored by installed apps. A **prerelease** is built too but never installed: apps only look at the latest full release.

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
| `settings.json` | Settings (root folder, AVAproject path, workers, idle minutes, rescan minutes, max attempts, update automatically) |
| `updates\` | A downloaded, verified update waiting to be applied, with its SHA-256 |
| `update.json` | The version that failed to start after an update, so it isn't tried again |
| `state.db` | SQLite: every `.apj` seen, its status and attempts |
| `logs\yyyy-MM-dd.log` | One line per scan, export and state change; kept 30 days |
| `logs\snaps\` | Screenshots of unexpected AVAproject windows from failed exports |
| `work\<worker>\` | Each worker's local copy of the project being exported |

## Development

Requires the .NET 7 SDK (`global.json` pins 7.0.400 or a later 7.0 feature band) and, to run, Windows with AVAproject installed.

```
dotnet build
dotnet test
```

Publish the app as one file, as the release workflow does (without `-p:AutoUpdateBuild=true`, so it is a dev build that never updates itself):

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
