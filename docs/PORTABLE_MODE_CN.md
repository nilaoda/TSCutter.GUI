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

下次启动时应用检测到该文件，会在同一目录创建 `data` 文件夹（macOS 为 `.app` 包旁边，不在包内），并在那里读写所有用户数据：

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

原文件不会被删除或改写。更新锁与更新结果文件不会复制，避免把其他安装的过期锁带进便携目录。迁移只执行一次：只要 `data` 文件夹已经存在，后续启动不会再覆盖其中的内容。数据目录与文件均为私有：Unix 下使用仅所有者可读写权限（`0700`/`0600`），Windows 下为当前用户（以及 SYSTEM、Administrators）设置受保护 ACL，因为其中包含远程编辑账号的密码哈希。

## 与就地更新的配合

就地更新只替换发布包里的文件，因此 `TSCutter.GUI.portable` 标记和 `data` 文件夹都会被保留。macOS 更新只替换 `.app` 包，旁边的 `data` 文件夹不受影响，不会改动可能已签名的包内容。如果标记已被删除，更新程序会记录退出标记，不会把新包里的标记重新加回。

## 关闭便携模式

删除 `TSCutter.GUI.portable` 文件即可。下次启动时，如果用户配置目录还没有配置文件，应用会把便携数据复制回去，并在 `data` 中写入 `TSCutter.GUI.portable.disabled` 退出标记。`data` 文件夹仍会保留。重新创建便携标记会清除退出标记，再次启用便携模式。

## 注意事项

- 安装目录必须可写。若无法创建 `data` 文件夹，应用会回退到用户配置目录，并在“设置 → 常规”中显示原因；只读位置建议直接使用普通（非便携）模式。
- “设置 → 常规”会显示便携模式是否开启，并提供打开数据文件夹的按钮。
- 把便携安装复制到另一台机器后，不存在的最近项目和上次使用的目录会被清除。
- Web 界面子进程使用同一个可执行文件，因此会自动共享便携数据目录；通过环境变量传递的偏好快照不会改变数据路径。
- `data/update.lock` 会记录持有锁的机器。如果共享盘上的便携目录被另一台机器锁定，更新会被阻止，直到该锁超过两小时或被删除。
