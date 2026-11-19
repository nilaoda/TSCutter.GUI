# Remuxing and muxing

## MP4 / MKV remux editing and merging

The following describes keyframe-mode exports. Smart cut re-encodes video near cut points; see [Smart cut](./smart-cut).

- Choose the output container and retained tracks in **Export settings**. Track order, language, titles, and default/forced flags follow the source.
- Export individual clips or batches, or merge selected ranges from one source. Overlapping selected clips are combined once.
- Video cuts use keyframes and include the marked end frame; audio cuts use compressed packet boundaries. Compatible text subtitles, including MP4 mov_text and MKV ASS, and chapters are trimmed and retimed. MKV also supports PGS bitmap subtitles, restoring the picture active at each cut and clearing it at the end. MKV attachments and MP4 cover art can be retained.
- Open-GOP sources: leading pictures after the end keyframe that display before it are kept with the end. Leading pictures after the start keyframe lack their references and are dropped. A cut can start at such a keyframe only when no later picture references those leading pictures (MPEG-2, HEVC RASL/RADL, non-reference H.264 B-frames). Otherwise the export is rejected and lists usable start keyframes within 20 seconds before and after, if any; smart cut is another option.
- Copy compressed audio/video and codec configuration, preserving container-supported color, HDR static data, and Dolby Vision configuration. Dynamic metadata in the bitstream travels with the packets.
- HEVC in MP4 normally uses `hvc1`, or `dvh1` for Dolby Vision with no compatible base layer (such as Profile 5). If the source carries its parameter sets only in the bitstream, the export tries to rebuild the configuration record from each segment’s starting keyframe. If the required sets are missing or conflicting, the output keeps `hev1` or `dvhe`.
- Check codec and track compatibility, with clear errors when content cannot be safely cut or merged.

::: info Not re-encoded, but not byte-for-byte lossless
Keyframe-mode MP4/MKV export does not re-encode audio/video, but container structures, timestamps, and metadata are rewritten, so file hashes and sizes change. Metadata fields depend on the output container. MP4 exports retaining cover art use standard metadata fields; other MP4 exports can write custom metadata.
:::

Fast MKV text subtitle seeking is enabled by default and reads from about 30 seconds before each cut. Earlier text subtitles spanning the cut may be omitted; disable this option in export settings to retain them. PGS subtitles separately restore their required display state.

Failed or cancelled exports preserve existing destinations and remove temporary output.

## Media muxing

Open **Tools → Merge and Mux → Media Muxer** to combine tracks from video, audio and subtitle files. Select tracks, drag them into output order, and edit their names, language codes, default/forced flags and time offsets. Output supports MKV, MP4, TS and M2TS without re-encoding audio or video.

- Media Muxer uses the same HEVC MP4 tags and preserves in-band parameter sets. If the configuration record needs rebuilding, the muxer reads parameter sets from the first video sample. If the required sets are missing, the output keeps `hev1` or `dvhe`.
- **Optimize for web playback** is available for MP4 and off by default; it moves the index to the beginning and requires an additional move of data within the output file when finishing.
- Positive offsets delay a track; negative offsets advance it, like FFmpeg `-itsoffset`. The output container handles timestamps before zero: MP4 uses an edit list to skip leading content, while MKV and TS/M2TS may shift the timeline.
- **Stop at the shortest track** is off by default; enable it to stop at the earliest audio/video end after offsets, and optionally include subtitles. Cover art and attachments are ignored. Cuts follow compressed packet boundaries; subtitle display durations are clipped to the end. Tracks without duration information require an initial packet read to determine their end.
- Compatible subtitle tracks are copied. Other text subtitles are automatically converted to mov_text for MP4 or SRT for MKV, with the conversion and possible style loss shown before starting. Bitmap subtitles are not converted to text; unsupported tracks must be deselected or use another container. The official runtime includes the required subtitle encoders.
- TS/M2TS does not preserve track names or default/forced flags.

Failed or cancelled operations retain an existing destination and remove the temporary output. Errors include the muxing stage and expandable FFmpeg diagnostics.
