# 便携模式（Portable mode）

[返回文档列表](README.md) · [English](PORTABLE_MODE.md)

便携模式把应用的全部用户数据保存在安装目录旁边，而不是用户配置目录中。适合放在 U 盘、共享盘或希望整个安装目录可搬走的使用场景。

## 启用

新的发布 ZIP 已自带空的 `TSCutter.GUI.portable` 文件，解压后默认就是便携模式。旧版本发布包没有该文件，可在可执行文件所在目录自行创建一个空文件（内容不限）：

```text
TSCutter.GUI/
  TSCutterGUI.exe        # Linux/macOS 为 TSCutterGUI
  THIRD_PARTY_NOTICES.txt
  TSCutter.GUI.portable  # 新建这个空文件
```

macOS 上请把 `TSCutter.GUI.portable` 文件放在 `TSCutter.GUI.app` 包根目录，与 `Contents` 同级。

下次启动时应用检测到该文件，会在同一目录创建 `data` 文件夹，并在那里读写所有用户数据：

| 数据 | 便携模式路径 |
| --- | --- |
| 设置与最近项目 | `data/config.json` |
| 远程编辑账号与设置 | `data/remote-editing.json` |
| 保存的帧 | `data/saved-frames.json` |
| 崩溃恢复文件 | `data/Recovery/` |
| 更新锁与更新结果 | `data/update.lock`、`data/UpdateResult.txt` |

## 首次启动的数据迁移

如果 `data` 文件夹尚不存在，应用会从当前用户配置目录复制一次已有数据：

- `%APPDATA%/TSCutter.GUI/`（macOS：`~/Library/Application Support/TSCutter.GUI/`）下的 `config.json`、`remote-editing.json`、`saved-frames.json`；
- `%LOCALAPPDATA%/TSCutter.GUI/Recovery/`（macOS 同上）下的恢复文件。

原文件不会被删除或改写。更新锁与更新结果文件不会复制，避免把其他安装的过期锁带进便携目录。迁移只执行一次：只要 `data` 文件夹已经存在，后续启动不会再覆盖其中的内容。

## 与就地更新的配合

就地更新只替换发布包里的文件，因此 `TSCutter.GUI.portable` 标记和 `data` 文件夹都会被保留。macOS 更新会整体替换 `.app` 包，更新程序会在替换前把这两项复制到新包中。

## 关闭便携模式

删除 `TSCutter.GUI.portable` 文件即可。应用会恢复使用用户配置目录；`data` 文件夹会原样留在安装目录中，需要时可手动把其中的文件复制回用户配置目录。

## 注意事项

- 安装目录必须可写。若无法创建 `data` 文件夹，应用会在控制台输出提示并自动回退到用户配置目录；只读位置建议直接使用普通（非便携）模式。
- Web 界面子进程使用同一个可执行文件，因此会自动共享便携数据目录。
- 就地更新会把标记带入旧安装目录，因此从旧版本更新后会自动启用便携模式。
