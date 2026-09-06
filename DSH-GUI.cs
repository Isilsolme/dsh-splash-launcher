// DSH GUI 一键式启动器（C#，无需 PowerShell）
// 双击即打开浏览器 --app 窗口播放 splash 动画（唯一启动画布）；后台启动 dsh web；
// 服务就绪后把 token 写入 token.js，预热页读到后原地跳到同源 splash，加载真实 GUI 后揭示；关闭窗口停服务。
// 开源友好：单个 .cs 文件即可用 Windows 自带 csc.exe 编译。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using IOPath = System.IO.Path;

namespace DshGui
{
    public static class Program
    {
        static string Workspace = "";
        static int Port = 3080;
        static string Url = "http://127.0.0.1:3080";
        // alpha.3+ 的 dsh web 用一次性启动 token 换取浏览器 cookie;从服务输出行捕获后注入 splash target
        static string LaunchToken = null;
        const string SplashStyle = ""; // "&logo=draw" 可切换鲸鱼描边版式

        internal static readonly string AppDir = AppDomain.CurrentDomain.BaseDirectory;
        static readonly string BaseDir = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSH-GUI");
        static readonly string ProfileDir = IOPath.Combine(BaseDir, "profile");
        static readonly string LogFile = IOPath.Combine(BaseDir, "launcher.log");
        static readonly string ServerOut = IOPath.Combine(BaseDir, "server.out.log");
        static readonly string ServerErr = IOPath.Combine(BaseDir, "server.err.log");
        static readonly string LockFile = IOPath.Combine(BaseDir, "owner.lock");

        static string NpmRoot = "";
        static string DshBin = "";
        static string DistDir = "";
        static string NodeExe = "";
        // 路径解析结果缓存：升级 dsh 或删除该文件后会自动重新求解
        static readonly string ResolveCacheFile = IOPath.Combine(BaseDir, "resolved.cache");
        static bool NoOpenSupported = false;
        static string LastServerError = null;
        static int ExitCode = 0;
        static double WinW = 1100;
        static double WinH = 720;
        static int WinLeft = 0;
        static int WinTop = 0;

        // dsh 用户数据根目录：$DSH_HOME > ~/.dsh，与 dsh-home-paths 的 resolveDshHome 保持一致
        static string DshHomePath()
        {
            string env = Environment.GetEnvironmentVariable("DSH_HOME");
            string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(env))
            {
                env = env.Trim();
                if (env == "~") return userHome;
                if (env.StartsWith("~/") || env.StartsWith("~\\")) env = IOPath.Combine(userHome, env.Substring(2));
                return IOPath.GetFullPath(env);
            }
            return IOPath.Combine(userHome, ".dsh");
        }

        // 读取 dsh 外观偏好：settings.yaml 的 ui-theme.preference，缺省跟随系统
        internal static string ReadDshThemePreference()
        {
            try
            {
                string file = IOPath.Combine(DshHomePath(), "settings.yaml");
                if (!File.Exists(file)) return "system";
                string[] lines = File.ReadAllLines(file);
                bool inTheme = false;
                foreach (string raw in lines)
                {
                    string trimmed = raw.Trim();
                    if (!inTheme)
                    {
                        if (trimmed == "ui-theme:" || trimmed.StartsWith("ui-theme:")) inTheme = true;
                        continue;
                    }
                    if (trimmed.StartsWith("preference:"))
                    {
                        string val = trimmed.Substring("preference:".Length).Trim().Trim('"', '\'');
                        if (val == "light" || val == "dark" || val == "system") return val;
                    }
                    if (raw.Length > 0 && !char.IsWhiteSpace(raw[0]) && !trimmed.StartsWith("preference:"))
                        inTheme = false;
                }
            }
            catch { }
            return "system";
        }

        static string ThemeQuery()
        {
            return "&theme=" + Uri.EscapeDataString(ReadDshThemePreference());
        }

        static void LoadConfig()
        {
            // 工作目录：环境变量 DSH_GUI_WORKSPACE > 同目录 workspace.txt > %USERPROFILE%
            string ws = Environment.GetEnvironmentVariable("DSH_GUI_WORKSPACE");
            if (string.IsNullOrEmpty(ws))
            {
                string cfg = IOPath.Combine(AppDir, "workspace.txt");
                try { if (File.Exists(cfg)) ws = File.ReadAllText(cfg, Encoding.UTF8).Trim(); } catch { }
            }
            if (string.IsNullOrEmpty(ws)) ws = Environment.GetEnvironmentVariable("USERPROFILE");
            if (string.IsNullOrEmpty(ws)) ws = IOPath.GetTempPath();
            Workspace = ws;

            // 端口：环境变量 DSH_GUI_PORT > 默认 3080
            int p;
            string ps = Environment.GetEnvironmentVariable("DSH_GUI_PORT");
            if (!string.IsNullOrEmpty(ps) && int.TryParse(ps.Trim(), out p) && p > 0 && p < 65536) Port = p;
            Url = "http://127.0.0.1:" + Port;
            Log("config: workspace=" + Workspace + " port=" + Port);
        }

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--selftest") return SelfTest();
            try
            {
                Directory.CreateDirectory(BaseDir);
                LoadConfig();
                Log("start, version 1.4.0 (browser-splash mode)");

                if (PortOpen())
                {
                    Log("port already open, open GUI directly");
                    OpenBrowser(Url, false);
                    return 0;
                }

                // 不用 WPF 动画窗口——浏览器 splash 动画就是唯一的启动画布（浏览器现在启动快，避免双窗口抢层级）。
                // 这里只计算浏览器 --app 窗口的居中几何。
                Rect wa = SystemParameters.WorkArea;
                WinLeft = (int)(wa.Left + (wa.Width - WinW) / 2);
                WinTop = (int)(wa.Top + (wa.Height - WinH) / 2);

                // 后台线程负责启动编排；主线程阻塞等它结束（浏览器 GUI 窗口关闭即结束，结束即退出）。
                Thread worker = new Thread(LauncherWorker);
                worker.IsBackground = true;
                worker.Start();
                worker.Join();
                return ExitCode;
            }
            catch (Exception ex)
            {
                Log("ERROR: " + ex);
                try { MessageBox.Show("DSH GUI 启动失败：\n" + ex.Message, "DSH GUI", MessageBoxButton.OK, MessageBoxImage.Error); } catch { }
                return 1;
            }
        }

        static void LauncherWorker()
        {
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                // 清理过期 owner 锁
                try
                {
                    if (File.Exists(LockFile))
                    {
                        int stale = 0;
                        if (int.TryParse(File.ReadAllText(LockFile).Trim(), out stale) && stale > 0)
                        {
                            try { if (Process.GetProcessById(stale) == null) throw new Exception(); }
                            catch { File.Delete(LockFile); Log("removed stale lock " + stale); }
                        }
                    }
                }
                catch (Exception ex) { Log("lock cleanup: " + ex.Message); }

                if (File.Exists(LockFile))
                {
                    // 另一实例正在启动服务：等它就绪即可——owner 的唯一预热窗会自动跳到同源 GUI，
                    // 这里不再开第二个窗口，避免重复。
                    Log("another instance is starting the server");
                    bool ready = WaitPort(90);
                    if (!ready)
                    {
                        UiMessage("DSH web 服务未能就绪，请重试。", "DSH GUI", MessageBoxImage.Warning);
                        FinishApp(1);
                        return;
                    }
                    // 等它把 token 写入共享 ServerOut（token.js 由 owner 写，本进程无需再开窗口）
                    WaitLaunchToken(15);
                    FinishApp(0);
                    return;
                }

                // 轮换服务输出日志：本轮启动只保留本次 run 的行，
                // 防止把上一次 run 残留的 token 行误认成当前服务的 token。
                // 必须在写 owner 锁之前清空：等锁的其它实例从共享日志扫 token 时才不会读到旧行。
                try { File.WriteAllText(ServerOut, ""); } catch { }
                File.WriteAllText(LockFile, Process.GetCurrentProcess().Id.ToString());
                Log("starting dsh web");

                // 尽早打开 file:// splash 预热浏览器，让浏览器冷启动与下面的 dsh 求解 / 服务 boot 重叠（省 1~3s）。
                // wait=token：这个窗口是唯一窗口，轮询同目录 token.js，读到 token 后原地自跳转到同源 splash。
                // 先删掉上一轮遗留的 token.js，避免预热页读到旧进程的失效 token（token 是每进程一次的）。
                try { File.Delete(IOPath.Combine(SplashDir(), "token.js")); } catch { }
                string prewarmUrl = FileSplashUrl("0") + "&wait=token";
                Log("prewarm browser: " + sw.ElapsedMilliseconds + "ms -> " + prewarmUrl);
                OpenBrowser(prewarmUrl, true);
                Log("browser spawned: " + sw.ElapsedMilliseconds + "ms");

                ResolveDshInstall();
                Log("resolve dsh install: " + sw.ElapsedMilliseconds + "ms");
                SyncSplashToDist();
                Log("sync splash to dist: " + sw.ElapsedMilliseconds + "ms");

                Process server = StartServer();
                if (server == null)
                {
                    KillBrowser();
                    UiMessage(LastServerError ?? "无法启动 dsh web（node 或 dsh 未找到）。", "DSH GUI", MessageBoxImage.Error);
                    try { File.Delete(LockFile); } catch { }
                    FinishApp(1);
                    return;
                }
                Log("server process started: " + sw.ElapsedMilliseconds + "ms");

                if (!WaitPort(90))
                {
                    UiMessage("DSH web 服务 90 秒内未就绪，请查看日志：" + ServerErr, "DSH GUI", MessageBoxImage.Error);
                    KillBrowser();
                    KillTree(server.Id);
                    try { File.Delete(LockFile); } catch { }
                    FinishApp(1);
                    return;
                }
                Log("server ready: " + sw.ElapsedMilliseconds + "ms");

                // 等 token 行落盘(最久 15 秒)
                WaitLaunchToken(15);

                // 服务就绪 + token 到手：把 token 写进 splash 同目录的 token.js。
                // 预热窗（唯一窗口）轮询到后原地跳到带 token 的同源 splash——单窗口自跳转，不再"关一个开一个"。
                WriteLaunchTokenJs();
                Log("token.js written: " + sw.ElapsedMilliseconds + "ms");

                // 窗口关闭后停止本次启动的服务
                while (GuiBrowserAlive()) Thread.Sleep(1000);
                Log("window closed, stopping server");
                KillTree(server.Id);
                try { File.Delete(LockFile); } catch { }
                FinishApp(0);
            }
            catch (Exception ex)
            {
                Log("ERROR: " + ex);
                KillBrowser();
                UiMessage("DSH GUI 启动失败：\n" + ex.Message, "DSH GUI", MessageBoxImage.Error);
                FinishApp(1);
            }
        }

        static void UiMessage(string text, string caption, MessageBoxImage image)
        {
            // 无 WPF 消息循环：直接从工作线程弹 MessageBox（Win32 对话框，可在非 UI 线程显示）
            try { MessageBox.Show(text, caption, MessageBoxButton.OK, image); }
            catch (Exception ex) { Log("message failed: " + ex.Message); }
        }

        static void FinishApp(int code)
        {
            ExitCode = code;
            // 无 WPF 消息循环：退出码由 Main 的 worker.Join() 返回（写后 Join 建立 happens-before）
        }

        static bool PortOpen()
        {
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    IAsyncResult r = c.BeginConnect("127.0.0.1", Port, null, null);
                    if (!r.AsyncWaitHandle.WaitOne(300)) return false;
                    c.EndConnect(r);
                    return true;
                }
            }
            catch { return false; }
        }

        static bool WaitPort(int seconds)
        {
            for (int i = 0; i < seconds; i++)
            {
                if (PortOpen()) return true;
                Thread.Sleep(1000);
            }
            return false;
        }

        static string FindBrowser()
        {
            string[] candidates = {
                IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google\\Chrome\\Application\\chrome.exe"),
                IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google\\Chrome\\Application\\chrome.exe"),
                IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google\\Chrome\\Application\\chrome.exe"),
                IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft\\Edge\\Application\\msedge.exe"),
                IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft\\Edge\\Application\\msedge.exe")
            };
            foreach (string p in candidates) if (File.Exists(p)) return p;
            return null;
        }

        static void OpenBrowser(string targetUrl, bool useSplashGeometry)
        {
            string browser = FindBrowser();
            if (browser == null)
            {
                Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true });
                return;
            }
            string args = "--app=\"" + targetUrl + "\" --user-data-dir=\"" + ProfileDir +
                "\" --no-first-run --no-default-browser-check --disable-background-mode --disable-session-crashed-bubble";
            if (useSplashGeometry)
            {
                // 与预热 splash 窗口保持同一尺寸与位置，交接时画面完全重叠
                args += " --window-size=" + (int)WinW + "," + (int)WinH +
                        " --window-position=" + WinLeft + "," + WinTop;
            }
            Log("open window: " + targetUrl);
            Process.Start(new ProcessStartInfo(browser, args) { UseShellExecute = false, CreateNoWindow = true });
        }

        // splash 跳转目标:alpha.3+ 首次进入带 token(服务端换 cookie 后 303 回干净路径),无 token 时退回原行为
        static string SplashTarget()
        {
            return LaunchToken == null ? Url : Url + "?token=" + LaunchToken;
        }

        static string FileSplashUrl(string handoff)
        {
            string file = new Uri(IOPath.Combine(SplashDir(), "splash.html")).AbsoluteUri;
            return file + "?target=" + Uri.EscapeDataString(SplashTarget()) + "&timeout=90&handoff=" + handoff + SplashStyle + ThemeQuery();
        }

        static string SplashDir()
        {
            // 磁盘同目录素材优先（可自定义动画），否则使用 exe 内嵌资源（便携单文件）
            if (File.Exists(IOPath.Combine(AppDir, "splash.html"))) return AppDir;
            string dir = IOPath.Combine(BaseDir, "assets");
            try
            {
                Directory.CreateDirectory(dir);
                foreach (string f in new[] { "splash.html", "deepseek-wordmark.svg", "whale.png", "whale-anim.svg" })
                    Assets.ExtractTo(dir, f);
                Log("assets extracted to " + dir);
            }
            catch (Exception ex) { Log("extract assets failed: " + ex.Message); }
            return dir;
        }

        static string RunCapture(string exe, string args)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                string so = p.StandardOutput.ReadToEnd();
                p.WaitForExit(15000);
                return so.Trim();
            }
        }

        static string ResolveNodeExe()
        {
            try
            {
                string found = RunCapture("cmd.exe", "/c where node")
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrEmpty(found) && File.Exists(found)) { NodeExe = found; return found; }
            }
            catch { }
            NodeExe = "node";
            return "node";
        }

        static string ResolveNpmRoot()
        {
            // 先探测常见全局目录（秒级，避免每次启动都 spawn `npm root -g`——npm 启动慢且可能被杀软扫描）。
            // 找不到才用 npm root -g 兜底。
            string[] candidates = {
                IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules"),
                IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node_modules"),
                IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "npm-global", "node_modules")
            };
            foreach (string c in candidates)
            {
                if (Directory.Exists(IOPath.Combine(c, "@deepseek-ai", "dsh"))) return c;
            }
            try
            {
                string s = RunCapture("cmd.exe", "/c npm root -g");
                string root = s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                if (!string.IsNullOrEmpty(root) && Directory.Exists(root)) return root;
            }
            catch { }
            return "";
        }

        static void ResolveDshInstall()
        {
            // 命中缓存直接复用，把这段从 1.8~26.8s 压到 ~0ms。
            // 升级 dsh 后删除 %LocalAppData%\DSH-GUI\resolved.cache 即重算。
            if (TryLoadResolveCache()) return;
            try
            {
                NpmRoot = ResolveNpmRoot();
                if (string.IsNullOrEmpty(NpmRoot)) throw new Exception("npm root -g empty");
                DshBin = IOPath.Combine(NpmRoot, "@deepseek-ai", "dsh", "lib", "bin.js");
                if (!File.Exists(DshBin)) throw new Exception("dsh bin missing: " + DshBin);

                string node = ResolveNodeExe();
                // dsh >= 0.1.0-rc.7 默认会自动打开系统浏览器；用 --no-open 关掉，
                // 避免和我们自己打开的 Chrome --app 窗口重复。
                // 两个独立的 node 探测并行跑，减少串行等待。
                var tNoOpen = Task.Factory.StartNew<bool>(delegate
                {
                    try
                    {
                        string help = RunCapture(node, "\"" + DshBin + "\" web --help");
                        return help.IndexOf("--no-open", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                    catch { return false; }
                });
                var tDist = Task.Factory.StartNew<string>(delegate
                {
                    try
                    {
                        string script =
                            "const p = require('path');" +
                            "const r = require('module').createRequire(p.join(process.argv[1], '@deepseek-ai', 'dsh', 'lib', 'bin.js'));" +
                            "console.log(p.dirname(r.resolve('@deepseek-ai/dsh-web-frontend/dist/index.html')));";
                        return RunCapture(node, "-e \"" + script + "\" \"" + NpmRoot + "\"")
                            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                    }
                    catch { return (string)null; }
                });
                Task.WaitAll(tNoOpen, tDist);
                NoOpenSupported = tNoOpen.Result;
                DistDir = tDist.Result;

                Log("dsh --no-open supported: " + NoOpenSupported);
                Log("dsh bin: " + DshBin);
                Log("dist: " + DistDir);
                SaveResolveCache();
            }
            catch (Exception ex) { Log("resolve dsh install failed: " + ex.Message); }
        }

        static void SaveResolveCache()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("node=" + NodeExe);
                sb.AppendLine("npmRoot=" + NpmRoot);
                sb.AppendLine("dshBin=" + DshBin);
                sb.AppendLine("distDir=" + DistDir);
                sb.AppendLine("noOpen=" + (NoOpenSupported ? "1" : "0"));
                File.WriteAllText(ResolveCacheFile, sb.ToString(), Encoding.UTF8);
                Log("resolve cache saved");
            }
            catch (Exception ex) { Log("save cache failed: " + ex.Message); }
        }

        static bool TryLoadResolveCache()
        {
            try
            {
                if (!File.Exists(ResolveCacheFile)) return false;
                var map = new Dictionary<string, string>();
                foreach (string line in File.ReadAllLines(ResolveCacheFile))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
                string nm, db, dd;
                if (!map.TryGetValue("npmRoot", out nm) || string.IsNullOrEmpty(nm) || !Directory.Exists(nm)) return false;
                if (!map.TryGetValue("dshBin", out db) || string.IsNullOrEmpty(db) || !File.Exists(db)) return false;
                if (!map.TryGetValue("distDir", out dd) || string.IsNullOrEmpty(dd) || !Directory.Exists(dd)) return false;
                NodeExe = map.ContainsKey("node") && !string.IsNullOrEmpty(map["node"]) ? map["node"] : "node";
                // 缓存的绝对 node 路径已失效（升级/卸载）则作废缓存重算
                if (NodeExe.IndexOf(IOPath.DirectorySeparatorChar) >= 0 && !File.Exists(NodeExe)) return false;
                NoOpenSupported = map.ContainsKey("noOpen") && map["noOpen"] == "1";
                NpmRoot = nm;
                DshBin = db;
                DistDir = dd;
                Log("resolve cache hit");
                return true;
            }
            catch (Exception ex) { Log("resolve cache load failed: " + ex.Message); return false; }
        }

        static void SyncSplashToDist()
        {
            try
            {
                if (string.IsNullOrEmpty(DistDir) || !Directory.Exists(DistDir)) throw new Exception("dist missing");
                foreach (string f in new[] { "splash.html", "deepseek-wordmark.svg", "whale.png", "whale-anim.svg" })
                {
                    Assets.ExtractTo(DistDir, f);
                }
                Log("splash synced to dist");
            }
            catch (Exception ex) { Log("sync failed (fallback file mode): " + ex.Message); }
        }

        static Process StartServer()
        {
            try
            {
                if (!Directory.Exists(Workspace))
                {
                    LastServerError = "工作目录不存在：" + Workspace +
                        "\r\n请在 exe 同目录新建 workspace.txt（或设置环境变量 DSH_GUI_WORKSPACE）指向一个已存在的目录。";
                    Log("workspace missing: " + Workspace);
                    return null;
                }
                string node = string.IsNullOrEmpty(NodeExe) ? ResolveNodeExe() : NodeExe;
                if (string.IsNullOrEmpty(DshBin) || !File.Exists(DshBin))
                {
                    Log("node/dsh unavailable, fallback to cmd");
                    LastServerError = "未在 npm 全局目录找到 @deepseek-ai/dsh，请先执行：npm install -g @deepseek-ai/dsh";
                    ProcessStartInfo fallback = new ProcessStartInfo("cmd.exe", "/c dsh web" + (NoOpenSupported ? " --no-open" : ""));
                    fallback.WorkingDirectory = Workspace;
                    fallback.CreateNoWindow = true;
                    fallback.UseShellExecute = false;
                    fallback.WindowStyle = ProcessWindowStyle.Hidden;
                    return Process.Start(fallback);
                }

                ProcessStartInfo psi = new ProcessStartInfo(node, "\"" + DshBin + "\" web" + (NoOpenSupported ? " --no-open" : ""));
                psi.WorkingDirectory = Workspace;
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                Process p = Process.Start(psi);
                p.OutputDataReceived += (s, e) => { if (e.Data == null) return; AppendLine(ServerOut, e.Data); ExtractLaunchToken(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) AppendLine(ServerErr, e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                return p;
            }
            catch (Exception ex)
            {
                Log("start server failed: " + ex.Message);
                return null;
            }
        }

        static void AppendLine(string path, string line)
        {
            try { File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8); } catch { }
        }

        // alpha.3+ 的 dsh web 会打印 "dsh web: http://127.0.0.1:3080/?token=xxx";
        // 截取 token(已 URL 编码)供 splash target 注入。旧版 dsh 无此行,保持 null 走原路径。
        static void ExtractLaunchToken(string line)
        {
            if (LaunchToken != null || line == null) return;
            int at = line.IndexOf("dsh web:", StringComparison.Ordinal);
            if (at < 0) return;
            string url = line.Substring(at + 8).Trim();
            int q = url.IndexOf('?');
            if (q < 0) return;
            foreach (string part in url.Substring(q + 1).Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string val = part.Substring(eq + 1);
                if (part.Substring(0, eq) == "token" && val.Length > 0)
                {
                    LaunchToken = val;
                    Log("launch token captured");
                    return;
                }
            }
        }

        // 端口就绪后 token 行可能稍后才打印(树收敛后输出);轮询等待,最长 seconds 秒。
        // 另一实例启动服务时本进程不捕获其 stdout,从共享的 ServerOut 日志读。
        static void WaitLaunchToken(int seconds)
        {
            for (int i = 0; i < seconds * 5 && LaunchToken == null; i++)
            {
                try
                {
                    if (File.Exists(ServerOut))
                        foreach (string line in File.ReadLines(ServerOut)) ExtractLaunchToken(line);
                }
                catch { }
                if (LaunchToken == null) Thread.Sleep(200);
            }
            if (LaunchToken == null) Log("launch token not seen (older dsh without token auth?)");
        }

        // 把 token 写成 splash 同目录的 token.js；预热窗(唯一窗口)轮询到后原地自跳到带 token 的同源 splash。
        static void WriteLaunchTokenJs()
        {
            try
            {
                if (string.IsNullOrEmpty(LaunchToken)) { Log("no launch token; token.js not written"); return; }
                string dir = SplashDir();
                File.WriteAllText(IOPath.Combine(dir, "token.js"),
                    "window.__DSH_TOKEN='" + EscapeJs(LaunchToken) + "';\n", Encoding.UTF8);
                Log("token.js -> " + dir);
            }
            catch (Exception ex) { Log("write token.js failed: " + ex.Message); }
        }

        static string EscapeJs(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("'", "\\'");
        }

        static void KillTree(int pid)
        {
            if (pid <= 0) return;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("taskkill.exe", "/PID " + pid + " /T /F");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                using (Process p = Process.Start(psi)) { p.WaitForExit(5000); }
                Log("stopped tree " + pid);
            }
            catch (Exception ex) { Log("kill failed: " + ex.Message); }
        }

        static void KillBrowser()
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='msedge.exe' OR Name='chrome.exe'"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        string cl = mo["CommandLine"] as string;
                        if (cl != null && cl.Contains("DSH-GUI"))
                        {
                            uint pid = (uint)mo["ProcessId"];
                            try
                            {
                                ProcessStartInfo psi = new ProcessStartInfo("taskkill.exe", "/PID " + pid + " /T /F");
                                psi.CreateNoWindow = true;
                                psi.UseShellExecute = false;
                                psi.WindowStyle = ProcessWindowStyle.Hidden;
                                using (Process p = Process.Start(psi)) { p.WaitForExit(3000); }
                            }
                            catch { }
                        }
                    }
                }
                Log("stopped prewarmed browser");
            }
            catch (Exception ex) { Log("kill browser failed: " + ex.Message); }
        }

        static bool GuiBrowserAlive()
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT CommandLine FROM Win32_Process WHERE Name='msedge.exe' OR Name='chrome.exe'"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        string cl = mo["CommandLine"] as string;
                        if (cl != null && cl.Contains("DSH-GUI")) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        internal static void Log(string message)
        {
            try
            {
                File.AppendAllText(LogFile, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        static int SelfTest()
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                // 校验内嵌素材可读取（WPF 几何解析已移除，只核对素材字节数）
                sb.AppendLine("splash.html=" + Assets.ReadText("splash.html").Length + "B, deepseek-wordmark.svg=" + Assets.ReadText("deepseek-wordmark.svg").Length + "B");
                sb.AppendLine("embedded resources: " + string.Join(",", typeof(Program).Assembly.GetManifestResourceNames()));
                ResolveDshInstall();
                sb.AppendLine("dshBin=" + DshBin);
                sb.AppendLine("dist=" + DistDir);
                if (string.IsNullOrEmpty(DshBin) || !File.Exists(DshBin))
                    sb.AppendLine("WARNING: dsh 未找到（npm root -g 解析失败）；请确认已执行 npm install -g @deepseek-ai/dsh");
                sb.AppendLine("dshTheme=" + ReadDshThemePreference());
                sb.AppendLine("selftest OK");
                try { File.WriteAllText(IOPath.Combine(AppDir, "selftest.txt"), sb.ToString(), Encoding.UTF8); } catch { }
                return 0;
            }
            catch (Exception ex)
            {
                sb.AppendLine("selftest FAILED: " + ex.Message);
                try { File.WriteAllText(IOPath.Combine(AppDir, "selftest.txt"), sb.ToString(), Encoding.UTF8); } catch { }
                return 1;
            }
        }
    }

    public static class Assets
    {
        // 读取素材：优先 exe 同目录磁盘文件（可自定义），否则使用内嵌资源（便携单文件）
        public static Stream Open(string name)
        {
            string disk = IOPath.Combine(Program.AppDir, name);
            if (File.Exists(disk)) return File.OpenRead(disk);
            Stream s = typeof(Program).Assembly.GetManifestResourceStream("DshGui." + name);
            if (s == null) throw new Exception("asset not found (disk and embedded): " + name);
            return s;
        }

        public static string ReadText(string name)
        {
            using (Stream s = Open(name))
            using (StreamReader r = new StreamReader(s, Encoding.UTF8))
            {
                return r.ReadToEnd();
            }
        }

        public static void ExtractTo(string dir, string name)
        {
            using (Stream s = Open(name))
            using (FileStream fs = new FileStream(IOPath.Combine(dir, name), FileMode.Create, FileAccess.Write))
            {
                s.CopyTo(fs);
            }
        }

    }

}