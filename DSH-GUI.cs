// DSH GUI 一键式启动器（C# / WPF，无需 PowerShell）
// 双击即显示 WPF 即时启动动画；后台启动 dsh web；
// 服务就绪后打开浏览器同源 splash，由页面后台加载真实 GUI 并在就绪后淡入。
// 开源友好：单个 .cs 文件即可用 Windows 自带 csc.exe 编译。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Sockets;
using Microsoft.Win32;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using IOPath = System.IO.Path;
using ShapePath = System.Windows.Shapes.Path;

namespace DshGui
{
    public static class Program
    {
        static string Workspace = "";
        static int Port = 3080;
        static string Url = "http://127.0.0.1:3080";
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
        static bool ServedOk = false;
        static string LastServerError = null;
        static SplashWindow Splash = null;
        static Application UiApp = null;
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
                Log("start, version 1.3");

                if (PortOpen())
                {
                    Log("port already open, open GUI directly");
                    OpenBrowser(Url, false);
                    return 0;
                }

                // 立刻显示 WPF 即时启动动画；UI 线程只负责动画，后台线程负责启动编排
                Assets.Load();
                Rect wa = SystemParameters.WorkArea;
                WinLeft = (int)(wa.Left + (wa.Width - WinW) / 2);
                WinTop = (int)(wa.Top + (wa.Height - WinH) / 2);

                Splash = new SplashWindow(WinW, WinH);
                Splash.Left = WinLeft;
                Splash.Top = WinTop;

                UiApp = new Application();
                UiApp.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Splash.Show();

                Thread worker = new Thread(LauncherWorker);
                worker.IsBackground = true;
                worker.Start();

                UiApp.Run();
                return ExitCode;
            }
            catch (Exception ex)
            {
                Log("ERROR: " + ex);
                try { MessageBox.Show("DSH GUI 启动失败：\n" + ex.Message, "DSH GUI", MessageBoxButton.OK, MessageBoxImage.Error); } catch { }
                return 1;
            }
        }

        static bool BrowserTitleVisible()
        {
            foreach (string name in new[] { "msedge", "chrome" })
            {
                foreach (Process p in Process.GetProcessesByName(name))
                {
                    try { if (p.MainWindowTitle == "DSH") return true; }
                    catch { }
                }
            }
            return false;
        }

        static void WaitBrowserTitle(int seconds)
        {
            for (int i = 0; i < seconds * 5; i++)
            {
                if (BrowserTitleVisible()) return;
                Thread.Sleep(200);
            }
        }

        static void LauncherWorker()
        {
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                ResolveDshInstall();
                Log("resolve dsh install: " + sw.ElapsedMilliseconds + "ms");
                SyncSplashToDist();
                Log("sync splash to dist: " + sw.ElapsedMilliseconds + "ms");

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
                    // 另一实例正在启动服务：等待其就绪后只打开窗口
                    Log("another instance is starting the server");
                    bool ready = WaitPort(90);
                    if (!ready)
                    {
                        UiMessage("DSH web 服务未能就绪，请重试。", "DSH GUI", MessageBoxImage.Warning);
                        FinishApp(1);
                        return;
                    }
                    OpenBrowser(ServedOk ? ServedSplashUrl() : FileSplashUrl("0"), true);
                    WaitBrowserTitle(8);   // 等浏览器窗口出现、WPF 动画自行淡出后再退出
                    FinishApp(0);
                    return;
                }

                File.WriteAllText(LockFile, Process.GetCurrentProcess().Id.ToString());
                Log("starting dsh web");

                Process server = StartServer();
                if (server == null)
                {
                    UiMessage(LastServerError ?? "无法启动 dsh web（node 或 dsh 未找到）。", "DSH GUI", MessageBoxImage.Error);
                    try { File.Delete(LockFile); } catch { }
                    FinishApp(1);
                    return;
                }
                Log("server process started: " + sw.ElapsedMilliseconds + "ms");

                // 提前打开 file:// splash，让浏览器冷启动与 DSH 服务等待重叠
                string prewarmUrl = FileSplashUrl(ServedOk ? "1" : "0");
                Log("prewarm browser: " + sw.ElapsedMilliseconds + "ms -> " + prewarmUrl);
                OpenBrowser(prewarmUrl, true);
                Log("browser spawned: " + sw.ElapsedMilliseconds + "ms");

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

                // 不依赖 file:// splash 的自动跳转（file:// 探测 http 端口在 Chrome/Edge 里不可靠），
                // 端口就绪后主动关掉预热窗口，再打开正式的同源 splash。
                ClosePrewarmBrowser();
                OpenBrowser(ServedOk ? ServedSplashUrl() : FileSplashUrl("0"), true);
                Log("browser handoff ready: " + sw.ElapsedMilliseconds + "ms");

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
                UiMessage("DSH GUI 启动失败：\n" + ex.Message, "DSH GUI", MessageBoxImage.Error);
                FinishApp(1);
            }
        }

        static void UiMessage(string text, string caption, MessageBoxImage image)
        {
            try
            {
                UiApp.Dispatcher.Invoke(new Action(delegate { MessageBox.Show(text, caption, MessageBoxButton.OK, image); }));
            }
            catch (Exception ex) { Log("message failed: " + ex.Message); }
        }

        static void FinishApp(int code)
        {
            ExitCode = code;
            try
            {
                UiApp.Dispatcher.BeginInvoke(new Action(delegate { UiApp.Shutdown(); }));
            }
            catch (Exception ex) { Log("shutdown failed: " + ex.Message); }
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
                // 与 WPF 动画窗口保持同一尺寸与位置，交接时画面完全重叠
                args += " --window-size=" + (int)WinW + "," + (int)WinH +
                        " --window-position=" + WinLeft + "," + WinTop;
            }
            Log("open window: " + targetUrl);
            Process.Start(new ProcessStartInfo(browser, args) { UseShellExecute = false, CreateNoWindow = true });
        }

        static string ServedSplashUrl()
        {
            return Url + "/splash.html?hold=1&target=%2F&timeout=60" + SplashStyle + ThemeQuery();
        }

        static string FileSplashUrl(string handoff)
        {
            string file = new Uri(IOPath.Combine(SplashDir(), "splash.html")).AbsoluteUri;
            return file + "?target=" + Uri.EscapeDataString(Url) + "&timeout=90&handoff=" + handoff + SplashStyle + ThemeQuery();
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
                if (!string.IsNullOrEmpty(found) && File.Exists(found)) return found;
            }
            catch { }
            return "node";
        }

        static string ResolveNpmRoot()
        {
            try
            {
                string s = RunCapture("cmd.exe", "/c npm root -g");
                string root = s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                if (!string.IsNullOrEmpty(root) && Directory.Exists(root)) return root;
            }
            catch { }
            // 兜底：常见全局安装位置
            string[] candidates = {
                IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules"),
                IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node_modules"),
                IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "npm-global", "node_modules")
            };
            foreach (string c in candidates)
            {
                if (Directory.Exists(IOPath.Combine(c, "@deepseek-ai", "dsh"))) return c;
            }
            return "";
        }

        static void ResolveDshInstall()
        {
            try
            {
                NpmRoot = ResolveNpmRoot();
                if (string.IsNullOrEmpty(NpmRoot)) throw new Exception("npm root -g empty");
                DshBin = IOPath.Combine(NpmRoot, "@deepseek-ai", "dsh", "lib", "bin.js");
                if (!File.Exists(DshBin)) throw new Exception("dsh bin missing: " + DshBin);

                string node = ResolveNodeExe();
                string script =
                    "const p = require('path');" +
                    "const r = require('module').createRequire(p.join(process.argv[1], '@deepseek-ai', 'dsh', 'lib', 'bin.js'));" +
                    "console.log(p.dirname(r.resolve('@deepseek-ai/dsh-web-frontend/dist/index.html')));";
                DistDir = RunCapture(node, "-e \"" + script + "\" \"" + NpmRoot + "\"")
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                Log("dsh bin: " + DshBin);
                Log("dist: " + DistDir);
            }
            catch (Exception ex) { Log("resolve dsh install failed: " + ex.Message); }
        }

        static void SyncSplashToDist()
        {
            ServedOk = false;
            try
            {
                if (string.IsNullOrEmpty(DistDir) || !Directory.Exists(DistDir)) throw new Exception("dist missing");
                foreach (string f in new[] { "splash.html", "deepseek-wordmark.svg", "whale.png", "whale-anim.svg" })
                {
                    Assets.ExtractTo(DistDir, f);
                }
                ServedOk = true;
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
                string node = ResolveNodeExe();
                if (string.IsNullOrEmpty(DshBin) || !File.Exists(DshBin))
                {
                    Log("node/dsh unavailable, fallback to cmd");
                    LastServerError = "未在 npm 全局目录找到 @deepseek-ai/dsh，请先执行：npm install -g @deepseek-ai/dsh";
                    ProcessStartInfo fallback = new ProcessStartInfo("cmd.exe", "/c dsh web");
                    fallback.WorkingDirectory = Workspace;
                    fallback.CreateNoWindow = true;
                    fallback.UseShellExecute = false;
                    fallback.WindowStyle = ProcessWindowStyle.Hidden;
                    return Process.Start(fallback);
                }

                ProcessStartInfo psi = new ProcessStartInfo(node, "\"" + DshBin + "\" web");
                psi.WorkingDirectory = Workspace;
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                Process p = Process.Start(psi);
                p.OutputDataReceived += (s, e) => { if (e.Data != null) AppendLine(ServerOut, e.Data); };
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

        static void ClosePrewarmBrowser()
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='msedge.exe' OR Name='chrome.exe'"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        string cl = mo["CommandLine"] as string;
                        // 只关预热窗口：file:// splash 带 handoff=1；正式窗口是 http:// 同源 splash
                        if (cl != null && cl.Contains("DSH-GUI") && cl.Contains("file:///") && cl.Contains("handoff=1"))
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
                Log("closed prewarm browser");
            }
            catch (Exception ex) { Log("close prewarm browser failed: " + ex.Message); }
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
                Assets.Load();
                sb.AppendLine("assets OK: letters=" + Assets.Letters.Count + " wordmark=" + Assets.Wordmark.Count);
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
        public static Geometry Whale;
        public static List<Geometry> Wordmark = new List<Geometry>();
        public static List<Geometry> Letters = new List<Geometry>();
        public static List<double> LetterLengths = new List<double>();
        public static List<List<Geometry>> LetterFigures = new List<List<Geometry>>();
        public static List<List<double>> LetterFigureLengths = new List<List<double>>();

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

        public static void Load()
        {
            Whale = null;
            Wordmark.Clear();
            Letters.Clear();
            LetterLengths.Clear();
            LetterFigures.Clear();
            LetterFigureLengths.Clear();

            string whaleSvg = ReadText("whale-anim.svg");
            Match wm = Regex.Match(whaleSvg, "<path class=\"ink\" d=\"([^\"]+)\"");
            if (!wm.Success) throw new Exception("whale path not found");
            string whaleD = Normalize(wm.Groups[1].Value);
            Whale = Geometry.Parse(whaleD);

            string wordmark = ReadText("deepseek-wordmark.svg");
            foreach (Match m in Regex.Matches(wordmark, "<path d=\"([^\"]+)\""))
            {
                Wordmark.Add(Geometry.Parse(m.Groups[1].Value));
            }
            if (Wordmark.Count != 9) throw new Exception("wordmark path count=" + Wordmark.Count);

            string html = ReadText("splash.html");
            MatchCollection letterMatches = Regex.Matches(html, "<path class=\"letter\"[^>]*d=\"([^\"]+)\"");
            if (letterMatches.Count != 7) throw new Exception("letter count=" + letterMatches.Count);
            foreach (Match m in letterMatches)
            {
                Geometry g = Geometry.Parse(m.Groups[1].Value);
                Letters.Add(g);
                PathGeometry pg = PathGeometry.CreateFromGeometry(g);
                List<Geometry> figs = new List<Geometry>();
                List<double> lens = new List<double>();
                double total = 0;
                foreach (PathFigure f in pg.Figures)
                {
                    PathGeometry one = new PathGeometry();
                    one.Figures.Add(f);
                    double l = PathLength(one);
                    figs.Add(one);
                    lens.Add(l);
                    total += l;
                }
                LetterFigures.Add(figs);
                LetterFigureLengths.Add(lens);
                LetterLengths.Add(total);
            }
        }

        static string Normalize(string d)
        {
            string d2 = Regex.Replace(d, "(?<![eE])(?<=\\d)-", " -");
            return Regex.Replace(d2, "(?<![eE])(?<=\\.)-", " -");
        }

        public static double PathLength(Geometry geo)
        {
            double len = 0;
            PathGeometry flat = geo.GetFlattenedPathGeometry(0.05, ToleranceType.Relative);
            foreach (PathFigure fig in flat.Figures)
            {
                Point prev = fig.StartPoint;
                foreach (PathSegment seg in fig.Segments)
                {
                    if (seg is LineSegment)
                    {
                        LineSegment ls = (LineSegment)seg;
                        len += Dist(prev, ls.Point);
                        prev = ls.Point;
                    }
                    else if (seg is PolyLineSegment)
                    {
                        PolyLineSegment pl = (PolyLineSegment)seg;
                        foreach (Point pt in pl.Points)
                        {
                            len += Dist(prev, pt);
                            prev = pt;
                        }
                    }
                }
            }
            return len;
        }

        static double Dist(Point a, Point b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }

    public class SplashWindow : Window
    {
        const string BrowserTitle = "DSH";
        const string AppTitle = "DeepSeek Harness";
        const double MinShowSec = 2.0;
        const double MaxLifeSec = 45;
        const double DrawSeconds = 0.22;   // 单字母描边时长
        const double LetterStep = 0.14;    // 相邻字母起笔间隔（小于 DrawSeconds，重叠连续运笔）

        class FigureInfo
        {
            public ShapePath Path;
            public double Len;
            public double Acc;
            public double Total;
            public double Delay;
        }

        readonly List<FigureInfo> drawFigures = new List<FigureInfo>();

        // 跟随 Windows 应用深浅色：HKCU\...\Themes\Personalize\AppsUseLightTheme
        static bool IsSystemLightTheme()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        object v = key.GetValue("AppsUseLightTheme");
                        if (v != null) return Convert.ToInt32(v) == 1;
                    }
                }
            }
            catch { }
            return false;   // 默认深色
        }

        // 跟随 dsh 外观设置：light/dark/system；system 时再跟随 Windows 应用深浅色
        static bool IsLightTheme()
        {
            string pref = Program.ReadDshThemePreference();
            if (pref == "light") return true;
            if (pref == "dark") return false;
            return IsSystemLightTheme();
        }

        public SplashWindow(double width = 960, double height = 640)
        {
            Width = width;
            Height = height;
            WindowStyle = WindowStyle.None;   // 无边框，启动期间像一张全屏动画卡
            AllowsTransparency = false;
            Background = new SolidColorBrush(C(IsLightTheme() ? "#f4f8ff" : "#070b18"));
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Title = BrowserTitle;
            Content = BuildRoot();
        }

        static Color C(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }

        FrameworkElement BuildRoot()
        {
            Grid root = new Grid();
            bool light = IsLightTheme();

            Rectangle bg = new Rectangle();
            LinearGradientBrush bgBrush = new LinearGradientBrush();
            bgBrush.StartPoint = new Point(0, 0);
            bgBrush.EndPoint = new Point(0, 1);
            bgBrush.GradientStops.Add(new GradientStop(C(light ? "#f4f8ff" : "#070b18"), 0));
            bgBrush.GradientStops.Add(new GradientStop(C(light ? "#eaf2ff" : "#0a1024"), 0.55));
            bgBrush.GradientStops.Add(new GradientStop(C(light ? "#f4f8ff" : "#070b18"), 1));
            bg.Fill = bgBrush;
            root.Children.Add(bg);

            root.Children.Add(Blob(560, "#4d6bfe", 0.30, HorizontalAlignment.Left, VerticalAlignment.Top, new Thickness(-160, -140, 0, 0)));
            root.Children.Add(Blob(580, "#22d3ee", 0.18, HorizontalAlignment.Right, VerticalAlignment.Bottom, new Thickness(0, 0, -200, -220)));

            StackPanel stage = new StackPanel();
            stage.VerticalAlignment = VerticalAlignment.Center;
            stage.HorizontalAlignment = HorizontalAlignment.Center;
            root.Children.Add(stage);

            // 鲸鱼：升起 + 呼吸闪烁
            Canvas whaleOuter = new Canvas { Width = 72, Height = 72, HorizontalAlignment = HorizontalAlignment.Center };
            Canvas whaleInner = new Canvas { Width = 72, Height = 72 };
            ShapePath whalePath = new ShapePath
            {
                Data = Assets.Whale,
                Fill = new SolidColorBrush(C(light ? "#1b2a5b" : "#ffffff")),
                RenderTransform = new ScaleTransform(72.0 / 50.0, 72.0 / 50.0)
            };
            whaleInner.Children.Add(whalePath);
            whaleOuter.Children.Add(whaleInner);
            stage.Children.Add(whaleOuter);

            CubicEase easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
            TranslateTransform whaleRise = new TranslateTransform(0, 12);
            whaleOuter.RenderTransform = whaleRise;
            whaleOuter.Opacity = 0;
            Animate(whaleOuter, UIElement.OpacityProperty, 0, 1, 0.5, 0, easeOut, false);
            Animate(whaleRise, TranslateTransform.YProperty, 12, 0, 0.5, 0, easeOut, false);
            DoubleAnimation flicker = new DoubleAnimation(1, 0.6, TimeSpan.FromSeconds(0.9));
            flicker.AutoReverse = true;
            flicker.RepeatBehavior = RepeatBehavior.Forever;
            flicker.BeginTime = TimeSpan.FromSeconds(0.5);
            whaleInner.BeginAnimation(UIElement.OpacityProperty, flicker);

            // 品牌行：deepseek | HARNESS
            StackPanel brandRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 18, 0, 0)
            };
            stage.Children.Add(brandRow);

            Canvas wordCanvas = new Canvas { Width = 234, Height = 58, Margin = new Thickness(0, 4, 0, 0) };
            double wordScale = 234.0 / 97.0;
            TransformGroup wordGroup = new TransformGroup();
            // 先缩放再平移：与 SVG viewBox="26 0 97 24" 的裁剪语义一致，
            // 否则 deepseek 字标会被错误地裁掉左半边、整体偏左
            wordGroup.Children.Add(new ScaleTransform(wordScale, 58.0 / 24.0));
            wordGroup.Children.Add(new TranslateTransform(-26 * wordScale, 0));
            foreach (Geometry wg in Assets.Wordmark)
            {
                wordCanvas.Children.Add(new ShapePath { Data = wg, Fill = new SolidColorBrush(C(light ? "#1b2a5b" : "#E7ECFF")), RenderTransform = wordGroup });
            }
            wordCanvas.Opacity = 0;
            TranslateTransform wordRise = new TranslateTransform(0, 12);
            wordCanvas.RenderTransform = wordRise;
            brandRow.Children.Add(wordCanvas);
            Animate(wordCanvas, UIElement.OpacityProperty, 0, 1, 0.5, 0.1, easeOut, false);
            Animate(wordRise, TranslateTransform.YProperty, 12, 0, 0.5, 0.1, easeOut, false);

            Canvas harnessCanvas = new Canvas { Width = 310, Height = 62, Margin = new Thickness(14, 0, 0, 0) };
            ScaleTransform harnessScale = new ScaleTransform(310.0 / 700.0, 62.0 / 140.0);
            LinearGradientBrush hGrad = new LinearGradientBrush();
            // 与 splash.html 的 userSpaceOnUse 渐变一致：整行 HARNESS 共享一条从左到右的渐变
            hGrad.MappingMode = BrushMappingMode.Absolute;
            hGrad.StartPoint = new Point(0, 20);
            hGrad.EndPoint = new Point(700, 120);
            hGrad.GradientStops.Add(new GradientStop(C(light ? "#5b7cfa" : "#a7c0ff"), 0));
            hGrad.GradientStops.Add(new GradientStop(C("#4d6bfe"), 0.5));
            hGrad.GradientStops.Add(new GradientStop(C(light ? "#0e9bb8" : "#22d3ee"), 1));

            for (int i = 0; i < Assets.Letters.Count; i++)
            {
                double total = Assets.LetterLengths[i];
                double acc = 0;
                for (int j = 0; j < Assets.LetterFigures[i].Count; j++)
                {
                    // 每个子路径一段 dash；由 OnContentRendered 的绘制计时器统一推进，
                    // 用整字母的 CSS ease 曲线换算“笔尖已走距离”，与浏览器 SVG 版手感一致
                    ShapePath p = new ShapePath
                    {
                        Data = Assets.LetterFigures[i][j],
                        RenderTransform = harnessScale,
                        Stroke = hGrad,
                        StrokeThickness = 6,
                        StrokeStartLineCap = PenLineCap.Square,
                        StrokeEndLineCap = PenLineCap.Square,
                        StrokeLineJoin = PenLineJoin.Round
                    };
                    double l = Assets.LetterFigureLengths[i][j];
                    p.StrokeDashArray = new DoubleCollection { l };
                    p.StrokeDashOffset = 1.1 * l;   // 首帧完全隐藏，不露起始点
                    harnessCanvas.Children.Add(p);
                    drawFigures.Add(new FigureInfo { Path = p, Len = l, Acc = acc, Total = total, Delay = 0.20 + i * LetterStep });
                    acc += l;
                }
            }
            brandRow.Children.Add(harnessCanvas);

            TextBlock footer = new TextBlock
            {
                Text = "DeepSeek Harness · DSH GUI",
                FontSize = 12,
                Foreground = new SolidColorBrush(C(light ? "#5a6a94" : "#8b96bb")),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 32),
                Opacity = 0
            };
            root.Children.Add(footer);
            Animate(footer, UIElement.OpacityProperty, 0, 0.4, 0.5, 0.36, easeOut, false);

            return root;
        }

        static void Animate(UIElement target, DependencyProperty prop, double from, double to, double sec, double delay, IEasingFunction ease, bool repeat)
        {
            DoubleAnimation a = new DoubleAnimation(from, to, TimeSpan.FromSeconds(sec));
            a.EasingFunction = ease;
            a.BeginTime = TimeSpan.FromSeconds(delay);
            a.FillBehavior = FillBehavior.HoldEnd;
            if (repeat) a.RepeatBehavior = RepeatBehavior.Forever;
            target.BeginAnimation(prop, a);
        }

        static void Animate(Animatable target, DependencyProperty prop, double from, double to, double sec, double delay, IEasingFunction ease, bool repeat)
        {
            DoubleAnimation a = new DoubleAnimation(from, to, TimeSpan.FromSeconds(sec));
            a.EasingFunction = ease;
            a.BeginTime = TimeSpan.FromSeconds(delay);
            a.FillBehavior = FillBehavior.HoldEnd;
            if (repeat) a.RepeatBehavior = RepeatBehavior.Forever;
            target.BeginAnimation(prop, a);
        }

        static FrameworkElement Blob(double size, string colorHex, double opacity,
            HorizontalAlignment halign, VerticalAlignment valign, Thickness margin)
        {
            Color c = C(colorHex);
            RadialGradientBrush brush = new RadialGradientBrush();
            brush.GradientStops.Add(new GradientStop(c, 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
            Ellipse e = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = brush,
                Opacity = opacity,
                Effect = new BlurEffect { Radius = 40 },
                HorizontalAlignment = halign,
                VerticalAlignment = valign,
                Margin = margin
            };
            return e;
        }

        // CSS ease = cubic-bezier(0.25, 0.1, 0.25, 1)，与 splash.html 的 stroke 动画同款
        static double CssEase(double x)
        {
            double x1 = 0.25, y1 = 0.1, x2 = 0.25, y2 = 1.0;
            double t = x;
            for (int i = 0; i < 8; i++)
            {
                double bx = 3 * (1 - t) * (1 - t) * t * x1 + 3 * (1 - t) * t * t * x2 + t * t * t;
                double dx = 3 * (1 - t) * (1 - t) * x1 + 6 * (1 - t) * t * (x2 - x1) + 3 * t * t * (1 - x2);
                double err = bx - x;
                if (Math.Abs(err) < 1e-6) break;
                if (Math.Abs(dx) < 1e-6) break;
                t -= err / dx;
            }
            t = Math.Max(0, Math.Min(1, t));
            return 3 * (1 - t) * (1 - t) * t * y1 + 3 * (1 - t) * t * t * y2 + t * t * t;
        }

        static double Clamp01(double v) { return Math.Max(0, Math.Min(1, v)); }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            DateTime started = DateTime.Now;
            bool closing = false;

            // 16ms 绘制计时器：统一推进每个子路径的 dashoffset，笔尖连续、无接力生硬感
            DispatcherTimer drawTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            drawTimer.Tick += delegate
            {
                if (closing) return;
                double elapsed = (DateTime.Now - started).TotalSeconds;
                foreach (FigureInfo fig in drawFigures)
                {
                    double p = Clamp01((elapsed - fig.Delay) / DrawSeconds);
                    double l = fig.Len;
                    if (p <= 0)
                    {
                        fig.Path.StrokeDashOffset = 1.1 * l;
                        continue;
                    }
                    double s = p * fig.Total;   // 本字母“笔尖”走过的总长度（linear 线性速度）
                    double x = s - fig.Acc;              // 本段内走过的长度
                    if (x <= 0)
                    {
                        fig.Path.StrokeDashOffset = 1.05 * l;   // 尚未轮到：完全隐藏
                    }
                    else if (x >= l)
                    {
                        fig.Path.StrokeDashOffset = 0.8 * l;    // 已走完：完整显示
                    }
                    else
                    {
                        fig.Path.StrokeDashOffset = l - x / 5.0; // 笔尖正在本段内前进
                    }
                }
            };
            drawTimer.Start();

            // 浏览器窗口真正加载完 GUI 后（标题变为 DeepSeek Harness）再淡出
            DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            timer.Tick += delegate
            {
                if (closing) return;
                double elapsed = (DateTime.Now - started).TotalSeconds;
                bool appUp = false;
                foreach (string name in new[] { "msedge", "chrome" })
                {
                    foreach (Process p in Process.GetProcessesByName(name))
                    {
                        try
                        {
                            string t = p.MainWindowTitle;
                            // 应用标题形如 "新对话 - DeepSeek Harness"，splash 阶段则是 "DSH"
                            if (!string.IsNullOrEmpty(t) && t != BrowserTitle && t.Contains(AppTitle))
                            {
                                appUp = true;
                                break;
                            }
                        }
                        catch { }
                    }
                    if (appUp) break;
                }
                if ((appUp && elapsed >= MinShowSec) || elapsed >= MaxLifeSec)
                {
                    if (appUp)
                        Program.Log("splash appUp after " + elapsed.ToString("0.00") + "s");
                    else
                        Program.Log("splash maxlife reached after " + elapsed.ToString("0.00") + "s");
                    closing = true;
                    drawTimer.Stop();
                    timer.Stop();
                    DoubleAnimation fade = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.25));
                    fade.Completed += delegate { Close(); };
                    BeginAnimation(Window.OpacityProperty, fade);
                }
            };
            timer.Start();
        }
    }
}
