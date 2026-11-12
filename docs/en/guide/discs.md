# DVD and Blu-ray

Open DVD-Video and unencrypted Blu-ray sources from ISO images or disc folders (`VIDEO_TS` / `BDMV`), choose a title or playlist, navigate across its files, and preview VOB or PGS subtitles. DVD CSS protection is supported where the runtime can handle it.

## Keyframe cutting

Cut without re-encoding, defaulting to VOB for DVD and M2TS for Blu-ray; MP4/MKV remain available for compatible tracks.

- Lossless export rejects playlist joins that enter a clip inside a GOP.
- Disc M2TS output is remuxed with timestamps rebuilt for the exported timeline.
- Standalone VOB does not carry the IFO subtitle palette; use MKV to preserve subtitle colors in one file.

## Smart cut

Disc titles also support [smart cut](./smart-cut), using the container chosen in the export options (DVD: VOB, MKV or MP4; Blu-ray: M2TS, MKV or MP4). Output is written by the muxer, so the packet-level preservation of TS sources does not apply.

- DVD subtitles are kept in VOB and MKV output.
- Blu-ray PGS subtitles are kept in M2TS and MKV output: the subtitle on screen at a cut is restored by reading about 30 seconds back (if its start is not found there, the cut point is treated as having no subtitle), and the screen is cleared at each segment end.

## DVD seeking accuracy

DVD seeking is only VOBU-accurate, and every jump rebuilds timestamps, so preview times may differ from the actual position. The app records each cut by the picture shown when marking (a nearby keyframe plus an offset), and the export reads forward from shortly before the first cut to find exactly that picture.

## Not supported yet

- Playlists whose stream layout changes between PlayItems have not been validated; track selection reflects the streams detected when opening the title.
- Disc menus, protected Blu-ray discs, and saving disc sources in projects.
