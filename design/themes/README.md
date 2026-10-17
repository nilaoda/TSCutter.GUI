# 桌面主题

Windows 11 浅色和深色共用控件模板，以 Fluent 的表面分层、细边框、圆角、输入框底线及强调色选中状态呈现。字体沿用应用默认设置，应用 Logo 不参与配色切换。

- 色板依据 [WinUI 的主题资源](https://github.com/microsoft/microsoft-ui-xaml/blob/main/controls/dev/CommonStyles/Common_themeresources_any.xaml)定义文字、控件和表面层次；强调色使用固定的浅色蓝和深色蓝，悬停、按下按原色的 90% 和 80% 不透明度绘制。
- 单选框外圈及交互状态依据 [WinUI RadioButton 的资源定义](https://github.com/microsoft/microsoft-ui-xaml/blob/main/controls/dev/CommonStyles/RadioButton_themeresources.xaml)，与普通控件的弱描边分开处理。
- 菜单几何尺寸依据 [WinUI MenuFlyout](https://github.com/microsoft/microsoft-ui-xaml/blob/main/controls/dev/CommonStyles/MenuFlyout_themeresources.xaml) 的鼠标输入规格：条目内边距 `11,4,11,5`、条目外边距 `4,2`、弹出层内边距 `0,2`，普通图标后间距 12，快捷键及箭头前间距 24；选中标记区域按应用的桌面密度收紧为 16 像素标记加 2 像素间距，不套用普通图标的留白。度量集中在 `Windows11Templates.axaml`，各级菜单共用。
- 下拉框采用 [Avalonia Fluent ComboBox](https://github.com/AvaloniaUI/Avalonia/blob/11.3.12/src/Avalonia.Themes.Fluent/Controls/ComboBox.xaml) 的 30 像素箭头区域和 12 像素文字起始留白，沿用应用现有控件高度。
- 输入框和菜单模板保留 [Classic.Avalonia](https://github.com/BAndysc/Classic.Avalonia) 的文本呈现、验证、菜单状态和模板部件，许可见 `THIRD_PARTY_LICENSES.txt`。
- Windows 11 通过 Avalonia 请求 Mica；macOS 使用系统背景模糊作为近似材质，原生图层圆角启用后撤销应用的重复裁剪，避免圆角边缘出现亮边或生硬的像素过渡。只有实际获得材质时才使用半透明表面，不支持时使用实色背景。Windows 11 主题失焦时全窗切换为实色背景，重新激活后恢复材质。
- 常规窗口四角半径为 8，最大化和全屏时取消圆角和自绘边框。材质裁剪与应用绘制保持同步。
- Windows 11 Build 22000 及以上由 DWM 为 Windows 11 和 Aero 主题统一处理窗口、背景材质和阴影的圆角；原生设置成功后撤销应用的重复裁剪。最大化、全屏使用直角，离开圆角主题时恢复系统原值；较旧 Windows 的 Windows 7 / Aero 与 Windows 11 主题均使用直角窗口，与原生矩形边框和阴影一致；不再使用应用圆角或二值 HWND 区域裁剪。
- 较旧 Windows 的 Aero / Windows 11 主题使用 `BorderOnly` 原生装饰，保留缩放及贴靠所需边框，避免最大化时短暂露出原生标题栏；保留原生缩放边框，并通过回调补回 `WS_SYSMENU`；显式启用 DWM 非客户区绘制以保留系统阴影，离开主题时恢复按系统样式绘制。窗口表面和细外框同时取消圆角，材质继续按系统能力回退。Windows 11 主题在底色上方、内容下方绘制不透明细外框，关闭按钮悬停背景覆盖上、右边缘。
- Windows 上标题栏右键复用 HWND 的原生系统菜单，外观和文字跟随宿主系统，并提供原生移动、大小和窗口状态操作；其他平台沿用应用菜单。固定尺寸、对话框及关闭取消仍遵循窗口自身限制。
- Classic、XP、Windows 7、8、10、11 的标题按钮保留本地化文字提示，最大化/最小化后更新为还原；仅 Windows 11 系统上支持 Snap Layouts 的最大化按钮取消应用提示，交给系统处理，旧系统或不满足 Snap 窗口条件时继续显示文字提示。
- 页签以圆角轮廓和不同底色区分状态，选中页签与内容区连接，并保留强调色短线；支持四个方向及键盘焦点。
- 进度条参考 Windows 11 文件复制对话框，使用浅灰轨道、细边框和连续绿色填充；深色配色调整轨道与边框对比度。
- 标题按钮宽度为 46，高度随标题栏资源铺满，关闭背景延伸到顶部和右侧外边缘，共用窗口圆角裁剪，避免浅色接缝；最大化及全屏时取消边缘延伸。
- Windows 11 Build 22000 及以上使用 Windows 11 主题时，允许调整大小和最大化的窗口将最大化按钮标记为系统 `HTMAXBUTTON`，由 Avalonia 转发非客户区输入，悬停显示 Windows 原生 Snap Layouts；遵循系统的贴靠设置。
- 普通控件和菜单项采用 4 的圆角，菜单与浮层采用 8 的圆角。保留现有窗口的布局密度和显式尺寸约束。
- 矢量图标源稿位于 `design/icons/fluent`，生成方式见 `design/icons/README.md`。

调整主题时需检查：普通与禁用状态、复选框三态、键盘焦点、下拉框及多级菜单、长文本、窗口最大化，以及切换回 Classic 后模板和原生材质的恢复。
