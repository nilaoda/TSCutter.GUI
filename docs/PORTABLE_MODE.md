# Portable mode

[Back to docs](README.md) · [简体中文](PORTABLE_MODE_CN.md)

Portable mode keeps all application data next to the installation instead of in
the user profile folder. It is useful on a USB drive, a shared drive, or anywhere
you want to move the whole install folder to another machine.

## Enable it

Portable mode is strictly opt-in. Release ZIPs do not contain the marker, so a
fresh extract uses the user profile. To enable it, create an empty
`TSCutter.GUI.portable` file yourself in the folder that contains the executable
(the file contents do not matter):

```text
TSCutter.GUI/
  TSCutterGUI.exe        # TSCutterGUI on Linux/macOS
  THIRD_PARTY_NOTICES.txt
  TSCutter.GUI.portable  # create this empty file
```

On macOS, put `TSCutter.GUI.portable` at the root of the `TSCutter.GUI.app`
bundle, next to `Contents`.

On the next launch the app detects the marker, creates a `data` folder beside the
executable (on macOS: beside the `.app` bundle, not inside it), and reads and
writes all user data there:

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
copied, so a stale lock from another install cannot block updates. Migration is
built in a staging folder and activated only after every intended file is copied;
a failed migration leaves the profile in use, and a retry never overwrites
settings saved in the meantime. The folder must be writable or the app falls back
to the user profile and shows the reason in Settings. The data folder and its
files are private: owner-only modes (`0700`/`0600`) on Unix and a protected ACL
for the current user (plus SYSTEM and Administrators) on Windows, because they
contain remote-account password hashes.

## In-place updates

In-place updates replace only files shipped in the package, so the marker and the
`data` folder survive. The macOS update replaces only the `.app` bundle; the sibling
`data` folder is untouched. If the marker was deleted, a marker in an incoming
bundle or package is removed or ignored, so an update never enables portability.
The Windows update helper receives the parent's update-state directory
(`LocalApplicationData`, or the portable data folder), so its lock and result
files stay with that installation. The helper handles its arguments before any
portable initialization, so it never initializes storage relative to its own
temporary executable.

## Turn it off

Delete `TSCutter.GUI.portable`. On the next launch the app copies the portable
data back into the user profile if that profile has no config yet, and records a
`TSCutter.GUI.portable.disabled` opt-out inside `data`. This only happens for a
`data` folder this application finished migrating; an unrelated folder with the
same name is never touched. The `data` folder itself stays where it is. Re-creating
the marker clears the opt-out and turns portable mode back on.

## Notes

- The install folder must be writable. If the `data` folder cannot be created,
  the app falls back to the user profile and shows the reason in Settings →
  General; use normal (non-portable) mode on read-only volumes.
- Settings → General shows whether portable mode is active and has a button to
  open the data location actually in use (the profile folder in normal mode).
  The fallback reason is localized in en-US, zh-CN and zh-TW.
- Moving a portable install to another machine drops recent items and saved
  folders that no longer exist there.
- The Web UI worker uses the same executable and therefore shares the portable
  data folder; its environment-based preferences snapshot does not redirect paths.
- `data/update.lock` records the owning machine. If a portable folder on a shared
  drive was locked on another machine, updates are blocked until that lock is
  older than two hours or is removed.
