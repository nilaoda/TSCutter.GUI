# Chapters and EPG

When a file or disc title has chapters (MP4, MKV, DVD, Blu-ray), or a TV recording carries EPG (the DVB present/following table), a band above the timeline splits it by chapter or by the program on air, and the status bar shows the chapter or program count. Chapters take precedence when both are present. The band follows the timeline zoom and highlights the segment under the playhead.

## Viewing and jumping

- Hover to see a chapter's range and length, or a program's schedule (with date), range in the recording and description; click to go to the segment start.
- Right-click to go to the chapter (program) start or end, or **Add clip for chapter** (**Add clip for program**). The new clip is named after the chapter or program, or "Chapter N" when the chapter has no name, and its boundaries follow the current cut mode.
- The Chapters or EPG tab of the toolbar's **Info** window lists every chapter, or every program read from the file with its air date and time and whether it is in this recording; click a chapter or a program in the recording to jump to it.

## How EPG is read

EPG is read only near the start of the file; if none is found there, the file is treated as having no EPG and is not scanned further. After you jump elsewhere and stay for a moment, that position is checked once.

::: warning Times are approximate
EPG switches at scheduled times, so the actual broadcast is often a few minutes early or late. A clip added for a program shows "EPG times, check the start and end" until you mark the start or end again.
:::

## Turning the band off

Turn the band off in **View → Chapters / program band** or **Tools → Settings → Preview**. Chapters and EPG are then not read at all, and the band, the count in the status bar and the Chapters / EPG tab of the **Info** window are hidden; turning it back on reads them for the open file. Turning it off also avoids the EPG reads on network or slow drives.
