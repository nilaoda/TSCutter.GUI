# Remote editing

Run TSCutter.GUI on one computer as the host, and other devices can sign in from a browser to open media, mark cuts and export clips remotely. Each account has its own editing process and output folder, and media folders are read-only.

## Start and sign in

1. On the host, open **Tools → Remote Editing**. Select existing default media and output folders. They must be separate, and neither can contain the other.
2. Add usernames and passwords under **User accounts**. Each account can use the default folders or have its own locations.
3. Start the service, copy an access link, and give it to the user along with their account credentials. Users open the link in a browser and sign in. Each account allows one controlling browser at a time.

**Allow access from this local network** is on by default; turn it off to accept connections only from this computer. The host's local editor is locked while the service runs. Closing the remote settings window keeps the service running; **Stop remote editing**, or confirming that you want to close the host, stops all its workspaces.

## Folders and accounts

Under **User accounts**, clear **Use default folder** for the media or output folder, choose a location, and save.

- Media folders are read-only, and output folders cannot overlap any configured media folder.
- Accounts using the same output folder share its files. For separate access, choose output folders that neither overlap nor contain one another.
- Existing export files are not overwritten: choose another filename or folder on a conflict. **New folder** in save and export-folder dialogs creates and opens a subfolder inside the assigned output folder.
- Remote workspaces support media files and projects; open disc sources in the desktop app.

Changing a running workspace's media or output folder requires confirmation and discards its unsaved edits. Only that account's workspace is stopped, and its browser can reconnect with the same login. Finish exports or media loading and close workspace dialogs before applying a folder change.

Default folders, connection settings, accounts and account preferences are saved in `TSCutter.GUI/remote-editing.json` under the application data folder, with passwords stored as salted hashes. Closing the settings window saves the default folders even before the service starts; use the save button to apply per-account folder changes.

## Workspaces and sign-in

Each account has its own editing process, state and preferences.

- Disconnecting or signing out keeps the workspace. Resetting a password revokes that account's logins and disconnects its browser, and also keeps the workspace.
- Stopping a workspace, disabling or deleting its account, stopping the service or exiting the host discards unsaved edits and remote saved-frame templates. Saved projects and exported files stay on disk.
- Workspaces start on demand, with up to four retained processes by default; the limit can be changed before starting the service. Empty disconnected workspaces are reclaimed after 30 minutes. Workspaces with loaded media, clips, queued exports or open dialogs are kept until stopped. Stop unused workspaces to free capacity.

The same browser can reconnect from a bookmark while the host stays open, the access address is unchanged and its login is still valid. Stopping and restarting the service also keeps logins when the default folders and access address are unchanged. Restarting the host requires signing in again.

## Browser controls

Use **Open** to select media. Mark cuts and save clips as in the desktop app; results go to the account's assigned output folder.

The floating controls panel has **Picture**, **Input** and **Connection** tabs. It opens from the bottom on phones and floats at the bottom-right on desktop; closing it leaves a small button. Clicking outside or pressing Esc closes the panel without clicking the editor underneath. Enable sound or show the phone keyboard in **Input**; audio is transmitted only while sound is on.

**Quality** defaults to **Sharp** and **Resolution** to automatic, which follows the device's pixel density within the quality preset's size limit. If text looks blurred, try Sharp or choose a higher resolution.

- 720p, 1080p, 1440p and 4K set output pixel limits while keeping the workspace aspect ratio. With a fixed resolution, Quality controls encoding quality and bitrate rather than output size. The maximum is 3840 × 2160, or 2160 × 3840 in portrait.
- **Aspect ratio** defaults to **Follow screen**; choose 16:9 for a widescreen workspace. Interface scaling follows the device's display density and browser zoom.
- Lower the resolution to reduce the host's encoding load, or lower the quality to reduce bandwidth.

Phones start in **Touchpad mode**:

- Slide one finger to move the cursor, tap to left-click, tap with two fingers to right-click, and slide two fingers in parallel to scroll. Hold one finger, then slide to drag.
- The virtual cursor and the desktop mouse cursor show text, link and resize shapes; draggable areas show a hold-to-drag hint in the upper-left corner.
- Pinch to zoom, and use **Fit picture to window** to restore the full picture.
- Touching the workspace does not open the keyboard; choose **Show keyboard** in the **Input** tab when you need to type.
- With Touchpad mode off, touch works directly, and sliding one finger pans a zoomed picture.

## Video and network

Video is sent over WebSocket using software-encoded H.264 by default, at up to 60 fps. In the **Picture** tab you can switch to AV1, optimized for screen content, or to JPEG images. The selected mode must be supported by both the host and the browser; actual frame rate and clarity depend on resolution, host performance, browser and network.

- H.264 adjusts bitrate and capture rate from playback feedback; congestion does not change the selected video mode or resolution.
- In H.264 and AV1 modes, capture pauses while the page is hidden and the picture refreshes when you return.
- After a recoverable playback or delivery error, the browser keeps the last picture and retries the selected video stream up to three times in a row; the workspace and edits stay intact. If playback cannot recover, the **Connection** tab shows the reason. Choose **Retry selected video mode** or pick another mode.

The service uses HTTP. Allow inbound TCP traffic on the web port for other devices to connect. IPv4 is used by default. To add IPv6, allow LAN access and select **Enable IPv6 listening** (off by default); both address families then share the same port, and the address list includes available IPv6 addresses. IPv6 URLs need square brackets, for example `http://[2001:db8::1]:5360/`.

## Single-user command line

```sh
TSCutterGUI --web-ui <port> <media-folder> <export-folder> [--lan [--ipv6]]
```

Starts a single-user service that signs in with the pairing code printed in the terminal; desktop accounts are not used. Without `--lan` it listens only on the IPv4 loopback address; `--lan` allows IPv4 LAN access, and `--lan --ipv6` enables both IPv4 and IPv6. Enter `pair` to generate a new code, which also revokes paired browsers and disconnects the current one.
