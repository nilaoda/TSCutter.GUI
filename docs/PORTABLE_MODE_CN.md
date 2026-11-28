# 便携模式（Portable mode）

[返回文档列表](README.md) · [English](PORTABLE_MODE.md)

便携模式把应用的全部用户数据保存在安装目录旁边，而不是用户配置目录中。适合放在 U 盘、共享盘或希望整个安装目录可搬走的使用场景。

## 启用

便携模式严格按需开启，发布 ZIP 不包含标记文件，解压后默认使用用户配置目录。要启用它，请在可执行文件所在目录自行创建一个空的 `TSCutter.GUI.portable` 文件（内容不限）：

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

原文件不会被删除或改写。更新锁与更新结果文件不会复制，避免把其他安装的过期锁带进便携目录。新建数据目录时先在临时目录中准备，所有目标文件复制完成后才会启用；清理临时目录不会跟随目录符号链接，安装目录之外的文件不受影响。已存在的数据目录不会被删除或改名，只会补入缺失文件，不覆盖已有内容，也保留无关文件。每次启动都会检查目录是否可写：若已有目录尚未完成迁移，应用会尝试就地恢复其可写性并继续复制，失败则保持原样；已完成迁移的目录不会被修改，若变为只读则回退到用户配置目录，保留原内容，并在“设置 → 常规”中显示原因。失败的迁移会继续使用用户配置目录，下次重试也不会覆盖其间保存的设置。数据目录与文件均为私有：Unix 下使用仅所有者可读写权限（`0700`/`0600`），Windows 下为当前用户（以及 SYSTEM、Administrators）设置受保护 ACL，因为其中包含远程编辑账号的密码哈希。

## 与就地更新的配合

就地更新只替换发布包里的文件，因此 `TSCutter.GUI.portable` 标记和 `data` 文件夹都会被保留。macOS 更新只替换 `.app` 包，旁边的 `data` 文件夹不受影响，不会改动可能已签名的包内容。如果标记已被删除，新包或新包内自带的标记会被忽略或移除，更新永远不会自动启用便携模式。Windows 更新助手会从父进程接收更新状态目录（`LocalApplicationData` 或便携数据目录），确保锁文件与结果文件留在该安装目录中。助手先处理命令行参数再初始化存储，不会在临时可执行文件旁做便携初始化。

## 关闭便携模式

删除 `TSCutter.GUI.portable` 文件即可。下次启动时，如果用户配置目录还没有配置文件，应用会把便携数据复制回去，并在 `data` 中写入 `TSCutter.GUI.portable.disabled` 退出标记。只有本应用完成迁移的 `data` 文件夹才会被识别，同名无关文件夹不会被触碰。`data` 文件夹仍会保留。重新创建便携标记会清除退出标记，再次启用便携模式。

## 注意事项

- 安装目录必须可写。若无法创建 `data` 文件夹，应用会回退到用户配置目录，并在“设置 → 常规”中显示原因；只读位置建议直接使用普通（非便携）模式。
- “设置 → 常规”会显示便携模式是否开启，并提供打开当前实际数据位置的按钮（普通模式下为用户配置目录）。回退原因提供 en-US、zh-CN、zh-TW 三种语言。
- 把便携安装复制到另一台机器后，不存在的最近项目和上次使用的目录会被清除。
- Web 界面子进程使用同一个可执行文件，因此会自动共享便携数据目录；通过环境变量传递的偏好快照不会改变数据路径。
- `data/update.lock` 会记录持有锁的机器。如果共享盘上的便携目录被另一台机器锁定，更新会被阻止，直到该锁超过两小时或被删除。
