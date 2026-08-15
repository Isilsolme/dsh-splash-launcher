# DSH GUI

> DeepSeek Harness（dsh）Web GUI 的 Windows 一键启动器：双击即弹出无边框启动动画，后台拉起 `dsh web`，GUI 真正加载完成后无缝淡入；关闭窗口自动停服务。

启动动画借鉴 [SPlayer-Next](https://github.com/SPlayer-Dev/SPlayer-Next) 的思路：SVG `stroke-dashoffset` “一笔一划”书写效果——鲸鱼 Logo 升起呼吸，`deepseek` 官方字标浮现，`HARNESS` 七个字母逐笔描边。

![license](https://img.shields.io/badge/license-MIT-blue) ![windows](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)

---

## 功能特性

- **点击即出动画**：WPF 原生动画窗口，不等待浏览器冷启动，双击后立刻播放；
- **一笔一划描边**：`HARNESS` 七个字母按 CSS `cubic-bezier(0.25,0.1,0.25,1)` 缓动逐笔书写，笔尖连续、首帧空白；
- **后台加载、就绪切换**：启动动画期间，真实 GUI 在浏览器后台（同源 iframe）完成加载，检测到官方启动页（`Loading plugins…`）消失后才淡出动画，不会出现“动画播完又加载”的断档；
- **无 PowerShell**：启动器是单个 C#/WPF 程序，无脚本进程，规避“PowerShell 木马”类安全软件启发式误报；
- **开即启动、关即退出**：窗口关闭后自动结束本次启动的 `dsh web` 服务；端口已有服务时只开窗口、不接管生命周期；
- **可配置**：工作目录与端口可通过环境变量或 `workspace.txt` 修改；
- **开源友好**：单文件 C# 源码 + `build.cmd`，使用 Windows 自带 `csc.exe` 编译，无需外部工具链。

## 快速开始

### 环境要求

- Windows 10 / 11；
- 已通过 npm 全局安装 DeepSeek Harness：`npm install -g @deepseek-ai/dsh`（`dsh web` 可用）；
- Node.js（npm 全局安装 dsh 时自带依赖）；
- Microsoft Edge 或 Google Chrome（二选一，自动探测）。

### 构建

双击或命令行运行：

```bat
build.cmd
```

生成 `DSH-GUI.exe`（黑鲸图标）。构建仅使用 Windows 自带的 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`。

> 也可以直接下载 GitHub Releases 里 Actions 构建的产物。

### 使用

1. 把整个目录放在任意位置（例如 `D:\dsh-gui`）；
2. 运行 `DSH-GUI.exe`；
3. 如需桌面快捷方式：右键 `DSH-GUI.exe` → 发送到 → 桌面快捷方式（图标会自动使用黑鲸）。

### 配置

| 项 | 默认值 | 修改方式 |
|---|---|---|
| 工作目录（`dsh web` 启动目录） | `D:\VSCode` | 环境变量 `DSH_GUI_WORKSPACE`，或在 exe 同目录新建 `workspace.txt` 写入路径 |
| 端口 | `3080` | 环境变量 `DSH_GUI_PORT` |
| 启动动画版式 | HARNESS 描边 | 源码 `SplashStyle` 改为 `"&logo=draw"` 可切换为“鲸鱼本体一笔一划”版 |

## 启动流程

```
双击 DSH-GUI.exe
  ├─ 端口已占用 ────────────────→ 直接打开 GUI 窗口（不显示动画、不接管服务）
  └─ 端口空闲
       ├─ 立即显示 WPF 无边框启动动画（≈0ms，不等浏览器）
       ├─ 解析 dsh 安装路径（npm root -g + node require.resolve）
       ├─ 将 splash 素材同步进 dsh-web-frontend/dist（同源 iframe 探测的前提）
       ├─ 后台启动 `node <dsh>/lib/bin.js web`（隐藏窗口）
       ├─ 轮询端口就绪（最多 90s）
       ├─ 以相同尺寸/位置在动画背后打开浏览器 `--app` 窗口
       │     └─ splash.html(hold) 用隐藏 iframe 加载 `/`
       │     └─ 检测到官方启动卡片消失 → 揭示 iframe，并同步标题
       ├─ WPF 检测到浏览器标题包含 "DeepSeek Harness" → 淡出动画
       └─ 浏览器窗口关闭 → taskkill 结束本次 dsh web 进程树
```

## 项目结构

```
dsh-gui/
├─ DSH-GUI.cs                 # 启动器全部源码（C# 5 / WPF，单文件）
├─ build.cmd                  # 构建脚本（Windows 自带 csc.exe）
├─ splash.html                # 浏览器侧启动页（hold 态 + 后台 iframe 预加载 + 就绪检测）
├─ deepseek-wordmark.svg      # 官方 deepseek 字标（提取自 dsh 前端）
├─ whale.png                  # 黑鲸 Logo（256px，透明底）
├─ whale-anim.svg             # 鲸鱼描边动画（可选版式 logo=draw）
├─ icons/
│  └─ whale-black.ico         # 黑鲸图标（exe 与快捷方式共用）
├─ LICENSE                    # MIT + 品牌素材说明
└─ README.md
```

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
| 描边速度 | `SplashWindow.DrawSeconds`（默认 1.25s/字母） |
| 字母间隔 | `SplashWindow.LetterGap`（默认 0.24s） |
| 动画窗口尺寸 | `Program.WinW / WinH`（默认 1100×720） |
| 最短动画时长 | `SplashWindow.MinShowSec` |
| 就绪判定标题 | `SplashWindow.AppTitle`（按“包含”匹配） |

## 已知问题

### 1. 任务栏图标会先闪一下 Chrome/Edge 默认图标
浏览器 `--app` 窗口创建时，任务栏按钮先使用浏览器进程自带图标，页面 `favicon`（黑鲸）加载完成后才切换，因此会出现极短暂的一闪。这是浏览器自身的图标占位机制，没有命令行参数可预设；当前动画窗口会盖住这段过程，实际看到界面时已是黑鲸图标。彻底隐藏需要 Win32 控制窗口显隐，可能引起页面节流、拖慢启动，暂不实现。

### 2. 启动有一定等待时间
启动时长主要来自：`dsh web` 服务引导 + 浏览器冷启动 + 前端插件装载。动画会覆盖全部等待过程并持续到 GUI 就绪，属于“可见但不可压缩”的时间。首次运行、杀毒软件实时扫描、机械硬盘会进一步加长。可通过 WPF 描边速度（见上）让动画与等待时长更匹配。

### 3. 安全软件可能误报（未签名 exe）
本程序会启动隐藏进程、结束后台进程、复制文件，这类“类管理工具”行为可能触发启发式防护（例如卡巴斯基 PDM:Trojan.Win32.Generic 对旧版 PowerShell 脚本的误报）。建议：
- 将 `dsh-gui` 目录加入杀软排除项/受信任应用程序；
- 正式分发时对 `DSH-GUI.exe` 做代码签名（本程序已不使用 PowerShell，命中率大幅降低）。

### 4. 端口已被占用时直接进入 GUI
如果 `3080` 端口已有 `dsh web`（例如正在终端里使用），双击只会打开 GUI 窗口，不会显示启动动画，也不会在关窗时停止该服务——这是刻意设计，避免误杀已有会话。

### 5. 同源预加载依赖 dist 写入权限
启动器需要把 `splash.html` 等素材复制到 npm 全局的 `dsh-web-frontend/dist` 目录。若该目录不可写，会自动退回“文件页动画 → 就绪后直接跳转 GUI”模式，此时会短暂看到官方 HARNESS 加载页。

### 6. 就绪判定依赖标题
WPF 以浏览器窗口标题包含 `DeepSeek Harness` 判定 GUI 就绪；若官方前端修改标题结构，可能需要同步更新 `SplashWindow.AppTitle`。

## 许可证

- 代码：MIT，见 [LICENSE](LICENSE)。
- `whale.png`、`deepseek-wordmark.svg`、`whale-anim.svg` 派生自 DeepSeek Harness（`@deepseek-ai/dsh`，MIT © 2026 DeepSeek）前端素材，保留 DeepSeek 品牌权利；DeepSeek 名称与鲸鱼 Logo 为各自权利人的商标。

## 致谢

- 启动动画的“一笔一划”与时长编排思路借鉴 [SPlayer-Next](https://github.com/SPlayer-Dev/SPlayer-Next)（AGPL-3.0；本项目未复制其代码，仅参考交互思路）。
- DeepSeek Harness（`dsh`，npm 包 `@deepseek-ai/dsh`）及其 Web GUI。
