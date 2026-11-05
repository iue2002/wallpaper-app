using System;
using System.Runtime.InteropServices;

namespace App1.Helpers
{
    /// <summary>
    /// 托盘宿主：一个纯 Win32 的隐藏窗口，只负责接收托盘图标的鼠标消息、弹右键菜单。
    ///
    /// 为什么要单独开一个窗口，而不是继续挂在主窗口上：
    /// 主窗口关掉之后要能被彻底销毁（窗口、XAML 树、图片解码缓存全部释放），
    /// 内存才真正降得下来；而托盘图标必须一直活着。两件事绑在同一个窗口上，
    /// 就只能选择"窗口不关、内存不降"。拆开之后，主窗口变成纯粹的前端，
    /// 用的时候创建、关掉就销毁。
    ///
    /// 这个窗口不显示、不占任务栏、尺寸为 0，消息由 WinUI 已经在跑的消息泵分发。
    /// </summary>
    public sealed class TrayHost : IDisposable
    {
        /// <summary>窗口类名。外部唤醒进程时靠 FindWindow 找的就是这个名字。</summary>
        public const string WindowClassName = "XiaoKWallpaperTrayHost";

        /// <summary>外部进程（第二次启动的实例）用这个消息请求"把主界面叫出来"。</summary>
        public const int WM_APP_SHOW_MAIN = 0x8000 + 1;

        private const int WM_TRAYICON = 0x0400 + 1;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_NULL = 0x0000;

        // 托盘图标
        private const int NIM_ADD = 0x00000000;
        private const int NIM_MODIFY = 0x00000001;
        private const int NIM_DELETE = 0x00000002;

        private const int NIF_MESSAGE = 0x00000001;
        private const int NIF_ICON = 0x00000002;
        private const int NIF_TIP = 0x00000004;
        private const int NIF_INFO = 0x00000010;
        private const int NIIF_INFO = 0x00000001;

        // 菜单
        private const uint MF_STRING = 0x00000000;
        private const uint MF_SEPARATOR = 0x00000800;
        private const uint MF_CHECKED = 0x00000008;
        private const uint TPM_RETURNCMD = 0x0100;
        private const uint TPM_RIGHTBUTTON = 0x0002;

        private const uint ID_SHOW = 1000;
        private const uint ID_EXIT = 1001;
        private const uint ID_CHANGE_NOW = 1002;
        private const uint ID_STARTUP = 1003;

        private IntPtr _hWnd = IntPtr.Zero;
        private IntPtr _hIcon = IntPtr.Zero;
        private NotifyIconData _nid;
        private bool _iconAdded;
        private bool _disposed;

        // 委托必须用字段保住引用，否则被 GC 回收后窗口过程就指向野指针了
        private WndProcDelegate? _wndProc;

        private readonly uint _taskbarCreatedMessage;

        /// <summary>双击托盘 / 菜单选"显示主界面" / 另一个实例发消息过来</summary>
        public event Action? OpenMainRequested;

        /// <summary>菜单选"立即换一张"</summary>
        public event Action? ChangeNowRequested;

        /// <summary>菜单选"退出"</summary>
        public event Action? ExitRequested;

        /// <summary>开机启动状态被切换（参数为新的状态）</summary>
        public event Action<bool>? StartupToggled;

        public IntPtr Handle => _hWnd;

        public TrayHost()
        {
            // Explorer 重启后会广播这条消息，收到之后要把图标重新加回去，
            // 否则任务栏崩一次、我们的托盘图标就永远消失了。
            _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        }

        /// <summary>
        /// 创建隐藏窗口并挂上托盘图标。失败不影响主流程，只是没有托盘。
        /// </summary>
        public bool Initialize()
        {
            try
            {
                if (!CreateHostWindow())
                {
                    return false;
                }

                AddIcon();
                return _iconAdded;
            }
            catch (Exception ex)
            {
                App1.Services.Diagnostics.AppLog.Write($"[托盘] 初始化失败: {ex.Message}");
                return false;
            }
        }

        private bool CreateHostWindow()
        {
            var hInstance = GetModuleHandle(null);
            _wndProc = WndProc;

            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                style = 0,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInstance,
                lpszClassName = WindowClassName
            };

            var atom = RegisterClassEx(ref wc);
            if (atom == 0)
            {
                // 1410 = 类已注册（同一个进程里重复创建时会遇到），可以继续用
                var err = Marshal.GetLastWin32Error();
                if (err != 1410)
                {
                    App1.Services.Diagnostics.AppLog.Write($"[托盘] 注册窗口类失败: {err}");
                    return false;
                }
            }

            // 样式 0：不可见、无边框、不占任务栏。消息照样能收。
            _hWnd = CreateWindowEx(
                0, WindowClassName, "小K壁纸", 0,
                0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

            if (_hWnd == IntPtr.Zero)
            {
                App1.Services.Diagnostics.AppLog.Write($"[托盘] 创建隐藏窗口失败: {Marshal.GetLastWin32Error()}");
                return false;
            }

            return true;
        }

        private void AddIcon()
        {
            try
            {
                _nid = new NotifyIconData
                {
                    cbSize = Marshal.SizeOf<NotifyIconData>(),
                    hWnd = _hWnd,
                    uID = 1,
                    uFlags = NIF_ICON | NIF_TIP | NIF_MESSAGE,
                    uCallbackMessage = WM_TRAYICON,
                    szTip = "小K壁纸",
                    szInfo = string.Empty,
                    szInfoTitle = string.Empty
                };

                if (_hIcon == IntPtr.Zero)
                {
                    var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "app.ico");
                    _hIcon = System.IO.File.Exists(iconPath)
                        ? LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE)
                        : LoadIcon(IntPtr.Zero, (IntPtr)32512);   // IDI_APPLICATION
                }

                _nid.hIcon = _hIcon;
                _iconAdded = Shell_NotifyIcon(NIM_ADD, ref _nid);
            }
            catch (Exception ex)
            {
                App1.Services.Diagnostics.AppLog.Write($"[托盘] 添加图标失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 弹一个气泡提示。主窗口关着的时候这是唯一能给用户反馈的渠道。
        /// </summary>
        public void ShowNotification(string title, string message)
        {
            if (!_iconAdded || _disposed)
            {
                return;
            }

            try
            {
                _nid.uFlags = NIF_INFO;
                _nid.szInfo = message;
                _nid.szInfoTitle = title;
                _nid.dwInfoFlags = NIIF_INFO;
                Shell_NotifyIcon(NIM_MODIFY, ref _nid);
            }
            catch
            {
                // 气泡失败无关紧要
            }
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (msg == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
                {
                    _iconAdded = false;
                    AddIcon();
                    return IntPtr.Zero;
                }

                if (msg == WM_APP_SHOW_MAIN)
                {
                    OpenMainRequested?.Invoke();
                    return IntPtr.Zero;
                }

                if (msg == WM_TRAYICON)
                {
                    var mouse = lParam.ToInt32();

                    if (mouse == WM_LBUTTONDBLCLK || mouse == WM_LBUTTONUP)
                    {
                        OpenMainRequested?.Invoke();
                        return IntPtr.Zero;
                    }

                    if (mouse == WM_RBUTTONUP)
                    {
                        ShowContextMenu();
                        return IntPtr.Zero;
                    }
                }
            }
            catch (Exception ex)
            {
                App1.Services.Diagnostics.AppLog.Write($"[托盘] 窗口过程异常: {ex.Message}");
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private void ShowContextMenu()
        {
            var hMenu = CreatePopupMenu();
            if (hMenu == IntPtr.Zero)
            {
                return;
            }

            try
            {
                var startupEnabled = StartupRegistrar.IsEnabled();

                AppendMenu(hMenu, MF_STRING, ID_SHOW, "显示主界面");
                AppendMenu(hMenu, MF_STRING, ID_CHANGE_NOW, "立即换一张");
                AppendMenu(hMenu, MF_SEPARATOR, 0, string.Empty);
                AppendMenu(hMenu, MF_STRING | (startupEnabled ? MF_CHECKED : 0), ID_STARTUP, "开机启动");
                AppendMenu(hMenu, MF_SEPARATOR, 0, string.Empty);
                AppendMenu(hMenu, MF_STRING, ID_EXIT, "退出");

                GetCursorPos(out var pt);

                // 不先抢前台，菜单点到别处不会消失（这是 Shell 托盘菜单的老规矩）
                SetForegroundWindow(_hWnd);

                var cmd = TrackPopupMenuEx(
                    hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, _hWnd, IntPtr.Zero);

                // 补一条空消息，让前台状态正常归还
                PostMessage(_hWnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

                switch (cmd)
                {
                    case ID_SHOW:
                        OpenMainRequested?.Invoke();
                        break;

                    case ID_CHANGE_NOW:
                        ChangeNowRequested?.Invoke();
                        break;

                    case ID_STARTUP:
                        var nowEnabled = StartupRegistrar.Toggle();
                        StartupToggled?.Invoke(nowEnabled);
                        ShowNotification("小K壁纸", nowEnabled ? "已开启开机启动" : "已关闭开机启动");
                        break;

                    case ID_EXIT:
                        ExitRequested?.Invoke();
                        break;
                }
            }
            catch (Exception ex)
            {
                App1.Services.Diagnostics.AppLog.Write($"[托盘] 菜单失败: {ex.Message}");
            }
            finally
            {
                DestroyMenu(hMenu);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                if (_iconAdded)
                {
                    _nid.uFlags = 0;
                    Shell_NotifyIcon(NIM_DELETE, ref _nid);
                    _iconAdded = false;
                }

                if (_hIcon != IntPtr.Zero)
                {
                    DestroyIcon(_hIcon);
                    _hIcon = IntPtr.Zero;
                }

                if (_hWnd != IntPtr.Zero)
                {
                    DestroyWindow(_hWnd);
                    _hWnd = IntPtr.Zero;
                }
            }
            catch
            {
                // 退出路径，忽略
            }
        }

        // ===== Win32 =====

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(
            uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(int dwMessage, ref NotifyIconData pnid);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadImage(IntPtr hInstance, string lpszName, uint uType, int cx, int cy, uint fuLoad);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

        [DllImport("user32.dll")]
        private static extern uint TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        private const uint IMAGE_ICON = 1;
        private const uint LR_LOADFROMFILE = 0x00000010;
        private const uint LR_DEFAULTSIZE = 0x00000040;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public int cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NotifyIconData
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }
    }

    /// <summary>
    /// 开机启动项的注册表读写。放在这里是因为托盘菜单和主窗口都要用，
    /// 而且两边必须以注册表为准 —— 免得一边勾了、另一边的设置文件还写着关闭。
    /// </summary>
    public static class StartupRegistrar
    {
        private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "小K壁纸";

        /// <summary>开机启动时带的参数：只要托盘和自动换壁纸，不要弹主窗口。</summary>
        public const string BackgroundArgument = "/background";

        public static bool IsEnabled()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        public static void Enable()
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, true)
                            ?? throw new InvalidOperationException("打不开开机启动注册表项");

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                throw new InvalidOperationException("取不到程序路径");
            }

            key.SetValue(ValueName, $"\"{exePath}\" {BackgroundArgument}");
        }

        public static void Disable()
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            key?.DeleteValue(ValueName, false);
        }

        /// <summary>切换并返回切换后的状态。</summary>
        public static bool Toggle()
        {
            if (IsEnabled())
            {
                Disable();
                return false;
            }

            Enable();
            return true;
        }
    }
}
