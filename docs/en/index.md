---
layout: home

hero:
  name: TSCutter.GUI
  text: Cross-platform video editor
  tagline: Fast keyframe cutting, frame-accurate smart cut, media muxing, and MPEG-TS analysis and repair. Available for Windows, macOS and Linux.
  image:
    src: /logo.svg
    alt: TSCutter.GUI
  actions:
    - theme: brand
      text: Download the latest release
      link: https://github.com/nilaoda/TSCutter.GUI/releases/latest
    - theme: alt
      text: User guide
      link: /en/guide/getting-started
    - theme: alt
      text: GitHub
      link: https://github.com/nilaoda/TSCutter.GUI

features:
  - icon: ✂️
    title: Keyframe cutting
    details: Cut at compatible keyframes without re-encoding audio or video. Ordinary single-clip TS/M2TS exports copy the original bytes.
    link: /en/guide/editing
    linkText: Editing and exporting
  - icon: 🎯
    title: Smart cut
    details: Place boundaries on any frame. Only video near the cuts is re-encoded; the middle is copied directly.
    link: /en/guide/smart-cut
    linkText: About smart cut
  - icon: 🧹
    title: Remove mode
    details: Mark commercials or opening credits as ranges to discard and export the rest as one file.
    link: /en/guide/remove-mode
    linkText: Use remove mode
  - icon: 💿
    title: DVD and Blu-ray
    details: Open ISO images or disc folders, choose titles and playlists, preview VOB/PGS subtitles and export clips.
    link: /en/guide/discs
    linkText: Disc support
  - icon: 🎛️
    title: Media muxer
    details: Select and reorder tracks, edit languages and time offsets, and output MP4, MKV, TS or M2TS.
    link: /en/guide/remux-mux
    linkText: Remuxing and muxing
  - icon: 🔍
    title: TS inspection and repair
    details: Quick Check, Packet Viewer, Stream Filter and Editor, Timeline Repair, Multi-source Repair and Binary Merge.
    link: /en/guide/ts-tools
    linkText: TS tools
---

<div class="home-showcase">

<Screenshot name="editor" alt="Smart cut editor with frame thumbnails, audio waveform, chapter band and several clips" />

</div>
