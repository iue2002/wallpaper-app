using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace App1.Helpers
{
    /// <summary>
    /// 系统托盘管理器
    /// </summary>
    public class TrayManager : IDisposable
    {
        private Window _window;
        private bool _disposed = false;
        private IntPtr _hWnd;
        private NotifyIconData _nid;
        private bool _iconAdded = false;
        private Action? _exitAction;

        // 原始窗口过程指针（用于还原）
        private IntPtr _prevWndProc = IntPtr.Zero;
        private WndProcDelegate? _wndProcDelegate;

        // 消息常量
        private const int WM_USER = 0x0400;
        private const int WM_TRAYICON = WM_USER + 1;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_LBUTTONDBLCLK = 0x0203;

        // 托盘图标消息
        private const int NIM_ADD = 0x00000000;
        private const int NIM_MODIFY = 0x00000001;
        private const int NIM_DELETE = 0x00000002;

        /// <summary>
        /// 初始化系统托盘管理器
        /// </summary>
        public TrayManager(Window window, Action exitAction)
        {
            _window = window;
            _exitAction = exitAction;
            _hWnd = WinRT.Interop.WindowNative.GetWindowHandle(window);

            // 子类化窗口以接收托盘回调消息
            TrySubclassWindow();

            InitializeTrayIcon();
        }

        private void TrySubclassWindow()
        {
            try
            {
                _wndProcDelegate = new WndProcDelegate(WndProc);
                // 设置新的窗口过程，并保存旧的
                _prevWndProc = SetWindowLongPtr(_hWnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[托盘] 子类化窗口失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 初始化系统托盘图标
        /// </summary>
        private void InitializeTrayIcon()
        {
            try
            {
                // 初始化托盘图标数据
                _nid = new NotifyIconData();
                _nid.cbSize = Marshal.SizeOf(typeof(NotifyIconData));
                _nid.hWnd = _hWnd;
                _nid.uID = 1;
                // 包含消息回调，这样托盘图标的鼠标事件会发送到窗口
                _nid.uFlags = NIF_ICON | NIF_TIP | NIF_MESSAGE;
                _nid.uCallbackMessage = WM_TRAYICON;
                _nid.szTip = "小K壁纸";

                // 加载图标
                string iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "app.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    // 使用 LoadImage 加载图标，支持透明
                    _nid.hIcon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
                }
                else
                {
                    // 使用默认图标
                    _nid.hIcon = LoadIcon(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION
                }

                // 添加托盘图标
                _iconAdded = Shell_NotifyIcon(NIM_ADD, ref _nid);
                if (_iconAdded)
                {
                    System.Diagnostics.Debug.WriteLine("[托盘] 系统托盘图标已添加");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[托盘] 系统托盘图标添加失败");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[托盘] 初始化失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 显示托盘通知
        /// </summary>
        public void ShowNotification(string title, string message)
        {
            try
            {
                if (_iconAdded)
                {
                    _nid.uFlags = NIF_INFO;
                    _nid.szInfo = message;
                    _nid.szInfoTitle = title;
                    _nid.dwInfoFlags = NIIF_INFO;

                    Shell_NotifyIcon(NIM_MODIFY, ref _nid);
                    System.Diagnostics.Debug.WriteLine($"[托盘通知] {title}: {message}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[托盘通知] 显示失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 显示主窗口
        /// </summary>
        public void ShowWindow()
        {
            _window.AppWindow.Show();
            _window.Activate();
        }

        /// <summary>
        /// 隐藏主窗口
        /// </summary>
        public void HideWindow()
        {
            _window.AppWindow.Hide();
        }

        /// <summary>
        /// 完全退出应用程序
        /// </summary>
        public void ExitApplication()
        {
            _exitAction?.Invoke();
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // 移除托盘图标
                    if (_iconAdded)
                    {
                        _nid.uFlags = 0;
                        Shell_NotifyIcon(NIM_DELETE, ref _nid);
                        _iconAdded = false;
                        System.Diagnostics.Debug.WriteLine("[托盘] 系统托盘图标已移除");
                    }

                    // 释放图标
                    if (_nid.hIcon != IntPtr.Zero)
                    {
                        DestroyIcon(_nid.hIcon);
                        _nid.hIcon = IntPtr.Zero;
                    }

                    // 还原窗口过程
                    if (_prevWndProc != IntPtr.Zero)
                    {
                        SetWindowLongPtr(_hWnd, GWLP_WNDPROC, _prevWndProc);
                        _prevWndProc = IntPtr.Zero;
                    }
                }

                _disposed = true;
            }
        }

        /// <summary>
        /// 析构函数
        /// </summary>
        ~TrayManager()
        {
            Dispose(false);
        }

        // 窗口过程委托
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        // 窗口过程实现：处理托盘图标回调消息
        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (msg == WM_TRAYICON && wParam.ToInt32() == _nid.uID)
                {
                    var message = (int)lParam;
                    if (message == WM_RBUTTONUP)
                    {
                        ShowContextMenu();
                        return IntPtr.Zero;
                    }
                    else if (message == WM_LBUTTONDBLCLK)
                    {
                        // 双击显示窗口
                        ShowWindow();
                        return IntPtr.Zero;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[托盘] 窗口过程异常: {ex.Message}");
            }

            // 调用原始窗口过程
            return CallWindowProc(_prevWndProc, hWnd, msg, wParam, lParam);
        }

        // 显示右键菜单
        private void ShowContextMenu()
        {
            try
            {
                // 菜单项 ID
                const uint ID_SHOW = 1000;
                const uint ID_EXIT = 1001;

                IntPtr hMenu = CreatePopupMenu();
                if (hMenu == IntPtr.Zero) return;

                // 插入项
                AppendMenu(hMenu, MF_STRING, ID_SHOW, "显示主窗口");
                AppendMenu(hMenu, MF_STRING, ID_EXIT, "退出");

                // 获取鼠标位置
                GetCursorPos(out POINT pt);

                // 需要把窗口设为前台，否则菜单可能不能正确消失
                SetForegroundWindow(_hWnd);

                // 显示并返回选择的命令
                uint cmd = TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_LEFTALIGN | TPM_TOPALIGN, pt.X, pt.Y, _hWnd, IntPtr.Zero);

                // 销毁菜单
                DestroyMenu(hMenu);

                if (cmd == ID_SHOW)
                {
                    ShowWindow();
                }
                else if (cmd == ID_EXIT)
                {
                    ExitApplication();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[托盘] 显示右键菜单失败: {ex.Message}");
            }
        }

        // Win32 API 定义
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern bool Shell_NotifyIcon(int dwMessage, ref NotifyIconData pnid);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr LoadImage(IntPtr hInstance, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        // Create/Show menu
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern uint TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        // 常量定义
        private const int NIF_ICON = 0x00000002;
        private const int NIF_TIP = 0x00000004;
        private const int NIF_INFO = 0x00000010;
        private const int NIF_MESSAGE = 0x00000001;
        private const int NIIF_INFO = 0x00000001;

        // LoadImage 常量
        private const uint IMAGE_ICON = 1;
        private const uint LR_LOADFROMFILE = 0x00000010;
        private const uint LR_DEFAULTSIZE = 0x00000040;

        // TrackPopupMenu flags
        private const uint TPM_RETURNCMD = 0x0100;
        private const uint TPM_NONOTIFY = 0x0080;
        private const uint TPM_LEFTALIGN = 0x0000;
        private const uint TPM_TOPALIGN = 0x0000;

        // AppendMenu flags
        private const uint MF_STRING = 0x00000000;

        // SetWindowLongPtr index
        private const int GWLP_WNDPROC = -4;

        // 托盘图标数据结构
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
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
}