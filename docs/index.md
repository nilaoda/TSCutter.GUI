---
layout: home

hero:
  name: TSCutter.GUI
  text: 跨平台视频剪辑工具
  tagline: 快速的关键帧剪切、精确到帧的智能剪辑，以及媒体混流和 MPEG-TS 分析修复。支持 Windows、macOS 和 Linux。
  image:
    src: /logo.svg
    alt: TSCutter.GUI
  actions:
    - theme: brand
      text: 下载最新版
      link: https://github.com/nilaoda/TSCutter.GUI/releases/latest
    - theme: alt
      text: 使用指南
      link: /guide/getting-started
    - theme: alt
      text: GitHub
      link: https://github.com/nilaoda/TSCutter.GUI

features:
  - icon: ✂️
    title: 关键帧剪辑
    details: 在兼容的关键帧处剪切，不重新编码音视频。普通 TS/M2TS 单段导出可直接复制原始字节。
    link: /guide/editing
    linkText: 剪辑与导出
  - icon: 🎯
    title: 智能剪辑
    details: 起止点可以落在任意画面上，只重新编码切点附近的视频，中间部分直接复制。
    link: /guide/smart-cut
    linkText: 了解智能剪辑
  - icon: 🧹
    title: 删除模式
    details: 把广告、片头片尾标记为要去掉的范围，其余内容合并为一个文件。
    link: /guide/remove-mode
    linkText: 使用删除模式
  - icon: 💿
    title: DVD 与蓝光
    details: 打开 ISO 或光盘文件夹，选择标题与播放列表，预览 VOB/PGS 字幕并导出片段。
    link: /guide/discs
    linkText: 光盘支持
  - icon: 🎛️
    title: 媒体混流
    details: 选择轨道、调整顺序、修改语言与时间偏移，输出 MP4、MKV、TS 或 M2TS。
    link: /guide/remux-mux
    linkText: 重封装与混流
  - icon: 🔍
    title: TS 检查与修复
    details: 快速检查、包查看器、流过滤与编辑、时间轴修复、多源修复与二进制合并。
    link: /guide/ts-tools
    linkText: TS 工具
---

<div class="home-showcase">

<Screenshot name="editor" alt="智能剪辑主界面：逐帧缩略图、音频波形、节目带与多个片段" />

</div>
