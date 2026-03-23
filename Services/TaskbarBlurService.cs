using System;
using System.Runtime.InteropServices;
using System.Text;

namespace App1.Services
{
    /// <summary>
    /// 任务栏透明服务 - 基于 TranslucentTB 核心原理
    /// 通过 SetWindowCompositionAttribute API 设置任务栏亚克力/模糊效果
    /// </summary>
    public class TaskbarBlurService : IDisposable
    {
        private IntPtr _taskbarHwnd = IntPtr.Zero;
        private IntPtr _secondTaskbarHwnd = IntPtr.Zero; // Windows 11 可能有第二个任务栏
        private IntPtr _taskbarButtonHwnd = IntPtr.Zero; // 任务栏按钮区域（Windows 11）
        private bool _isEnabled = false;
        private bool _disposed = false;

        // Accent 状态枚举
        private const uint ACCENT_DISABLED = 0;
        private const uint ACCENT_ENABLE_GRADIENT = 1;
        private const uint ACCENT_ENABLE_TRANSPARENTGRADIENT = 2;
        private const uint ACCENT_ENABLE_BLURBEHIND = 3;
        private const uint ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
        private const uint ACCENT_NORMAL = 5;

        // 任务栏类名
        private const string TASKBAR_CLASS_NAME = "Shell_TrayWnd";
        private const string SECOND_TASKBAR_CLASS_NAME = "Shell_SecondaryTrayWnd";
        private const string TASKBAR_BUTTON_CLASS = "TrayButtonClass"; // Windows 11 任务栏按钮
        private const string REBAR_WINDOW_CLASS = "ReBarWindow32";
        private const string MSTASKSW_CLASS = "MSTaskSwWClass";

        /// <summary>
        /// 获取或设置任务栏透明效果是否启用
        /// </summary>
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled != value)
                {
                    _isEnabled = value;
                    ApplyEffect();
                }
            }
        }

        /// <summary>
        /// 启用任务栏透明效果
        /// </summary>
        /// <returns>是否成功</returns>
        public bool Enable()
        {
            try
            {
                // 查找主任务栏
                _taskbarHwnd = FindWindow(TASKBAR_CLASS_NAME, null);
                if (_taskbarHwnd == IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine("[任务栏透明] 未找到主任务栏窗口");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[任务栏透明] 找到主任务栏窗口: {_taskbarHwnd}");

                    // 查找任务栏子窗口（MSTaskSwWClass 是任务栏应用区域）
                    FindChildWindows(_taskbarHwnd);
                }

                // 查找副任务栏（Windows 11 多屏可能使用）
                EnumWindows((hwnd, lParam) =>
                {
                    string className = GetClassName(hwnd);
                    if (className == SECOND_TASKBAR_CLASS_NAME)
                    {
                        _secondTaskbarHwnd = hwnd;
                        System.Diagnostics.Debug.WriteLine($"[任务栏透明] 找到副任务栏: {hwnd}");
                        FindChildWindows(hwnd);
                    }
                    return true;
                }, IntPtr.Zero);

                _isEnabled = true;
                ApplyEffect();

                System.Diagnostics.Debug.WriteLine("[任务栏透明] 已启用任务栏透明效果");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[任务栏透明] 启用失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 查找任务栏的子窗口（Windows 11 需要）
        /// </summary>
        private void FindChildWindows(IntPtr parentHwnd)
        {
            try
            {
                EnumChildWindows(parentHwnd, (hwnd, lParam) =>
                {
                    string className = GetClassName(hwnd);
                    System.Diagnostics.Debug.WriteLine($"[任务栏透明] 子窗口: {hwnd} - {className}");

                    // Windows 11 的任务栏按钮
                    if (className == TASKBAR_BUTTON_CLASS || className == MSTASKSW_CLASS)
                    {
                        _taskbarButtonHwnd = hwnd;
                        System.Diagnostics.Debug.WriteLine($"[任务栏透明] 找到任务栏按钮区域: {hwnd}");
                    }

                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[任务栏透明] 查找子窗口失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 恢复任务栏默认外观
        /// </summary>
        public bool Disable()
        {
            try
            {
                if (_taskbarHwnd == IntPtr.Zero)
                {
                    _taskbarHwnd = FindWindow(TASKBAR_CLASS_NAME, null);
                }

                if (_taskbarHwnd == IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine("[任务栏透明] 未找到任务栏窗口，无法恢复");
                    return false;
                }

                _isEnabled = false;
                SetAccentPolicy(_taskbarHwnd, ACCENT_NORMAL, 0, 0);

                // 同时处理副任务栏
                if (_secondTaskbarHwnd != IntPtr.Zero)
                {
                    SetAccentPolicy(_secondTaskbarHwnd, ACCENT_NORMAL, 0, 0);
                }

                // 发送 DWM 通知让系统重新渲染任务栏
                SendMessage(_taskbarHwnd, WM_DWMCOMPOSITIONCHANGED, IntPtr.Zero, IntPtr.Zero);

                System.Diagnostics.Debug.WriteLine("[任务栏透明] 已恢复任务栏默认外观");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[任务栏透明] 恢复失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 应用当前效果
        /// </summary>
        private void ApplyEffect()
        {
            if (_taskbarHwnd == IntPtr.Zero)
            {
                _taskbarHwnd = FindWindow(TASKBAR_CLASS_NAME, null);
            }

            if (_taskbarHwnd == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine("[任务栏透明] 未找到任务栏窗口");
                return;
            }

            if (_isEnabled)
            {
                // 使用完全透明的渐变效果，ABGR格式
                // 0x20000000 = Alpha=32 (约12.5%透明度)，黑色背景
                // 0x30000000 = Alpha=48 (约19%透明度)
                uint color = 0x30000000;

                // 应用到主任务栏
                SetAccentPolicy(_taskbarHwnd, ACCENT_ENABLE_BLURBEHIND, 2, color);

                // 应用到副任务栏
                if (_secondTaskbarHwnd != IntPtr.Zero)
                {
                    SetAccentPolicy(_secondTaskbarHwnd, ACCENT_ENABLE_BLURBEHIND, 2, color);
                }

                // Windows 11 还需要应用到任务栏按钮区域
                if (_taskbarButtonHwnd != IntPtr.Zero)
                {
                    SetAccentPolicy(_taskbarButtonHwnd, ACCENT_ENABLE_BLURBEHIND, 2, color);
                }

                System.Diagnostics.Debug.WriteLine($"[任务栏透明] 已应用透明效果: 0x{color:X8}");
            }
            else
            {
                SetAccentPolicy(_taskbarHwnd, ACCENT_NORMAL, 0, 0);

                if (_secondTaskbarHwnd != IntPtr.Zero)
                {
                    SetAccentPolicy(_secondTaskbarHwnd, ACCENT_NORMAL, 0, 0);
                }

                if (_taskbarButtonHwnd != IntPtr.Zero)
                {
                    SetAccentPolicy(_taskbarButtonHwnd, ACCENT_NORMAL, 0, 0);
                }

                SendMessage(_taskbarHwnd, WM_DWMCOMPOSITIONCHANGED, IntPtr.Zero, IntPtr.Zero);
            }
        }

        /// <summary>
        /// 设置窗口的 Accent Policy
        /// </summary>
        private void SetAccentPolicy(IntPtr hwnd, uint accentState, uint accentFlags, uint gradientColor)
        {
            var accent = new ACCENT_POLICY
            {
                AccentState = accentState,
                AccentFlags = accentFlags,
                GradientColor = gradientColor,
                AnimationId = 0
            };

            var data = new WINDOWCOMPOSITIONATTRIBDATA
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = accent,
                SizeOfData = (uint)Marshal.SizeOf(typeof(ACCENT_POLICY))
            };

            SetWindowCompositionAttribute(hwnd, ref data);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                if (_isEnabled)
                {
                    Disable();
                }
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }

        ~TaskbarBlurService()
        {
            Dispose();
        }

        #region Win32 API

        // 任务栏查找
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const uint WM_DWMCOMPOSITIONCHANGED = 0x031E;

        /// <summary>
        /// 获取窗口类名
        /// </summary>
        private static string GetClassName(IntPtr hWnd)
        {
            const int nMaxCount = 256;
            StringBuilder className = new StringBuilder(nMaxCount);
            GetClassName(hWnd, className, nMaxCount);
            return className.ToString();
        }

        // 核心 API: SetWindowCompositionAttribute (user32.dll 未公开API)
        [DllImport("user32.dll")]
        private static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);

        private const int WCA_ACCENT_POLICY = 19;

        // Accent Policy 结构体
        [StructLayout(LayoutKind.Sequential)]
        private struct ACCENT_POLICY
        {
            public uint AccentState;
            public uint AccentFlags;
            public uint GradientColor;
            public uint AnimationId;
        }

        // Window Composition Attribute Data 结构体
        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWCOMPOSITIONATTRIBDATA
        {
            public int Attribute;
            public ACCENT_POLICY Data;
            public uint SizeOfData;
        }

        #endregion
    }
}
