# TSCutter.GUI 网站

TSCutter.GUI 的使用说明与官网，基于 [VitePress](https://vitepress.dev/) 构建，部署到 GitHub Pages。

- 简体中文（默认）：`docs/`
- 繁體中文：`docs/zh-tw/`
- English：`docs/en/`

首次访问根路径时按浏览器语言自动跳转到繁体中文或英文页面，之后以用户在语言菜单中的选择为准。

## 本地预览

需要 Node.js 20 或更高版本。

```bash
npm ci
npm run dev       # 开发模式，修改后自动刷新
npm run build     # 生成静态页面到 docs/.vitepress/dist
npm run preview   # 预览生成结果
```

本地默认以 `/` 为根路径；要模拟 GitHub 项目页面，可在构建前设置 `SITE_BASE=/TSCutter.GUI/`。

## 部署

推送到 `site` 分支后，`.github/workflows/deploy.yml` 自动构建并发布。仓库首次启用时：

1. **Settings → Pages → Build and deployment → Source** 选择 **GitHub Actions**。
2. **Settings → Environments → github-pages** 的部署分支规则中允许 `site` 分支（默认只允许默认分支）。

页面地址为 `https://<用户名>.github.io/<仓库名>/`。以后绑定自定义域名时，在 Pages 设置中填写域名，并把工作流中的 `SITE_BASE` 改为 `/`。

## 截图

`docs/public/screenshots/<语言>/<名称>-light|dark.webp` 由程序在无界面模式下渲染主窗口得到（Windows 11 浅色 / 深色主题，2 倍分辨率），素材为 FFmpeg 生成的渐变画面、正弦音频和章节，不含任何真实影像。页面中用 `<Screenshot name="editor" alt="…" />` 引用，会按当前语言和浅色 / 深色模式切换。

`docs/public/themes/` 是各主题的设置窗口截图，界面更新后需要重新截取。
