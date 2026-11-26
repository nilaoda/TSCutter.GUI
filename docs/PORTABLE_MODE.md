# Portable mode

[Back to docs](README.md) · [简体中文](PORTABLE_MODE_CN.md)

Portable mode keeps all application data next to the installation instead of in
the user profile folder. It is useful on a USB drive, a shared drive, or anywhere
you want to move the whole install folder to another machine.

## Enable it

Release ZIPs already contain an empty `TSCutter.GUI.portable` marker, so a fresh
extract is portable by default. For older packages, create the marker yourself in
the folder that contains the executable (the file contents do not matter):

```text
TSCutter.GUI/
  TSCutterGUI.exe        # TSCutterGUI on Linux/macOS
  THIRD_PARTY_NOTICES.txt
  TSCutter.GUI.portable  # create this empty file
```

On macOS, put `TSCutter.GUI.portable` at the root of the `TSCutter.GUI.app`
bundle, next to `Contents`.

On the next launch the app detects the marker, creates a `data` folder beside the
executable, and reads and writes all user data there:

| Data | Portable path |
| --- | --- |
| Settings and recent items | `data/config.json` |
| Remote editing accounts and settings | `data/remote-editing.json` |
| Saved frames | `data/saved-frames.json` |
| Crash recovery files | `data/Recovery/` |
| Update lock and result | `data/update.lock`, `data/UpdateResult.txt` |

## First-launch migration

If the `data` folder does not exist yet, the app copies existing data from the
user profile once:

- `config.json`, `remote-editing.json` and `saved-frames.json` from
  `%APPDATA%/TSCutter.GUI/` (macOS: `~/Library/Application Support/TSCutter.GUI/`);
- recovery files from `%LOCALAPPDATA%/TSCutter.GUI/Recovery/` (macOS: the same
  Application Support folder).

Original files are left untouched. The update lock and result files are not
copied, so a stale lock from another install cannot block updates. Migration runs
only once: as long as `data` exists, later launches never overwrite its contents.

## In-place updates

In-place updates replace only files shipped in the package, so the marker and the
`data` folder survive. The macOS update replaces the whole `.app` bundle, so the
updater copies both of them into the new bundle before swapping it in.

## Turn it off

Delete `TSCutter.GUI.portable`. The app immediately goes back to the user profile
folders; the `data` folder stays where it is and can be copied back manually if
needed.

## Notes

- The install folder must be writable. If the `data` folder cannot be created,
  the app prints a message to the console and falls back to the user profile;
  use normal (non-portable) mode on read-only volumes.
- The Web UI worker uses the same executable and therefore shares the portable
  data folder.
- In-place updates carry the marker into older installs, so updating turns portable mode on as well.
