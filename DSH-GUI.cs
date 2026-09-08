// DSH GUI 一键式启动器（C#，无需 PowerShell）
// WebView2 宿主版：不再丢给 Chrome，而是启动器自己开一个窗口（自绘标题栏 + 黑鲸图标），
// 内嵌 WebView2 渲染 splash 动画与真实 GUI。窗口是启动器自己的：
//   - 任务栏图标从窗口出现那一刻就是黑鲸（不再闪 Chrome/Edge 默认图标）；
//   - 标题栏颜色可完全自定义（随 dsh 外观偏好深浅色）。
// 后台拉起 dsh web；服务就绪后写入 token.js，splash 自我跳转到同源 GUI；关闭窗口停服务。
// 开源友好：单个 .cs 文件即可用 Windows 自带 csc.exe 编译（需引用嵌入式 WebView2 SDK）。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using WPath = System.Windows.Shapes.Path;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IOPath = System.IO.Path;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

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
        static readonly string LogFile = IOPath.Combine(BaseDir, "launcher.log");
        static readonly string ServerOut = IOPath.Combine(BaseDir, "server.out.log");
        static readonly string ServerErr = IOPath.Combine(BaseDir, "server.err.log");
        static readonly string LockFile = IOPath.Combine(BaseDir, "owner.lock");
        // 窗口形态记忆（只记“缩小 / 最大化”二选一，不记尺寸与位置）
        static readonly string WindowStateFile = IOPath.Combine(BaseDir, "window.state");

        // WebView2 运行时：SDK 托管 DLL 与原生 loader 内嵌于 exe，启动时释放到 bin 目录并加载
        static readonly string Wv2BinDir = IOPath.Combine(BaseDir, "bin");
        internal static readonly string Wv2UserDataFolder = IOPath.Combine(BaseDir, "webview2");

        static string NpmRoot = "";
        static string DshBin = "";
        static string DistDir = "";
        static string NodeExe = "";
        // 路径解析结果缓存：升级 dsh 或删除该文件后会自动重新求解
        static readonly string ResolveCacheFile = IOPath.Combine(BaseDir, "resolved.cache");
        static bool NoOpenSupported = false;
        static string LastServerError = null;
        static int ExitCode = 0;
        internal static double WinW = 1100;
        internal static double WinH = 720;

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

        // ---- 窗口形态记忆：上次关闭是“缩小”还是“最大化”，下次启动直接按该形态打开 ----
        // 只区分两态（默认缩小窗口 / 最大化），不记忆具体长宽与坐标；分屏、自定义尺寸一律按缩小态处理。
        internal static bool ReadWindowMaximized()
        {
            try
            {
                if (!File.Exists(WindowStateFile)) return false;
                string s = File.ReadAllText(WindowStateFile).Trim().ToLowerInvariant();
                return s == "maximized" || s == "max";
            }
            catch { return false; }
        }

        internal static void SaveWindowMaximized(bool maximized)
        {
            try
            {
                Directory.CreateDirectory(BaseDir);
                string s = maximized ? "maximized" : "normal";
                File.WriteAllText(WindowStateFile, s, Encoding.UTF8);
                Log("window state saved: " + s);
            }
            catch (Exception ex) { Log("save window state failed: " + ex.Message); }
        }

        internal static string WindowStateText()
        {
            try
            {
                if (!File.Exists(WindowStateFile)) return "(unset -> normal)";
                string s = File.ReadAllText(WindowStateFile).Trim();
                return s.Length == 0 ? "(empty -> normal)" : s;
            }
            catch { return "(unreadable -> normal)"; }
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
                SetupWv2Runtime();
                Log("start, version 2.0.1 (webview2 host)");

                var app = new Application();
                var win = new DshWindow();

                if (PortOpen())
                {
                    Log("port already open, open GUI directly");
                    win.Navigate(Url);
                    app.Run(win);
                    return ExitCode;
                }

                // 端口空闲：用 WebView2 播放 file:// splash，后台拉起 dsh web。
                // 先删掉上一轮遗留的 token.js：splash 从加载起就高频轮询它，若不删，
                // 会在服务就绪前读到旧 token 并带着它跳转（旧 token 在新服务上被拒 → 404）。
                try { File.Delete(IOPath.Combine(SplashDir(), "token.js")); } catch { }
                // splash(wait=token) 轮询同目录 token.js，服务就绪+token 到手后原地跳到同源 GUI（单窗口、无缝）。
                win.Navigate(FileSplashUrl("0") + "&wait=token");
                Log("navigated to splash");

                var closeEvt = new ManualResetEvent(false);
                win.Closed += (s, e) => { try { closeEvt.Set(); } catch { } };

                Thread worker = new Thread(() => LauncherWorker(win, closeEvt));
                worker.IsBackground = true;
                worker.Start();

                app.Run(win);
                try { closeEvt.Set(); } catch { }
                try { worker.Join(6000); } catch { }
                return ExitCode;
            }
            catch (Exception ex)
            {
                Log("ERROR: " + ex);
                try { MessageBox.Show("DSH GUI 启动失败：\n" + ex.Message, "DSH GUI", MessageBoxButton.OK, MessageBoxImage.Error); } catch { }
                return 1;
            }
        }

        // 释放内嵌的 WebView2 SDK（托管 DLL + 原生 loader）到 bin 目录并挂载：
        // SetDllDirectory 让原生 WebView2Loader.dll 可被找到；AssemblyResolve 让托管 Core/Wpf 从 bin 加载。
        static void SetupWv2Runtime()
        {
            try
            {
                Directory.CreateDirectory(Wv2BinDir);
                string[][] map = new[] {
                    new[] { "DshGui.Wv2Core", "Microsoft.Web.WebView2.Core.dll" },
                    new[] { "DshGui.Wv2Wpf", "Microsoft.Web.WebView2.Wpf.dll" },
                    new[] { "DshGui.Wv2Loader", "WebView2Loader.dll" }
                };
                foreach (var pair in map)
                {
                    string dest = IOPath.Combine(Wv2BinDir, pair[1]);
                    if (!File.Exists(dest))
                    {
                        using (Stream s = typeof(Program).Assembly.GetManifestResourceStream(pair[0]))
                        {
                            if (s != null) using (FileStream fs = new FileStream(dest, FileMode.Create, FileAccess.Write)) s.CopyTo(fs);
                        }
                    }
                }
                SetDllDirectory(Wv2BinDir);
                AppDomain.CurrentDomain.AssemblyResolve += delegate (object o, ResolveEventArgs e)
                {
                    string simple = new AssemblyName(e.Name).Name + ".dll";
                    string p = IOPath.Combine(Wv2BinDir, simple);
                    return File.Exists(p) ? Assembly.LoadFrom(p) : null;
                };
                // 清理自愈（E_ABORT 换新目录）遗留的旧 profile 目录，避免堆积
                try
                {
                    string parent = IOPath.GetDirectoryName(Wv2UserDataFolder);
                    foreach (string d in Directory.GetDirectories(parent, "webview2-*"))
                    {
                        try { Directory.Delete(d, true); } catch { }
                    }
                }
                catch { }
                Log("webview2 sdk ready at " + Wv2BinDir);
            }
            catch (Exception ex) { Log("webview2 setup failed: " + ex.Message); }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool SetDllDirectory(string lpPathName);

        static void LauncherWorker(DshWindow win, ManualResetEvent closeEvt)
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
                    // 另一实例正在启动服务：等它就绪——本窗口直接打开同源 GUI（不重复启动服务器）
                    Log("another instance is starting the server");
                    bool ready = WaitPort(90);
                    if (!ready)
                    {
                        UiMessage("DSH web 服务未能就绪，请重试。", "DSH GUI", MessageBoxImage.Warning);
                        CloseUi(win);
                        FinishApp(1);
                        return;
                    }
                    WaitLaunchToken(15);
                    Log("owner ready, open GUI directly");
                    StartUi(win, delegate { win.Navigate(SplashTarget()); });
                    closeEvt.WaitOne();
                    FinishApp(0);
                    return;
                }

                // 轮换服务输出日志：本轮启动只保留本次 run 的行，防止上次 token 残留
                try { File.WriteAllText(ServerOut, ""); } catch { }
                File.WriteAllText(LockFile, Process.GetCurrentProcess().Id.ToString());
                Log("starting dsh web");

                ResolveDshInstall();
                Log("resolve dsh install: " + sw.ElapsedMilliseconds + "ms");
                SyncSplashToDist();
                Log("sync splash to dist: " + sw.ElapsedMilliseconds + "ms");

                Process server = StartServer();
                if (server == null)
                {
                    UiMessage(LastServerError ?? "无法启动 dsh web（node 或 dsh 未找到）。", "DSH GUI", MessageBoxImage.Error);
                    try { File.Delete(LockFile); } catch { }
                    CloseUi(win);
                    FinishApp(1);
                    return;
                }
                Log("server process started: " + sw.ElapsedMilliseconds + "ms");

                if (!WaitPort(90))
                {
                    UiMessage("DSH web 服务 90 秒内未就绪，请查看日志：" + ServerErr, "DSH GUI", MessageBoxImage.Error);
                    KillTree(server.Id);
                    try { File.Delete(LockFile); } catch { }
                    CloseUi(win);
                    FinishApp(1);
                    return;
                }
                Log("server ready: " + sw.ElapsedMilliseconds + "ms");

                WaitLaunchToken(15);

                // 启动器自己完成 token→cookie 交换：GET /?token=X → 303 + Set-Cookie（不跟随重定向）。
                // 轮询期间反复用 ServerOut 里的最新 token 重试，插件树重载导致的 token 轮换也能覆盖。
                // 这样 WebView2 全程不需要碰 token（token 是一次性/进程绑定的，交给页面容易失效）。
                if (ExchangeSessionToken(30)) Log("session cookie exchanged: " + sw.ElapsedMilliseconds + "ms");
                else Log("WARNING: session cookie not exchanged in 30s");

                // 带 cookie 拿一次 index（200 = 静态 fallback 已就绪），确认 WebView2 跳转必然成功
                if (WaitIndexWithCookie(30)) Log("index ready with cookie: " + sw.ElapsedMilliseconds + "ms");
                else Log("WARNING: index not ready with cookie in 30s");

                // 把会话 cookie 注入 WebView2，再导航到干净的 GUI 根路径（splash 只负责动画，不再自跳转）
                if (!string.IsNullOrEmpty(SessionCookieName) && !string.IsNullOrEmpty(SessionCookieValue))
                {
                    StartUi(win, delegate { win.SetSessionCookie(SessionCookieName, SessionCookieValue); });
                    // 跳转到“服务端 splash（hold 终态）”：画面与 file:// 动画终态一致，无缝衔接；
                    // 它用隐藏 iframe 预加载真实 GUI（官方 Loading 页不露脸），_boot_ 卡消失后淡出揭示。
                    StartUi(win, delegate { win.Navigate(ServedSplashUrl()); });
                    Log("gui navigation triggered: " + sw.ElapsedMilliseconds + "ms");
                }
                else
                {
                    Log("no session cookie; fallback to token.js flow");
                    WriteLaunchTokenJs();
                }

                // 窗口关闭后停止本次启动的服务
                closeEvt.WaitOne();
                Log("window closed, stopping server");
                KillTree(server.Id);
                try { File.Delete(LockFile); } catch { }
                FinishApp(0);
            }
            catch (Exception ex)
            {
                Log("ERROR: " + ex);
                CloseUi(win);
                FinishApp(1);
            }
        }

        // 跨线程把动作调度到 UI 线程执行
        static void StartUi(DshWindow win, Action act)
        {
            try { win.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => { try { act(); } catch { } })); }
            catch { }
        }

        static void CloseUi(DshWindow win)
        {
            StartUi(win, delegate { if (win != null) win.Close(); });
        }

        static void UiMessage(string text, string caption, MessageBoxImage image)
        {
            try { MessageBox.Show(text, caption, MessageBoxButton.OK, image); }
            catch (Exception ex) { Log("message failed: " + ex.Message); }
        }

        static void FinishApp(int code)
        {
            ExitCode = code;
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

        static string SessionCookieName = "";
        static string SessionCookieValue = "";

        // 用最新 token 换取会话 cookie：GET /?token=X → 303 + Set-Cookie（不跟随）。
        // 每次循环都从 ServerOut 重扫最新 "dsh web:" 行（插件重载会轮换 token），拿到 303 即成功。
        static bool ExchangeSessionToken(int seconds)
        {
            for (int i = 0; i < seconds * 2; i++)
            {
                try
                {
                    if (string.IsNullOrEmpty(LaunchToken))
                    {
                        foreach (string line in File.ReadLines(ServerOut)) ExtractLaunchToken(line);
                    }
                    if (!string.IsNullOrEmpty(LaunchToken))
                    {
                        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(Url + "/?token=" + Uri.EscapeDataString(LaunchToken));
                        req.Method = "GET";
                        req.Timeout = 1100;
                        req.UserAgent = "DSH-GUI";
                        req.AllowAutoRedirect = false;
                        using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                        {
                            if (resp.StatusCode == HttpStatusCode.SeeOther)
                            {
                                string setCookie = resp.Headers["Set-Cookie"];
                                if (!string.IsNullOrEmpty(setCookie))
                                {
                                    int eq = setCookie.IndexOf('=');
                                    int sc = setCookie.IndexOf(';');
                                    if (eq > 0)
                                    {
                                        SessionCookieName = setCookie.Substring(0, eq).Trim();
                                        SessionCookieValue = (sc > eq ? setCookie.Substring(eq + 1, sc - eq - 1) : setCookie.Substring(eq + 1)).Trim();
                                        Log("session cookie: " + SessionCookieName);
                                        return true;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (WebException wx)
                {
                    HttpWebResponse r = wx.Response as HttpWebResponse;
                    if (r != null)
                    {
                        // 401 = 认证层已就绪但 token 不匹配（可能已轮换）→ 重扫新 token 重试
                        if ((int)r.StatusCode != 401) { Thread.Sleep(300); continue; }
                    }
                }
                catch { }
                Thread.Sleep(300);
            }
            return false;
        }

        // 带已交换的 cookie 拿 index：200 = 认证 + 静态 fallback 都已就绪（WebView2 注入 cookie 后同此链）。
        static bool WaitIndexWithCookie(int seconds)
        {
            for (int i = 0; i < seconds * 2; i++)
            {
                try
                {
                    if (string.IsNullOrEmpty(SessionCookieName) || string.IsNullOrEmpty(SessionCookieValue)) { Thread.Sleep(300); continue; }
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(Url + "/");
                    req.Method = "GET";
                    req.Timeout = 900;
                    req.UserAgent = "DSH-GUI";
                    req.AllowAutoRedirect = true;
                    CookieContainer cc = new CookieContainer();
                    cc.Add(new Cookie(SessionCookieName, SessionCookieValue, "/", "127.0.0.1"));
                    req.CookieContainer = cc;
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    {
                        if (resp.StatusCode == HttpStatusCode.OK) return true;
                    }
                }
                catch (WebException wx)
                {
                    HttpWebResponse r = wx.Response as HttpWebResponse;
                    if (r != null && r.StatusCode == HttpStatusCode.OK) return true;
                }
                catch { }
                Thread.Sleep(400);
            }
            return false;
        }



        // splash 跳转目标:alpha.3+ 首次进入带 token(服务端换 cookie 后 303 回干净路径),无 token 时退回原行为
        static string SplashTarget()
        {
            return LaunchToken == null ? Url : Url + "?token=" + LaunchToken;
        }

        // 服务端 splash（hold 终态）：与 file:// 动画终态同款画面，隐藏 iframe 预加载真实 GUI（无 token，cookie 已注入）
        static string ServedSplashUrl()
        {
            return Url + "/splash.html?hold=1&timeout=60&target=" + Uri.EscapeDataString(Url) + ThemeQuery();
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
            // 先探测常见全局目录（秒级，避免每次启动都 spawn `npm root -g`）。
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
            if (TryLoadResolveCache()) return;
            try
            {
                NpmRoot = ResolveNpmRoot();
                if (string.IsNullOrEmpty(NpmRoot)) throw new Exception("npm root -g empty");
                DshBin = IOPath.Combine(NpmRoot, "@deepseek-ai", "dsh", "lib", "bin.js");
                if (!File.Exists(DshBin)) throw new Exception("dsh bin missing: " + DshBin);

                string node = ResolveNodeExe();
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
                sb.AppendLine("splash.html=" + Assets.ReadText("splash.html").Length + "B, deepseek-wordmark.svg=" + Assets.ReadText("deepseek-wordmark.svg").Length + "B");
                sb.AppendLine("embedded resources: " + string.Join(",", typeof(Program).Assembly.GetManifestResourceNames()));
                ResolveDshInstall();
                sb.AppendLine("dshBin=" + DshBin);
                sb.AppendLine("dist=" + DistDir);
                if (string.IsNullOrEmpty(DshBin) || !File.Exists(DshBin))
                    sb.AppendLine("WARNING: dsh 未找到（npm root -g 解析失败）；请确认已执行 npm install -g @deepseek-ai/dsh");
                sb.AppendLine("dshTheme=" + ReadDshThemePreference());
                sb.AppendLine("windowState=" + WindowStateText() + " (" + WindowStateFile + ")");
                sb.AppendLine("wv2 core=" + (typeof(Program).Assembly.GetManifestResourceStream("DshGui.Wv2Core") != null) +
                    " wpf=" + (typeof(Program).Assembly.GetManifestResourceStream("DshGui.Wv2Wpf") != null) +
                    " loader=" + (typeof(Program).Assembly.GetManifestResourceStream("DshGui.Wv2Loader") != null));
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

    // 主窗口：自绘标题栏（随主题色）+ 黑鲸图标 + WebView2 内容区
    public class DshWindow : Window
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
        static readonly int DWMWA_CAPTION_COLOR = 25; // Win11 22H2+：非客户区标题栏填充色
        static readonly int DWMWA_BORDER_COLOR = 34;  // Win11 22H2+：窗口边框线色

        WebView2 web;
        TextBlock _titleText;
        string _pendingUrl;
        Color _barColor = Color.FromRgb(0xd5, 0xe2, 0xff);
        // 最近一次“非最小化”的窗口形态：关闭时若处于最小化，用它回落（避免把最小化记成默认缩小）
        WindowState _lastRestorableState = WindowState.Normal;

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }
        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")]
        static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                IntPtr h = new WindowInteropHelper(this).Handle;
                if (h == IntPtr.Zero) return;
                int colorref = (_barColor.B << 16) | (_barColor.G << 8) | _barColor.R;
                DwmSetWindowAttribute(h, DWMWA_CAPTION_COLOR, ref colorref, 4);
                DwmSetWindowAttribute(h, DWMWA_BORDER_COLOR, ref colorref, 4);

                // 无边框窗口最大化时会超出屏幕约 7px（右侧被裁掉，右上角按钮"偏右/没显示完"）。
                // 处理 WM_GETMINMAXINFO，把最大化尺寸约束到当前显示器的工作区。
                HwndSource src = (HwndSource)HwndSource.FromVisual(this);
                if (src != null) src.AddHook(WndProc);
            }
            catch { }
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x0024) // WM_GETMINMAXINFO
            {
                try
                {
                    MINMAXINFO mmi = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO));
                    IntPtr mon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
                    if (mon != IntPtr.Zero)
                    {
                        MONITORINFO mi = new MONITORINFO();
                        mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                        if (GetMonitorInfo(mon, ref mi))
                        {
                            mmi.ptMaxPosition.X = mi.rcWork.Left;
                            mmi.ptMaxPosition.Y = mi.rcWork.Top;
                            mmi.ptMaxSize.X = mi.rcWork.Right - mi.rcWork.Left;
                            mmi.ptMaxSize.Y = mi.rcWork.Bottom - mi.rcWork.Top;
                            mmi.ptMaxTrackSize.X = mmi.ptMaxSize.X;
                            mmi.ptMaxTrackSize.Y = mmi.ptMaxSize.Y;
                            Marshal.StructureToPtr(mmi, lParam, false);
                            handled = true;
                        }
                    }
                }
                catch { }
            }
            return IntPtr.Zero;
        }

        public DshWindow()
        {
            Title = "DeepSeek Harness";
            Width = Program.WinW;
            Height = Program.WinH;
            MinWidth = 640;
            MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            // WindowChrome 接管窗口外壳：取消非客户区（内容铺满到窗口边缘，无系统绘制的
            // 钢蓝边框带），同时保留缩放手柄（ResizeBorderThickness）与标题栏拖拽（CaptionHeight）。
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 36,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0, 0, 0, 1),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            });

            try
            {
                using (Stream s = typeof(Program).Assembly.GetManifestResourceStream("DshGui.whale-black.ico"))
                    if (s != null) Icon = BitmapFrame.Create(s);
            }
            catch { }

            // 还原上次关闭时的形态：只有“最大化”需要额外动作，其余一律按默认缩小窗口打开
            _lastRestorableState = Program.ReadWindowMaximized() ? WindowState.Maximized : WindowState.Normal;
            if (_lastRestorableState == WindowState.Maximized) WindowState = WindowState.Maximized;

            BuildLayout();
            Loaded += DshWindow_Loaded;
            // 关闭时记忆形态：最小化状态下关闭则回落到最近一次非最小化形态
            Closing += (s, e) => Program.SaveWindowMaximized(
                (WindowState == WindowState.Minimized ? _lastRestorableState : WindowState) == WindowState.Maximized);
        }

        public void Navigate(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            _pendingUrl = url;
            try
            {
                if (web != null && web.CoreWebView2 != null && !string.IsNullOrEmpty(_pendingUrl))
                    web.CoreWebView2.Navigate(_pendingUrl);
            }
            catch { }
        }

        // 注入启动器换来的会话 cookie（host-only @127.0.0.1），随后导航 / 即可通过认证
        public void SetSessionCookie(string name, string value)
        {
            try
            {
                if (web == null || web.CoreWebView2 == null) return;
                var cm = web.CoreWebView2.CookieManager;
                var cookie = cm.CreateCookie(name, value, "127.0.0.1", "/");
                cookie.SameSite = CoreWebView2CookieSameSiteKind.Strict;
                cookie.Expires = DateTime.Now.AddDays(30);
                cm.AddOrUpdateCookie(cookie);
                Program.Log("session cookie injected into webview2");
            }
            catch (Exception ex) { Program.Log("cookie inject failed: " + ex.Message); }
        }

        string _initError = "";

        async void DshWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (web == null) return;
            if (await InitAndNavigate(Program.Wv2UserDataFolder)) return;
            // 自愈：环境创建被中止（0x80004004 E_ABORT）通常是因为用户数据目录被上次强制结束的实例残留占用。
            // 换一个全新目录重试一次（token 每次启动都会重新认证，无需保留旧的 cookie）。
            string fresh = Program.Wv2UserDataFolder + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Program.Log("webview2 init failed (" + _initError + "), retry with fresh profile");
            if (await InitAndNavigate(fresh))
            {
                Program.Log("webview2 init recovered (fresh profile)");
                return;
            }
            Program.Log("webview2 init failed (retry too): " + _initError);
            try { MessageBox.Show("WebView2 初始化失败（请确认已安装 Microsoft Edge WebView2 Runtime）：\n" + _initError, "DSH GUI", MessageBoxButton.OK, MessageBoxImage.Error); } catch { }
            Close();
        }

        async Task<bool> InitAndNavigate(string folder)
        {
            try
            {
                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, folder, null);
                await web.EnsureCoreWebView2Async(env);
                if (!string.IsNullOrEmpty(_pendingUrl))
                    web.CoreWebView2.Navigate(_pendingUrl);
                return true;
            }
            catch (Exception ex) { _initError = ex.Message; return false; }
        }

        void BuildLayout()
        {
            bool dark = Program.ReadDshThemePreference() != "light";
            // 标题栏与 dsh web 侧边栏同色（--dsw-specific-sidebar-fill）：
            // 浅色极浅灰 #f9fafb，深色 #1b1b1c（图标/文字为对比色）
            Color barColor = dark ? Color.FromRgb(0x1b, 0x1b, 0x1c) : Color.FromRgb(0xf9, 0xfa, 0xfb);
            _barColor = barColor; // 供 OnSourceInitialized 设置 DWM 边框色（与标题栏同色）
            Color barText = dark ? Color.FromRgb(0xe7, 0xec, 0xff) : Color.FromRgb(0x1b, 0x2a, 0x5b);
            Color hoverBg = dark ? Color.FromRgb(0x1a, 0x22, 0x3f) : Color.FromRgb(0xe4, 0xe8, 0xf2);
            Color closeBg = Color.FromRgb(0xe8, 0x1e, 0x2f);
            Background = new SolidColorBrush(barColor);

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // ---- 自定义标题栏 ----
            var titleBar = new Border();
            titleBar.Background = new SolidColorBrush(barColor);
            // 拖拽与双击最大化交给 WindowChrome（CaptionHeight 区域由系统原生处理）

            var tb = new Grid();
            tb.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tb.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();
            left.Orientation = Orientation.Horizontal;
            left.VerticalAlignment = VerticalAlignment.Center;
            var logo = new Image();
            try
            {
                using (Stream s = typeof(Program).Assembly.GetManifestResourceStream("DshGui.whale.png"))
                    if (s != null) logo.Source = BitmapFrame.Create(s);
                logo.Width = 18; logo.Height = 18;
                logo.Margin = new Thickness(10, 0, 8, 0);
                logo.VerticalAlignment = VerticalAlignment.Center;
                logo.SnapsToDevicePixels = true;
                RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality); // 高清缩放，避免发糊
                left.Children.Add(logo);
            }
            catch { }

            _titleText = new TextBlock();
            _titleText.Text = "DeepSeek Harness";
            _titleText.Foreground = new SolidColorBrush(barText);
            _titleText.VerticalAlignment = VerticalAlignment.Center;
            _titleText.Margin = new Thickness(0, 0, 0, 0);
            _titleText.FontSize = 12;
            left.Children.Add(_titleText);
            Grid.SetColumn(left, 0);
            tb.Children.Add(left);

            var buttons = new StackPanel();
            buttons.Orientation = Orientation.Horizontal;
            buttons.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(buttons, 1);

            // 三个按钮图标用矢量 Path 绘制：精确居中（不受字形底板影响）、任意缩放清晰
            buttons.Children.Add(MakeTitleButton(MakeGlyph("M2.5,7 L11.5,7"), barText, hoverBg, null,
                delegate { WindowState = WindowState.Minimized; }));

            WPath maxGlyph = MakeGlyph("M2,2 L12,2 L12,12 L2,12 Z");
            buttons.Children.Add(MakeTitleButton(maxGlyph, barText, hoverBg, null, delegate
            {
                if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
                else WindowState = WindowState.Maximized;
            }));
            StateChanged += (s, e) =>
            {
                UpdateMaxGlyph(maxGlyph);
                // 记录最近一次非最小化形态，供关闭时记忆
                if (WindowState != WindowState.Minimized) _lastRestorableState = WindowState;
            };
            UpdateMaxGlyph(maxGlyph);

            // 关闭按钮：平时与其他按钮同色，悬停时红底 + 白色图标（统一中有区分）
            buttons.Children.Add(MakeTitleButton(MakeGlyph("M2.5,2.5 L11.5,11.5 M11.5,2.5 L2.5,11.5"), barText, closeBg,
                Color.FromRgb(0xff, 0xff, 0xff), delegate { Close(); }));

            tb.Children.Add(buttons);
            titleBar.Child = tb;
            Grid.SetRow(titleBar, 0);
            grid.Children.Add(titleBar);

            // ---- WebView2 内容区 ----
            // 用户数据目录在 DshWindow_Loaded 里通过 CoreWebView2Environment.CreateAsync 显式指定（失败时可自愈换新目录）
            web = new WebView2();
            Grid.SetRow(web, 1);
            grid.Children.Add(web);

            Content = grid;
        }

        // 画一个 14x14 视框的矢量图标（Stroke 由调用方设置）
        WPath MakeGlyph(string data)
        {
            var p = new WPath();
            p.Data = Geometry.Parse(data);
            p.Width = 14;
            p.Height = 14;
            p.StrokeThickness = 1.4;
            p.StrokeStartLineCap = PenLineCap.Square;
            p.StrokeEndLineCap = PenLineCap.Square;
            p.StrokeLineJoin = PenLineJoin.Miter;
            p.HorizontalAlignment = HorizontalAlignment.Center;
            p.VerticalAlignment = VerticalAlignment.Center;
            return p;
        }

        // 最大化/还原图标随窗口状态切换：最大化时显示“双框重叠”（还原），否则显示单框（最大化）
        void UpdateMaxGlyph(WPath g)
        {
            if (WindowState == WindowState.Maximized)
                g.Data = Geometry.Parse("M5,1.5 L12.5,1.5 L12.5,9 M3.5,5 L10.5,5 L10.5,12.5 L3.5,12.5 Z");
            else
                g.Data = Geometry.Parse("M2,2 L12,2 L12,12 L2,12 Z");
        }

        Border MakeTitleButton(WPath glyph, Color glyphColor, Color hoverBg, Color? hoverGlyphColor, Action onClick)
        {
            // 扁平无边框；图标为矢量 Path（精确居中、任意缩放清晰）
            var b = new Border();
            WindowChrome.SetIsHitTestVisibleInChrome(b, true); // WindowChrome 标题栏内可点击区域
            b.Width = 46;
            b.Height = 36;
            b.Background = Brushes.Transparent;
            b.Cursor = Cursors.Hand;
            glyph.Stroke = new SolidColorBrush(glyphColor);
            b.Child = glyph;
            // 按下时标记已处理，避免被 WindowChrome 当作标题栏拖拽
            b.MouseLeftButtonDown += (s, e) => e.Handled = true;
            b.MouseEnter += (s, e) =>
            {
                b.Background = new SolidColorBrush(hoverBg);
                if (hoverGlyphColor.HasValue) glyph.Stroke = new SolidColorBrush(hoverGlyphColor.Value);
            };
            b.MouseLeave += (s, e) =>
            {
                b.Background = Brushes.Transparent;
                glyph.Stroke = new SolidColorBrush(glyphColor);
            };
            b.MouseLeftButtonUp += (s, e) => onClick();
            return b;
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
