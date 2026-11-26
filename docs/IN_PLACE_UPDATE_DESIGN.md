# In-place update design

[Back to README](README.md)

This document records how the current "check for updates" feature works and how an
in-place install path can be added to it. The new path is **additive**: every
existing behavior (silent startup check, strict version comparison, browser
fallback, Web-UI suppression, release layout) is preserved.

## Implementation status

The additive implementation is in place:

- CI publishes `update-manifest.json` and `SHA256SUMS.txt` alongside the six packages.
- `VersionChecker.CheckForUpdateAsync()` reads the release API and manifest, with the
  original 302 tag check as fallback; `GetLatestTagAsync()`, `CheckUpdateAsync()` and
  `IsNewerTag()` remain unchanged.
- `UpdateService` downloads, verifies SHA-256, extracts with zip-slip protection,
  stages, and applies the update with per-file replacement on Linux and a
  whole-bundle swap on macOS, with rollback on both. Staging files are cleaned up
  after success or on the next launch.
- `UpdatesInfoWindow` is implemented, with a **new** in-app check menu item; the
  original Help → Check update menu item and browser fallback are untouched.
- The feature gate, preflight write check, verified relaunch with restore, and
  startup reporting of helper failures are implemented (see 6.5).
- The manifest file name is trusted but must match an actual release asset; no
  stage or name pattern is reconstructed by the client.
- Tests cover tag/version rules, asset selection, manifest parsing, download
  verification, unsafe archive entries, cancellation, per-file Linux replacement,
  whole-directory/bundle swap with rollback, default backup parent creation, and
  the update lock.

End-to-end self-replacement has been smoke-tested on Windows (helper success and
rollback) and Linux (per-file replacement, unrelated-file preservation, relaunch,
and cleanup) with .NET SDK 11.0.100. A macOS smoke test with a real release build
is still follow-up work.

Verified with .NET SDK 11.0.100: all 55 update-related tests pass on the host,
the Windows VM and the Ubuntu VM. The full suite reports 1029 passed / 237
skipped / 4 failed on Windows (host and VM) and 1031 passed / 237 skipped /
2 failed on Ubuntu. Those failures reproduce on a clean checkout and are
environment-only (localized decimal separator and missing FFmpeg runtime DLLs),
not related to this feature.

## 1. Current behavior

The update feature today is deliberately small: it detects a newer version and
opens the releases page in the browser. It never downloads or installs anything.

### 1.1 Flow

```text
config.json: AutoCheckForUpdates (default true)
        |
App.OnFrameworkInitializationCompleted()          App.axaml.cs:172-176
        |  fire-and-forget, after the main window is shown
        v
App.CheckUpdatesAsync()                           App.axaml.cs:189-212
        |  Task.Delay(1000)
        v
VersionChecker.GetLatestTagAsync()                VersionChecker.cs:33-47
        |  GET .../releases/latest, AllowAutoRedirect = false
        |  read 302 Location -> .../releases/tag/<tag>
        v
VersionChecker.IsNewerTag(latest, App.CurrentTag) VersionChecker.cs:16-31
        |  strict "vMAJOR.MINOR.BUILD" only
        v
localized MessageBox (Yes / No)
        |  Yes
        v
MainWindow.CheckUpdateCommand                     MainWindowViewModel.cs:1639-1644
        |  Process.Start("https://github.com/nilaoda/TSCutter.GUI/releases")
        v
browser opens the releases page
```

### 1.2 Current version identity

`ReleaseInfo` (`../src/TSCutter.GUI/Utils/Application/ReleaseInfo.cs`) reads
`AssemblyMetadata` values emitted by `TSCutter.GUI.csproj:4,8,26-29`:

| Metadata | Value | Used for |
| --- | --- | --- |
| `ReleaseVersion` | `0.10.1` | title, About dialog |
| `ReleaseTag` | `v0.10.1` | `App.CurrentTag`, update comparison |
| `ReleaseStage` | `Beta` | title, About dialog, asset names |
| `BuildDate` | `2026-11-25` | About dialog |

`IsNewerTag` only accepts a `v` prefix plus a three-component `Version`
(`Build >= 0`, `Revision == -1`). Legacy `alphabuild_*` tags, prerelease tags
such as `v0.2.0-beta.1`, and malformed tags are ignored so a stale `/latest`
response can never suggest a downgrade. This rule is covered by
`../src/TSCutter.GUI.Tests/Application/VersionCheckerTests.cs:8-29`.

### 1.3 Existing pieces intended for future use

- `VersionChecker.CheckUpdateAsync()` (`VersionChecker.cs:49-61`) fetches the
  GitHub API `.../releases/latest` JSON and deserializes `GitHubVersionInfo`
  (`tag_name`, `body`, `name`, …) through `AppJsonContext`. The call is currently
  commented out at `App.axaml.cs:192-197`.
- `UpdatesInfoWindow` / `UpdatesInfoWindowViewModel` are registered in DI
  (`App.axaml.cs:91,230`) and covered by `DialogCloseTests`, but the window XAML
  still contains the literal `TODO` (`Views/Application/UpdatesInfoWindow.axaml:23`).

### 1.4 Release artifacts

`../.github/workflows/build-desktop.yml` publishes self-contained NativeAOT
single-file builds for six RIDs. Asset names are already deterministic:

`TSCutter.GUI_<Stage>_<rid>_<Version>.zip`, e.g. `TSCutter.GUI_Beta_win-x64_0.10.1.zip`.
The client does not reconstruct this name: it trusts the file name in the release
manifest and verifies it against the release's asset list.

| RID | ZIP payload |
| --- | --- |
| `win-x64`, `win-arm64` | `TSCutterGUI.exe` + `THIRD_PARTY_NOTICES.txt` |
| `linux-x64`, `linux-arm64` | publish folder contents (`.dbg` excluded) |
| `osx-arm64`, `osx-x64` | `TSCutter.GUI.app` bundle |

The release is tagged `v<Version>` and marked `makeLatest: true`. The workflow
validates `Version = X.Y.Z`, `Stage = Beta`, `Tag = v<Version>`
(`build-desktop.yml:45-64`).

### 1.5 Why in-place replacement is safe for user data

By default configuration lives in `%APPDATA%\TSCutter.GUI\config.json` (with
`remote-editing.json` beside it), recovery files in
`%LOCALAPPDATA%\TSCutter.GUI\Recovery`, and saved frames under `%APPDATA%`.
None of these are inside the application directory, so replacing the executable
or `.app` bundle leaves user settings untouched.

In portable mode (a `TSCutter.GUI.portable` marker beside the executable), all of these move
into `<install>/data` via `AppDataPaths` (beside the macOS bundle, not inside it).
Release ZIPs now include the marker, so a fresh install is portable by default.
Windows and Linux replacements only touch package files, so `data` is untouched;
the updater also carries the marker into older installs unless the user opted out.
The macOS swap replaces only the bundle, leaving the sibling `data` folder alone,
and carries only the marker. See [PORTABLE_MODE.md](PORTABLE_MODE.md).

---

## 2. Compatibility: what must not be removed

The new implementation is an *additional* path, not a replacement of the old
one. The following behavior stays:

1. `AppConfig.AutoCheckForUpdates` remains, defaults to `true`, and remains
   persisted in `config.json`. Tools → Settings keeps its checkbox.
2. Web-UI / headless sessions keep forcing `AutoCheckForUpdates = false`
   (`App.axaml.cs:141`). No installation is attempted from a remote session.
3. The silent, delayed, fire-and-forget check stays. When there is no newer
   version, nothing is shown to the user.
4. `GetLatestTagAsync()`, the 302 discovery, and `IsNewerTag()` stay available
   with their current semantics and tests. They remain the conservative
   pre-check; a newer tag is still required before any install UI appears.
5. `CheckUpdateAsync()` and `GitHubVersionInfo` stay available; they are
   extended with asset metadata rather than replaced.
6. **Help → Check update keeps opening the releases page in the browser.**
   This is the universal fallback whenever in-place update is unavailable,
   declined, or fails.
7. The existing localized strings (`String.UpdatesInfo.*`, `String.Settings.AutoCheckForUpdates`)
   keep their keys and values; new strings are added alongside them.
8. `UpdatesInfoWindow` / `UpdatesInfoWindowViewModel` DI registrations and the
   dialog-close tests keep working.
9. Release tag, stage, and all six asset filenames stay unchanged. The CI
   workflow gains an extra manifest asset; it does not rename or repackage
   existing assets.
10. `ReleaseMetadata_MatchesCompiledVersion` keeps passing (Stage stays `Beta`).
11. No user data, config, recovery file, or saved frame is deleted or moved.

---

## 3. Small changes to existing files

All changes below are additive extensions of the current code.

| File | Additive change | Purpose |
| --- | --- | --- |
| `Models/Application/GitHubVersionInfo.cs` | optional `assets` array (`name`, `size`, `browser_download_url`) and `html_url` | select the right download without hard-coded URLs |
| `Utils/Application/AppJsonContext.cs` | register the new model types for source-generated JSON | AOT-safe deserialization |
| `Utils/Application/VersionChecker.cs` | new `CheckForUpdateAsync()` returning an `UpdateInfo`; new RID mapping | API-based discovery with asset metadata |
| `App.axaml.cs` | keep `CheckUpdatesAsync()` but route "Yes" to the update dialog; keep `MainWindow.CheckUpdateCommand.Execute(null)` for the browser option | richer prompt, old fallback intact |
| `Views/MainWindow.axaml`, `ViewModels/MainWindowViewModel.cs` | add a **new** menu item and command for an in-app check; leave `CheckUpdateCommand` and its menu item untouched | manual install path without changing existing behavior |
| `ViewModels/Application/UpdatesInfoWindowViewModel.cs` | version strings, release notes, progress, and commands | drive the new dialog |
| `Views/Application/UpdatesInfoWindow.axaml` | replace `TODO` with real controls | user-facing install UI |
| `Models/Application/AppConfig.cs` | optional `SkippedUpdateVersion` and `EnableInPlaceUpdates` properties | "Skip this version" and a feature gate, both config-compatible |
| `Views/Application/SettingsWindow.axaml`, `ViewModels/Application/SettingsWindowViewModel.cs` | new checkbox bound to `EnableInPlaceUpdates` | user-controlled feature gate |
| `Lang/en-US.axaml`, `zh-CN.axaml`, `zh-TW.axaml` | new `String.Update.*` keys | localization for new UI |
| `Program.cs` | hidden `--apply-update ...` argument handler next to the existing hidden `--verify-static-*` flags | Windows self-replacement helper |
| `.github/workflows/build-desktop.yml` | upload `update-manifest.json` and `SHA256SUMS.txt` in the release job | verifiable download metadata |

New files: `Models/Application/UpdateModels.cs` and
`Services/Application/UpdateService.cs`, plus `UpdateServiceTests`.

---

## 4. New architecture

```text
Startup (existing)              New menu item: Check for updates (install)
        |                                   |
   AutoCheckForUpdates                      |
        |                                   |
   [1s delay] → VersionChecker  <------------+
        |  API /releases/latest (fallback: existing 302 check)
        v
   UpdateInfo (tag, notes, matching asset, checksum)
        |
   IsNewerTag + SkippedUpdateVersion (existing rule + new skip)
        |
   UpdatesInfoWindow
        +-- Install now ------> UpdateService.DownloadAndPrepareAsync
        |                          -> verify -> extract -> platform applier -> restart
        +-- Open in browser --> MainWindow.CheckUpdateCommand   (existing behavior)
        +-- Skip this version -> AppConfig.SkippedUpdateVersion
        +-- Later ------------> close

Existing Help → Check update menu item: unchanged, always opens the browser.
```

### 4.1 Release manifest

Generated in the `release` job after all six artifacts are downloaded:

```json
{
  "version": "0.10.1",
  "tag": "v0.10.1",
  "releaseNotesUrl": "https://github.com/nilaoda/TSCutter.GUI/releases/tag/v0.10.1",
  "assets": {
    "win-x64":   { "file": "TSCutter.GUI_Beta_win-x64_0.10.1.zip",   "size": 12345678, "sha256": "..." },
    "win-arm64": { "file": "TSCutter.GUI_Beta_win-arm64_0.10.1.zip", "size": 12345678, "sha256": "..." },
    "linux-x64": { "file": "TSCutter.GUI_Beta_linux-x64_0.10.1.zip", "size": 12345678, "sha256": "..." },
    "linux-arm64":{ "file": "TSCutter.GUI_Beta_linux-arm64_0.10.1.zip","size": 12345678, "sha256": "..." },
    "osx-arm64": { "file": "TSCutter.GUI_Beta_osx-arm64_0.10.1.zip",  "size": 12345678, "sha256": "..." },
    "osx-x64":   { "file": "TSCutter.GUI_Beta_osx-x64_0.10.1.zip",    "size": 12345678, "sha256": "..." }
  }
}
```

The client prefers the manifest asset (`browser_download_url` or a constructed
`.../releases/download/<tag>/<file>` URL) and requires its SHA-256 to match.
`SHA256SUMS.txt` is uploaded as a second, human-checkable asset.

### 4.2 RID mapping

| OS | Architecture | RID |
| --- | --- | --- |
| Windows (`OperatingSystem.IsWindows`) | `X64` | `win-x64` |
| Windows | `Arm64` | `win-arm64` |
| Linux (`OperatingSystem.IsLinux`) | `X64` | `linux-x64` |
| Linux | `Arm64` | `linux-arm64` |
| macOS (`OperatingSystem.IsMacOS`) | `Arm64` | `osx-arm64` |
| macOS | `X64` | `osx-x64` |

Any other combination has no matching asset → the dialog offers only the
browser fallback, so behavior never regresses.

---

## 5. Download and verification

`UpdateService.DownloadAndPrepareAsync(UpdateInfo, IProgress<UpdateProgress>, CancellationToken)`:

1. Create a staging folder named `.TSCutter.GUI.update-<guid>`. On Windows and
   Linux it is created inside the install folder, because only files in that
   folder are replaced. On macOS it is a sibling of the `.app` bundle:
   `<parent of bundle>/.TSCutter.GUI.update-<guid>`. This keeps every rename on
   the same filesystem, even when the app is on an external volume.
2. Stream the ZIP to `<file>.part` with `HttpCompletionOption.ResponseHeadersRead`;
   report progress from `Content-Length` (pattern used by existing `IProgress<T>`
   services). Rename to the final name on success.
3. Verify size and SHA-256 against the manifest before touching the archive.
   A mismatch cancels the install and offers the browser fallback.
4. Extract into a staging directory. Validate every entry path: reject absolute
   paths, `..` segments, and anything escaping the staging root (zip-slip).
5. Validate the expected payload per RID: `TSCutterGUI.exe` on Windows,
   `TSCutter.GUI.app/Contents/MacOS/TSCutterGUI` on macOS, `TSCutterGUI` with
   the executable bit on Linux.
6. Check free disk space first, following the existing `DiskSpaceUtil` /
   `ExportSpaceCheck` patterns.
7. Never execute downloaded code before steps 3-5 succeed.

Cancellation is honored at every step; partial downloads are discarded.

---

## 6. Platform replacement strategy

### 6.1 Windows

A running `.exe` cannot be overwritten in place. The app copies its own current
executable to a temp path (e.g. `%TEMP%\TSCutter.GUI.Updater.<guid>.exe`) and starts
it with a hidden argument:

```text
--apply-update <parentPid> <stagedExe> <targetExe> <payloadDir> <packageDir> [original args...]
```

The helper waits for the parent process to exit, renames the running executable
to `.old` (a rename is allowed on Windows even while the file is in use), moves
the staged executable into place with a short retry loop for antivirus/file
locks, copies the notices file, then relaunches the new executable with the
original arguments and exits. On success the `.old` files, the notices backup and
the staging folder are deleted. On failure the old executable and notices are
restored **and the restored version is relaunched**, so the error can be shown to
the user immediately. Using a copy of the same binary avoids shipping a second
updater executable and mirrors the existing hidden `--verify-static-*` flags in
`Program.cs`. The flag is internal and not shown in the UI.

### 6.2 Linux

The Linux ZIP contains only the package's own files (such as `TSCutterGUI` and
`THIRD_PARTY_NOTICES.txt`) at its root, and users may extract it into a folder
that holds other files. The installer therefore replaces **only files present in
the package**, never the whole directory. For each staged file, a temporary copy
is created next to the target, the old target is moved into a backup tree inside
the sibling staging folder, and the temporary copy is renamed into place. On any
failure the backups are moved back in reverse order. Unrelated files in the
install folder are left untouched. The executable bit is reapplied and the app
is relaunched with the original arguments via the shared `Relaunch` helper.

### 6.3 macOS

The staged `TSCutter.GUI.app` bundle replaces the running bundle with the same
whole-directory swap, located by walking up from `Environment.ProcessPath`. The
backup is moved back on failure, the executable bit is reapplied, and the app is
relaunched from `Contents/MacOS/TSCutterGUI` with the original arguments. An
immediate nonzero exit (or a process that cannot start) is treated as failure and
restores the backup. Because the ZIP is downloaded by the app rather than a
browser, no quarantine attribute is added.

### 6.4 Common rules

- Keep a backup of the current install until the new one is verified.
- Preserve the original command line when relaunching.
- A preflight write probe in the folder that holds the staging directory (the
  install folder on Windows/Linux, the bundle's parent on macOS) fails early for
  read-only/system locations; the dialog shows the error and leaves the app usable.
- No update is attempted in a Web-UI session.
- Any unsupported layout, permission error, or verification failure results in
  a clear message plus the existing browser fallback — never a silent failure.

### 6.5 Safeguards and rollback

- **Feature gate**: `AppConfig.EnableInPlaceUpdates` (default `true`, exposed in
  Tools → Settings). When off, both the automatic prompt and the new menu item
  keep the original browser-only behavior.
- **Verification before replacement**: strict tag comparison, skip-version,
  manifest tag/version match, manifest file name present in the release asset
  list, SHA-256 and size verification, zip-slip rejection, payload layout check,
  free-space check. No hard-coded stage or asset-name pattern.
- **Staging**: download and extraction happen inside the install folder on
  Windows/Linux and in a sibling folder on macOS, so all renames stay on one
  filesystem; any failure deletes it and leaves the installed app untouched. On
  success the staging folder and helper copy are deleted; leftovers are removed
  on the next launch.
- **Cleanup safety**: startup cleanup runs only when no update lock is held, and
  checks the age of each individual package folder rather than the shared Updates
  root, so an active download is never deleted.
- **Windows rollback**: the helper renames the running exe to `.old`, and only
  deletes that backup after the new exe has started and stayed alive for two
  seconds with a zero exit code. A failed relaunch is stopped, the old exe and
  notices are restored, the restored exe is relaunched, and the error is written
  to `%LOCALAPPDATA%\TSCutter.GUI\UpdateResult.txt` for immediate display.
- **Linux/macOS rollback**: Linux restores each replaced package file from its
  backup; macOS moves the swapped bundle's backup back. Backup parents are
  created before the first rename. Both platforms relaunch with the original
  arguments and only keep the backup after the new process has stayed alive for
  two seconds or exited with code zero; an immediate failure restores the old
  install. In portable mode, the `TSCutter.GUI.portable` marker is carried into
  the new macOS bundle while the sibling `data` folder is left untouched, so the
  updated app stays portable without writing inside a possibly signed bundle.
- **Transport hardening**: only HTTPS URLs on `github.com` / `*.githubusercontent.com`
  are accepted, and the checksum must be a 64-character hex string.
- **Multi-instance lock**: `update.lock` under `%LOCALAPPDATA%\TSCutter.GUI` holds the
  owner's `AppInstanceIdentity`; a running foreign owner blocks installation. The
  Windows helper releases the lock after it finishes.
- **Still not covered**: cryptographic signature verification (Sigstore / code
  signing). Integrity relies on HTTPS plus the SHA-256 published in the signed-off
  release manifest, not on a detached signature.

---

## 7. UI and localization

`UpdatesInfoWindow` replaces the `TODO` with: current version, new version,
release notes from `body`, a progress bar during download, and buttons:

| Button | Action |
| --- | --- |
| Install now | `UpdateService` → platform applier → restart |
| Open in browser | existing `MainWindow.CheckUpdateCommand` |
| Skip this version | stores `AppConfig.SkippedUpdateVersion` |
| Later | closes the dialog |

New `String.Update.*` keys are added to all three language files next to the
existing `String.UpdatesInfo.*` keys; no existing key is changed or deleted.
The new in-app menu item reports "You are up to date" when there is no newer
release, while the original Help → Check update menu item keeps opening the
releases page as before.

---

## 8. Testing

- Extend `VersionCheckerTests`: RID mapping, asset selection, manifest parsing,
  checksum validation. Keep all existing tag-comparison cases unchanged.
- Add `UpdateServiceTests` using a fake HTTP handler and temp directories:
  checksum mismatch, zip-slip rejection, payload layout, cancellation, rollback,
  default backup parent creation, Linux per-file replacement that preserves
  unrelated files, macOS bundle swap, and the update lock. No test requires real
  network access.
- Keep `ReleaseMetadata_MatchesCompiledVersion` green (Stage stays `Beta`).
- Keep `DialogCloseTests` passing for `UpdatesInfoWindow`.

---

## 9. Rollout and risks

- The in-place path is gated by a user setting and still falls back to the
  releases page on every platform.
- Unauthenticated GitHub API calls are rate-limited (60/hour/IP): results are cached
  for 10 minutes; a manual check forces a refresh, and the manifest avoids extra API
  calls for asset discovery.
- Windows SmartScreen / signing: a copied helper EXE may be treated as a new
  file. Keep the helper name and behavior minimal, and fall back to the browser
  if it is blocked.
- macOS code signing and quarantine are handled by the applier; verify that the
  replaced bundle is launchable before removing the backup.
- Multi-user and read-only installs: detect and fall back rather than asking
  for elevation.
- User consent stays explicit: the in-place path is only taken after the user
  chooses "Install now". Fully automatic installation is out of scope.

## 10. Implementation order

1. CI: manifest + checksum assets (Phase 0).
2. Models and checker extension; keep old check intact (Phase 1).
3. Download, verification, and staging service (Phase 2).
4. Platform appliers with backup/rollback (Phase 3).
5. Dialog, commands, and localization (Phase 4).
6. Tests and gated rollout (Phases 5-6).
