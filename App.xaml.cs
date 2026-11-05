using Microsoft.UI.Xaml;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using App1.Config;
using App1.Helpers;
using App1.Services.Background;
using App1.Services.Diagnostics;

namespace App1
{
    /// <summary>
    /// 应用生命周期。
    ///
    /// 结构上分成两层：
    ///   - 常驻层：托盘图标 + 自动换壁纸调度。进程一起来就有，主窗口关掉之后继续活着。
    ///   - 前端层：MainWindow。只有用户要看图时才创建，关掉就销毁，
    ///     XAML 树和图片解码缓存跟着一起释放。
    ///
    /// 原来这两层绑在一起，主窗口只能 Hide、不能关，所以托盘常驻时内存一直不降。
    /// </summary>
    public partial class App : Application
    {
        private static Mutex? _mutex;
        private const string MUTEX_NAME = "Global\\小K壁纸单实例Mutex";

        private Window? _mainWindow;
        private TrayHost? _tray;
        private AutoWallpaperService? _autoWallpaper;
        private bool _exiting;
        private bool _closeTipShown;

        public App()
        {
            InitializeComponent();

            // WinUI 默认在"最后一个窗口关闭"时结束进程。托盘常驻必须改掉这个默认值，
            // 否则主窗口一关，后台换壁纸也跟着停。
            DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            // 注意：非打包（unpackaged）的 WinUI 3 应用里，LaunchActivatedEventArgs.Arguments
            // 通常是空的，命令行参数只能从 Environment.GetCommandLineArgs() 拿。
            // 原来只认 args.Arguments，所以 /background 实际上从来没生效过，
            // 开机自启每次都会把主窗口弹出来。
            var fullCommandLine = Environment.CommandLine ?? string.Empty;
            var launchArgs = args?.Arguments ?? string.Empty;
            var combinedArgs = fullCommandLine + " " + launchArgs;

            var startHidden =
                combinedArgs.Contains("--background", StringComparison.OrdinalIgnoreCase) ||
                combinedArgs.Contains("/background", StringComparison.OrdinalIgnoreCase);

            AppLog.Write($"[启动] pid={Environment.ProcessId} 后台={startHidden} 命令行='{fullCommandLine}'");

            bool createdNew;
            _mutex = new Mutex(true, MUTEX_NAME, out createdNew);

            if (!createdNew)
            {
                // 已经有实例在跑。后台启动（开机自启）就安静退出；
                // 手动启动则把已经在跑的那个实例的窗口叫出来。
                if (!startHidden)
                {
                    AskRunningInstanceToShow();
                }

                AppLog.Write("[启动] 已有实例在运行，本进程退出");
                Environment.Exit(0);
                return;
            }

            AppDomain.CurrentDomain.ProcessExit += (_, _) => PersistSettingsBeforeExit();

            StartBackgroundServices(startHidden);

            if (!startHidden)
            {
                ShowMainWindow();
            }
            else
            {
                AppLog.Write("[启动] 后台模式：不创建主窗口，只跑托盘 + 自动换壁纸");

                // 后台启动没有窗口，但 XAML 框架初始化、JIT、托盘宿主仍然会留下
                // 一波临时对象。等它们死透之后收一次，任务管理器里的数字能明显降。
                _ = TrimAfterStartupAsync();
            }
        }

        /// <summary>
        /// 后台启动稳定之后回收一次内存。
        /// 放在线程池上跑，别让强制 GC 的几百毫秒卡在启动路径里。
        /// </summary>
        private static async Task TrimAfterStartupAsync()
        {
            await Task.Delay(3000).ConfigureAwait(false);

            try
            {
                await Task.Run(() => MemoryTrimmer.Compact("后台启动稳定后")).ConfigureAwait(false);
            }
            catch
            {
                // 回收失败不影响功能
            }
        }

        /// <summary>
        /// 启动常驻部分。这两个东西都不依赖 XAML 窗口，可以在整个进程生命周期里独立存在。
        /// </summary>
        private void StartBackgroundServices(bool backgroundLaunch)
        {
            _autoWallpaper = new AutoWallpaperService();
            _autoWallpaper.Changed += (desktop, title) =>
                AppLog.Write($"[自动换壁纸] 已应用 | {(desktop ? "桌面" : "锁屏")} | {title}");

            // 开机自启时 45 秒后先换一张 —— 用户开机就想看到桌面变了，
            // 而不是等满一个定时周期。手动启动则维持"等一个周期"的原行为。
            _autoWallpaper.ApplySettings(
                SettingsManager.LoadSettings(),
                initialDelaySeconds: backgroundLaunch ? 45 : -1);

            _tray = new TrayHost();
            _tray.OpenMainRequested += ShowMainWindow;
            _tray.ChangeNowRequested += () => _ = ChangeNowFromTrayAsync();
            _tray.ExitRequested += ExitApplication;

            // 开关键盘在注册表里，托盘菜单直接改；主窗口下次打开时读到的也是它
            _tray.StartupToggled += enabled =>
                AppLog.Write($"[开机启动] {(enabled ? "已开启" : "已关闭")}");

            _tray.Initialize();
        }

        /// <summary>
        /// 主窗口里改了定时设置时调这个，把新的间隔同步给后台调度。
        /// 不重启进程，也不依赖窗口是否还开着。
        /// </summary>
        public void ApplyAutoWallpaperSettings(AppSettings settings)
        {
            _autoWallpaper?.ApplySettings(settings);
        }

        /// <summary>
        /// 从主窗口冒个气泡。托盘在 App 层，窗口自己已经没有托盘句柄了。
        /// </summary>
        public void ShowTrayNotification(string title, string message)
        {
            _tray?.ShowNotification(title, message);
        }

        /// <summary>
        /// 主窗口里的"立即换一张"。和托盘菜单走同一条路径。
        /// </summary>
        public Task<bool> ChangeWallpaperNowAsync(string reason)
        {
            return _autoWallpaper?.ChangeAsync(desktop: true, reason) ?? Task.FromResult(false);
        }

        private async Task ChangeNowFromTrayAsync()
        {
            if (_autoWallpaper == null)
            {
                return;
            }

            var ok = await _autoWallpaper.ChangeAsync(desktop: true, reason: "托盘菜单");
            _tray?.ShowNotification("小K壁纸", ok ? "壁纸已更换" : "换壁纸失败，详见 app.log");
        }

        /// <summary>
        /// 打开主界面。已经开着就还原并前置，没开就新建。
        /// </summary>
        public void ShowMainWindow()
        {
            if (_exiting)
            {
                return;
            }

            if (_mainWindow != null)
            {
                try
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);
                    ShowWindow(hwnd, SW_RESTORE);
                    SetForegroundWindow(hwnd);
                }
                catch (Exception ex)
                {
                    AppLog.Write($"[主窗口] 激活已有窗口失败: {ex.Message}");
                }

                return;
            }

            try
            {
                var window = new MainWindow();
                window.Closed += OnMainWindowClosed;
                _mainWindow = window;
                window.Activate();
                AppLog.Write("[主窗口] 已创建");
            }
            catch (Exception ex)
            {
                AppLog.Write($"[主窗口] 创建失败: {ex}");
            }
        }

        private void OnMainWindowClosed(object sender, WindowEventArgs args)
        {
            if (ReferenceEquals(_mainWindow, sender))
            {
                _mainWindow = null;
            }

            // 窗口是真关掉了（不是藏起来），用户容易以为程序退出了。
            // 只在第一次关的时候冒个泡说明一下，免得每次关都弹。
            if (!_exiting && !_closeTipShown)
            {
                _closeTipShown = true;
                _tray?.ShowNotification("小K壁纸", "已转入后台，自动换壁纸继续运行");
            }

            // 窗口已经销毁，MainWindow 里也把 ItemsSource、Image.Source 都断掉了。
            // 这里再强制收一次并把工作集换出去：GC 归还不等于任务管理器数字会降，
            // 不 Trim 的话物理页还挂在进程上，看起来就像"只释放了 UI"。
            MemoryTrimmer.Compact("主窗口关闭");

            AppLog.Write("[主窗口] 已关闭并释放");
        }

        /// <summary>
        /// 真正退出进程。托盘菜单"退出"和主窗口里的退出都走这里。
        /// </summary>
        public void ExitApplication()
        {
            if (_exiting)
            {
                return;
            }

            _exiting = true;
            AppLog.Write("[退出] 开始");

            try { PersistSettingsBeforeExit(); } catch { }
            try { _autoWallpaper?.Dispose(); } catch { }
            try { _tray?.Dispose(); } catch { }
            try { _mainWindow?.Close(); } catch { }

            Exit();
        }

        // ===== 单实例唤醒 =====

        /// <summary>
        /// 第二次启动时，把已经在跑的那个实例的窗口叫出来。
        /// 走的是托盘宿主的隐藏窗口 —— 主窗口关着也能找到人。
        /// </summary>
        private static void AskRunningInstanceToShow()
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    var hwnd = FindWindow(TrayHost.WindowClassName, null);
                    if (hwnd != IntPtr.Zero)
                    {
                        PostMessage(hwnd, TrayHost.WM_APP_SHOW_MAIN, IntPtr.Zero, IntPtr.Zero);
                        AppLog.Write($"[单实例] 已请求现有实例显示主窗口（第 {attempt + 1} 次尝试）");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Write($"[单实例] 唤醒失败: {ex.Message}");
                }

                // 对方可能刚启动、托盘窗口还没建好，等一下再试
                Thread.Sleep(300);
            }

            AppLog.Write("[单实例] 未找到现有实例的托盘窗口");
        }

        // ===== 退出前保存 =====

        private static void PersistSettingsBeforeExit()
        {
            try
            {
                // 主窗口关着的时候没有设置可存，直接跳过
                if ((Application.Current as App)?._mainWindow is MainWindow window)
                {
                    window.SaveSettingsNow();
                }
            }
            catch (Exception ex)
            {
                AppLog.Write($"[退出] 保存设置失败: {ex.Message}");
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private const int SW_RESTORE = 9;
    }
}
