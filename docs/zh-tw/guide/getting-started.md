# 快速開始

TSCutter.GUI 是一款跨平台影片剪輯工具，支援快速的關鍵影格剪下和精確到影格的智慧剪輯，同時整合媒體混流、MPEG-TS 分析與修復功能。適用於 **Windows、macOS 和 Linux**。

## 下載

從 [GitHub Releases](https://github.com/nilaoda/TSCutter.GUI/releases/latest) 下載對應平台的最新 Beta 版。

官方釋出包已內建所需的 [FFmpeg 執行庫](https://github.com/nilaoda/FFmpegSharedLibraries)，無需另行安裝。

::: warning 處理器需求
x64 電腦的 CPU 需支援 SSE4.2 和 POPCNT（x86-64-v2）。不支援的有 Intel Core 2、2013 年以前的 Atom，以及 AMD Athlon 64、Phenom、Athlon II、A 系列 3000 和 E 系列（Bobcat）；在這些 CPU 上程式開啟後會直接結束，請使用 0.7.1 或更早的版本。Apple 晶片和 Arm64 裝置不受影響。
:::

::: tip macOS
如果應用因隔離屬性被攔截，可在終端執行：

```sh
xattr -dr com.apple.quarantine TSCutter.GUI.app
```
:::

## 四步上手

1. 開啟影片，或將檔案拖入視窗。
2. 定位畫面，標記片段起止點。需要按影格剪下時開啟 **智慧剪輯**；需要去掉片段時開啟 **刪除**。
3. 儲存單個片段、合併選取片段，或匯出刪除所選範圍後的內容。
4. 儲存 `.tscut` 專案，之後繼續編輯。

最近開啟過的影片、光碟和專案可以從 **檔案 → 最近開啟** 再次開啟，最多保留 10 項，前 9 項可按數字鍵直接選擇。重新開啟光碟時會預先選取上次的標題；找不到的檔案會標出，點擊時詢問是否移除。可在 **工具 → 設定 → 一般** 中關閉記錄或清除清單。

<Screenshot name="editor" alt="主介面：預覽、導覽列、時間軸與片段列表" />

## 兩種剪輯方式

| 模式 | 處理方式 |
| --- | --- |
| **關鍵影格剪輯**（預設） | 在相容的關鍵影格處剪下，不重新編碼音影片。 |
| **智慧剪輯** | 按影格選擇起止點，只重新編碼切點附近的影片，中間部分直接複製；音訊和字幕不重新編碼。 |

開啟 **刪除**，即可把廣告、片頭片尾等標記為要去掉的範圍，再將其餘內容合併匯出。刪除模式可與兩種剪輯方式搭配使用。

## 支援的格式

- 剪輯 TS/M2TS，重封裝匯出 MP4/MOV 和 MKV/WebM。
- 預覽 FFmpeg 支援的通用影片格式。
- 開啟 DVD-Video 與未加密藍光的 ISO 映象或光碟資料夾。

## 反饋與社群

- [反饋問題](https://github.com/nilaoda/TSCutter.GUI/issues)
- [Discord 社群](https://discord.gg/qG7kk9GQ44)
