# DSH GUI

> DeepSeek Harness（dsh）Web GUI 的 Windows 一键启动器：双击即用**自绘标题栏 + WebView2 内嵌**的窗口播放 splash 动画作为唯一启动画布（任务栏图标从窗口出现那一刻就是黑鲸，不再闪 Chrome/Edge 默认图标；标题栏颜色随主题统一），后台拉起 `dsh web`，GUI 就绪后揭示；关闭窗口自动停服务。

启动动画借鉴 [SPlayer-Next](https://github.com/SPlayer-Dev/SPlayer-Next) 的思路：SVG `stroke-dashoffset` “一笔一划”书写效果——鲸鱼 Logo 升起呼吸，`deepseek` 官方字标浮现，`HARNESS` 七个字母逐笔描边。

![license](https://img.shields.io/badge/license-MIT-blue) ![windows](https://img.shields.io/badge/platform-Windows%2010%2F11-blue) [![Awesome DSH Plugin](https://awesome-dsh-plugin.com/badge.svg)](https://awesome-dsh-plugin.com)

## 效果截图

**启动动画（浏览器 splash，HARNESS 逐笔描边）**

![启动动画](docs/screenshots/startup-animation.png)

**进入后的 GUI 界面**

![GUI 界面](docs/screenshots/gui.png)

---

## 功能特性

- **点击即出动画**：以 WebView2 内嵌的 splash 动画作为唯一启动画布，双击后立刻打开动画，无双层窗口互相抢层级；
- **窗口即原生**：不再丢给 Chrome/Edge，而是启动器自己开一个窗口（`DSH-GUI.exe`）——**任务栏图标从窗口出现那一刻就是黑鲸**（不再闪浏览器默认图标 / 低分辨率占位图），**标题栏颜色随 dsh 主题统一**；
- **便携单文件**：`splash.html`、SVG/PNG 素材、WebView2 SDK 托管 DLL 与原生 loader 全部内嵌进 exe，`DSH-GUI.exe` 单独一个文件即可运行；同目录放置同名素材文件可覆盖内嵌版本（自定义动画，无需重编译）；
- **一笔一划描边**：`HARNESS` 七个字母按 CSS `cubic-bezier(0.25,0.1,0.25,1)` 缓动逐笔书写，笔尖连续、首帧空白；
- **后台加载、就绪切换**：动画终态与“服务端 splash（hold）”是同款画面，跳转肉眼无切换；真实 GUI 在隐藏 iframe 中预加载（官方 `Loading plugins…` 页不可见），检测到启动卡片消失（GUI 就绪）后 splash 淡出揭示——动画→GUI 全程无缝，无“动画播完又加载”的断档；
- **启动器内做认证**：一次性启动 token 由启动器自己换成会话 cookie（不交给页面，避免一次性/进程绑定语义导致的失效），再注入 WebView2；WebView2 与 iframe 只访问干净的 `/`；
- **无 PowerShell**：启动器是单个 C# 程序，无脚本进程，规避“PowerShell 木马”类安全软件启发式误报；
- **开即启动、关即退出**：窗口关闭后自动结束本次启动的 `dsh web` 服务；端口已有服务时只开窗口、不接管生命周期；
- **可配置**：工作目录与端口可通过环境变量或 `workspace.txt` 修改；
- **深浅色跟随**：启动动画与标题栏跟随 dsh 设置里的“外观”选项（浅色/深色/跟随系统）；标题栏与 GUI 侧边栏同色：浅色极浅灰 `#f9fafb`（深色文字/图标），深色 `#1b1b1c`；
- **开源友好**：单文件 C# 源码 + `build.cmd`，使用 Windows 自带 `csc.exe` 编译，无需外部工具链。

## 快速开始

### 方式一：下载便携版（推荐，无需构建）

1. 在 [Releases](https://github.com/Isilsolme/dsh-splash-launcher/releases) 下载最新 `dsh-splash-launcher-vX.Y.Z.zip`（或只下载单文件 `DSH-GUI.exe`）；
2. 解压（或直接把 exe）放到任意目录——exe 自包含全部素材，**单独一个文件即可运行**；
3. 双击 `DSH-GUI.exe`。

> 首次运行可能弹出 SmartScreen“Windows 已保护你的电脑”：点击 **更多信息 → 仍要运行**（exe 未代码签名，属正常提示）。

### 方式二：源码构建

环境要求：

- Windows 10 / 11（含 Microsoft Edge WebView2 Runtime，随 Edge 自带，缺失时使用[独立安装包](https://developer.microsoft.com/microsoft-edge/webview2/)）；
- 已通过 npm 全局安装 DeepSeek Harness：`npm install -g @deepseek-ai/dsh`（`dsh web` 可用）；
- Node.js（npm 全局安装 dsh 时自带依赖）；
- .NET Framework 4.6.2+（Win10/11 自带）。

首次构建前，需在 `packages\ms.webview2\bin` 放置 WebView2 SDK（来自 NuGet 包 `Microsoft.Web.WebView2`）：

```powershell
# 一次性：下载并解出 SDK 到项目（任选方法，目标三文件：
#   Microsoft.Web.WebView2.Core.dll / Microsoft.Web.WebView2.Wpf.dll / WebView2Loader.dll(x64)）
$ver="1.0.4191.47"
$pk="packages\ms.webview2"; New-Item -ItemType Directory -Force -Path "$pk\bin" | Out-Null
Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$ver/microsoft.web.webview2.$ver.nupkg" -OutFile "$pk\wv2.nupkg"
Expand-Archive "$pk\wv2.nupkg" -DestinationPath "$pk\pkg" -Force
Copy-Item "$pk\pkg\lib\net462\Microsoft.Web.WebView2.Core.dll","$pk\pkg\lib\net462\Microsoft.Web.WebView2.Wpf.dll","$pk\pkg\runtimes\win-x64\native\WebView2Loader.dll" "$pk\bin\"
```

构建（双击或命令行运行）：

```bat
build.cmd
```

生成自包含的 `DSH-GUI.exe`（黑鲸图标，素材已内嵌为资源）。构建仅使用 Windows 自带的 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`；WebView2 SDK 的托管 DLL 与原生 loader 被内嵌进 exe，运行时释放到 `%LocalAppData%\DSH-GUI\bin` 并加载，**无需随包分发外部 DLL**。

### 使用

双击 `DSH-GUI.exe` 即可。如需桌面快捷方式：右键 `DSH-GUI.exe` → 发送到 → 桌面快捷方式（图标会自动使用黑鲸）。

### 作为 dsh 插件安装（可选）

本项目同时是一个可安装的 dsh bundle：随包携带 `DSH-GUI.exe`，并在宿主机注册 `desktop_launch` 工具，在对话中直接让 agent 打开带动画的 GUI 窗口：

```sh
dsh plugin --profile web add dsh-splash-launcher
```

安装后重启 `dsh web`，agent 可使用 `desktop_launch` 工具（参数 `workspace` 可选，指定会话工作目录）。

### 配置

| 项 | 默认值 | 修改方式 |
|---|---|---|
| 工作目录（`dsh web` 启动目录） | `%USERPROFILE%`（用户主目录） | 环境变量 `DSH_GUI_WORKSPACE`，或在 exe 同目录新建 `workspace.txt` 写入路径 |
| 端口 | `3080` | 环境变量 `DSH_GUI_PORT` |
| 启动动画版式 | HARNESS 描边 | 源码 `SplashStyle` 改为 `"&logo=draw"` 可切换为“鲸鱼本体一笔一划”版 |
| 动画素材 | 内嵌于 exe | exe 同目录放置同名 `splash.html` / `*.svg` / `whale.png` 可覆盖（自定义，无需重编译） |

## 启动流程

```
双击 DSH-GUI.exe
  ├─ 端口已占用 ────────────────→ 直接打开 WebView2 GUI 窗口（不显示动画、不接管服务）
  └─ 端口空闲
       ├─ 路径解析（缓存命中则 ~0ms；否则已知目录探测 + 并行解析，写入 resolved.cache）
       ├─ 打开自绘标题栏窗口并内嵌 WebView2，播放 file:// splash 动画（唯一启动画布）
       ├─ 将 splash 素材同步进 dsh-web-frontend/dist（服务端 splash 同源的前提）
       ├─ 后台启动 `node <dsh>/lib/bin.js web`（隐藏窗口）
       ├─ 轮询端口就绪（最多 90s）
       ├─ 从服务输出抓取一次性启动 token → 用启动器自己完成 token→cookie 交换（303 + Set-Cookie；
       │     轮询期间反复重扫最新 token，兼容插件树重载导致的 token 轮换）
       ├─ 把会话 cookie 注入 WebView2（host-only @127.0.0.1，SameSite=Strict）
       ├─ 跳转到服务端 splash（hold=1 终态，与动画终态同款画面，肉眼无切换）：
       │     隐藏 iframe 预加载真实 GUI（官方 Loading plugins… 藏在 iframe 里，不可见），
       │     检测到 `_boot_*` 启动卡消失（GUI 就绪）后 splash 淡出揭示——动画→GUI 全程无缝
       └─ 窗口关闭 → taskkill 结束本次 dsh web 进程树
```


## 项目结构

```
dsh-gui/
├─ DSH-GUI.cs                 # 启动器全部源码（C# 5，单文件；WebView2 宿主 + 自绘标题栏）
├─ build.cmd                  # 构建脚本（Windows 自带 csc.exe，内嵌 WebView2 SDK）
├─ splash.html                # 启动页（hold 态 + 后台 iframe 预加载 + 就绪检测）
├─ deepseek-wordmark.svg      # 官方 deepseek 字标（提取自 dsh 前端）
├─ whale.png                  # 黑鲸 Logo（256px，透明底）
├─ whale-anim.svg             # 鲸鱼描边动画（可选版式 logo=draw）
├─ icons/
│  └─ whale-black.ico         # 黑鲸图标（exe 与窗口/任务栏共用）
├─ packages/
│  └─ ms.webview2/bin         # WebView2 SDK（构建引用 + 内嵌；见下）
├─ LICENSE                    # MIT + 品牌素材说明
└─ README.md
```

> `DSH-GUI.exe` 为自包含便携程序（素材已内嵌为资源，可在任意目录单独运行）；仓库内保留素材源文件用于源码构建，且 exe 同目录的同名文件会优先于内嵌版本（自定义动画）。

## 开发

```bat
:: 构建
build.cmd

:: 自检（校验素材路径、dsh 安装路径与前端 dist，不弹窗）
DSH-GUI.exe --selftest
:: 结果写入 selftest.txt，退出码 0 为通过
```

常用调节点（`DSH-GUI.cs`）：

| 想调什么 | 位置 |
|---|---|
| 描边速度 | `splash.html` 的 `.splash-harness .letter` `animation` 时长（0.22s/字母） |
| 相邻字母起笔间隔 | `splash.html` 的 `.splash-harness path:nth-of-type(n)` `animation-delay` |
| 动画/窗口尺寸 | `Program.WinW / WinH`（默认 1100×720，`DSH-GUI.cs`） |
| 标题栏颜色 | `DshWindow.BuildLayout` 的 `barColor / barText`（随 dsh 主题深浅色），`DSH-GUI.cs` |
| 版式（鲸鱼描边） | `Program.SplashStyle` 改为 `"&logo=draw"`（`DSH-GUI.cs`） |

## 已知问题

### 1. ~~任务栏图标会先闪一下 Chrome/Edge 默认图标~~ ✅ 已解决
旧版用浏览器 `--app` 窗口，任务栏按钮先使用浏览器进程自带图标，页面 `favicon` 加载完成后才切换，因此有一闪。已成为 WebView2 宿主后：窗口是启动器自己的，`Window.Icon`（黑鲸）在窗口创建时即设置，**任务栏从窗口出现那一刻就是黑鲸，不再闪默认图标**；标题栏颜色也随主题统一（深色 = 侧边栏深色 `#1b1b1c`，浅色 = 侧边栏浅灰 `#f9fafb`）。

**新增：WebView2 Runtime 依赖**：运行需要 Microsoft Edge WebView2 Runtime（Win10/11 随 Edge 自带）。若缺失，窗口会提示安装，无法回退到浏览器 `--app` 模式。

### 2. 启动有一定等待时间
启动时长主要来自：`dsh web` 服务引导 + 浏览器冷启动 + 前端插件装载，动画会覆盖绝大多数等待过程并持续到 GUI 就绪，属于“可见但基本不可压缩”的时间。首次运行、杀毒软件实时扫描、机械硬盘会进一步加长。启动器自身的路径解析已缓存（`resolved.cache`）且最早预热浏览器，能显著缩短总时长；动画节奏可在 `splash.html` 的 CSS 里调节。

### 3. SmartScreen / 安全软件可能误报（未签名 exe）
本程序会启动隐藏进程、结束后台进程、复制文件，这类“类管理工具”行为可能触发启发式防护（例如卡巴斯基 PDM:Trojan.Win32.Generic 对旧版 PowerShell 脚本的误报）。建议：
- 首次运行若弹出 SmartScreen“Windows 已保护你的电脑”：点击 **更多信息 → 仍要运行**；
- 将 `dsh-gui` 目录加入杀软排除项/受信任应用程序；
- 正式分发时对 `DSH-GUI.exe` 做代码签名（本程序已不使用 PowerShell，命中率大幅降低）。

### 4. 端口已被占用时直接进入 GUI
如果 `3080` 端口已有 `dsh web`（例如正在终端里使用），双击只会打开 GUI 窗口，不会显示启动动画，也不会在关窗时停止该服务——这是刻意设计，避免误杀已有会话。

### 5. 同源预加载依赖 dist 写入权限
启动器需要把 `splash.html` 等素材复制到 npm 全局的 `dsh-web-frontend/dist` 目录。若该目录不可写，会自动退回“文件页动画 → 就绪后直接跳转 GUI”模式（素材内嵌于 exe，此模式下自动释放到 `%LocalAppData%\DSH-GUI\assets`），此时会短暂看到官方 HARNESS 加载页。

### 6. 就绪判定依赖官方启动卡片
浏览器 splash 以同源 iframe 里官方启动卡片（`_boot_*` class）消失判定 GUI 就绪；若官方前端改了启动卡片的 class 结构，可能需要同步更新 `splash.html` 里的 `appReady()` 判定。

## 许可证

- 代码：MIT，见 [LICENSE](LICENSE)。
- `whale.png`、`deepseek-wordmark.svg`、`whale-anim.svg` 派生自 DeepSeek Harness（`@deepseek-ai/dsh`，MIT © 2026 DeepSeek）前端素材，保留 DeepSeek 品牌权利；DeepSeek 名称与鲸鱼 Logo 为各自权利人的商标。

## 致谢

- 启动动画的“一笔一划”与时长编排思路借鉴 [SPlayer-Next](https://github.com/SPlayer-Dev/SPlayer-Next)（AGPL-3.0；本项目未复制其代码，仅参考交互思路）。
- DeepSeek Harness（`dsh`，npm 包 `@deepseek-ai/dsh`）及其 Web GUI。
