# TS tools

Open any utility directly from the **Tools** menu. Each tool opens in its own window and prompts for the required source file or files; no file needs to be loaded in the main editor first.

## Tools

| Tool | Purpose |
| --- | --- |
| **TS Raw Stream Cutter** | Extract a byte or packet range directly from a TS file. |
| **TS Quick Check** | Scan for synchronization loss, TEI/continuity/PES errors, PCR/PTS/DTS issues, A/V drift and bitrate changes, and export a text report. |
| **TS Packet Viewer** | Inspect 188-byte TS or 192-byte M2TS packets, navigate by packet number, offset or PID, and link parsed fields to highlighted Hex bytes. M2TS arrival headers and timestamps are also shown. |
| **TS Elementary Stream Extractor** | Select one or more tracks and export their raw video, audio, subtitle or data payload after removing TS and PES encapsulation. |
| **TS Stream Filter** | Keep selected PIDs or split selected services while rebuilding the required program and service tables. |
| **TS Stream Editor** | Remove tracks or services, remap identifiers and PIDs, and edit service or language metadata while preserving the original encoded media. |
| **TS Timeline Repair** | Analyze and safely correct supported PCR and timestamp discontinuities without hiding transport or packet-loss errors. |
| **TS Multi-source Repair** | Compare compatible recordings of the same feed and time period, then use healthy packet, PES or elementary-stream data to repair damaged regions and long gaps where safe. |
| **TS Binary Merge** | Append ordered TS segments, remove byte-identical overlaps, or merge overlapping recordings from the same source when only TS continuity counters differ. |

## TS checks during export

TS/M2TS exports are checked as the output is written, without a second scan; results appear in the export result window and in Export records.

## Packet sizes

Lossless clipping supports 188-byte TS and 192-byte M2TS based on the actual packet layout. M2TS defaults to `.m2ts` output and retains arrival headers.

| Tools | 188-byte TS | 192-byte M2TS |
| --- | :---: | :---: |
| Quick Check (including batch), raw cutting, packet viewing, service splitting, ES extraction | ✓ | ✓ |
| Timeline repair, stream filtering, stream editing, multi-source repair, binary merge | ✓ | — |

Unsupported packet sizes are reported explicitly.
