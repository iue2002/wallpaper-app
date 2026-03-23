using System;
using System.Runtime.InteropServices;
using System.Text;

namespace App1.Services
{
    /// <summary>
    /// 任务栏透明服务 - 参考 TranslucentTB 实现
    /// </summary>
    public class TaskbarBlurServiceV2 : IDisposable
    {
        private IntPtr _taskbarHwnd = IntPtr.Zero;
        private IntPtr _secondTaskbarHwnd = IntPtr.Zero;
        private IntPtr _workerWHwnd = IntPtr.Zero; // WorkerW - Win10 任务栏
        private bool _isEnabled = false;
        private bool _disposed = false;

        // Accent 状态枚举
        private const uint ACCENT_ENABLE_BLURBEHIND = 3;
        private const uint ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
        private const uint ACCENT_NORMAL = 5;

        // 任务栏类名
        private const string TASKBAR_CLASS_NAME = "Shell_TrayWnd";
        private const string SECOND_TASKBAR_CLASS_NAME = "Shell_SecondaryTrayWnd";
        private const string WORKERW_CLASS_NAME = "WorkerW"; // Win10 任务栏
        private const string REBAR_CLASS_NAME = "ReBarWindow32"; // Win11 任务栏

        // SetWindowCompositionAttribute 函数
        private delegate bool SetWindowCompositionAttributeDelegate(IntPtr hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);
        private static SetWindowCompositionAttributeDelegate? _setWindowCompositionAttribute;

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
        /// 静态构造函数 - 加载API
        /// </summary>
        static TaskbarBlurServiceV2()
        {
            try
            {
                var user32 = LoadLibrary("user32.dll");
                if (user32 != IntPtr.Zero)
                {
                    _setWindowCompositionAttribute = (SetWindowCompositionAttributeDelegate)Marshal.GetDelegateForFunctionPointer(
                        GetProcAddress(user32, "SetWindowCompositionAttribute"),
                        typeof(SetWindowCompositionAttributeDelegate)
                    );
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[任务栏透明] 加载API失败: {ex.Message}");
            }
        }

        public bool Enable()
        {
            try
            {
                // 查找主任务栏
                _taskbarHwnd = FindWindow(TASKBAR_CLASS_NAME, null);
                if (_taskbarHwnd == IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine("[任务栏透明] 未找到主任务栏窗口");
                    return false;
                }

                System.Diagnostics.Debug.WriteLine($"[任务栏透明] 找到主任务栏: {_taskbarHwnd}");

                // 查找副任务栏
                EnumWindows((hwnd, lParam) =>
                {
                    string className = GetClassName(hwnd);
                    if (className == SECOND_TASKBAR_CLASS_NAME)
                    {
                        _secondTaskbarHwnd = hwnd;
                        System.Diagnostics.Debug.WriteLine($"[任务栏透明] 找到副任务栏: {hwnd}");
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

        public bool Disable()
        {
            try
            {
                if (_taskbarHwnd == IntPtr.Zero)
                {
                    _taskbarHwnd = FindWindow(TASKBAR_CLASS_NAME, null);
                }

                _isEnabled = false;
                ApplyEffect();

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
            if (_setWindowCompositionAttribute == null)
            {
                System.Diagnostics.Debug.WriteLine("[任务栏透明] API未加载");
                return;
            }

            if (_taskbarHwnd == IntPtr.Zero)
            {
                _taskbarHwnd = FindWindow(TASKBAR_CLASS_NAME, null);
            }

            if (_taskbarHwnd == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine("[任务栏透明] 未找到任务栏窗口");
                return;
            }

            // 遍历所有子窗口
            EnumChildWindows(_taskbarHwnd, (hwnd, lParam) =>
            {
                string className = GetClassName(hwnd);
                System.Diagnostics.Debug.WriteLine($"[任务栏透明] 子窗口: {hwnd} - {className}");

                if (_isEnabled)
                {
                    // 应用透明效果
                    uint color = 0x00000000; // 完全透明
                    SetAccent(hwnd, ACCENT_ENABLE_BLURBEHIND, 2, color);
                }
                else
                {
                    // 恢复默认
                    SetAccent(hwnd, ACCENT_NORMAL, 0, 0);
                }

                return true;
            }, IntPtr.Zero);

            if (_secondTaskbarHwnd != IntPtr.Zero)
            {
                EnumChildWindows(_secondTaskbarHwnd, (hwnd, lParam) =>
                {
                    if (_isEnabled)
                    {
                        uint color = 0x00000000;
                        SetAccent(hwnd, ACCENT_ENABLE_BLURBEHIND, 2, color);
                    }
                    else
                    {
                        SetAccent(hwnd, ACCENT_NORMAL, 0, 0);
                    }
                    return true;
                }, IntPtr.Zero);
            }

            // 最后发送DWM变化通知
            SendMessage(_taskbarHwnd, WM_DWMCOMPOSITIONCHANGED, 0, 0);
        }

        /// <summary>
        /// 设置窗口的 Accent Policy
        /// </summary>
        private void SetAccent(IntPtr hwnd, uint accentState, uint accentFlags, uint gradientColor)
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

            _setWindowCompositionAttribute?.Invoke(hwnd, ref data);
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

        ~TaskbarBlurServiceV2()
        {
            Dispose();
        }

        #region Win32 API

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        private const uint WM_DWMCOMPOSITIONCHANGED = 0x031E;

        private static string GetClassName(IntPtr hWnd)
        {
            const int nMaxCount = 256;
            StringBuilder className = new StringBuilder(nMaxCount);
            GetClassNameW(hWnd, className, nMaxCount);
            return className.ToString();
        }

        private const int WCA_ACCENT_POLICY = 19;

        [StructLayout(LayoutKind.Sequential)]
        private struct ACCENT_POLICY
        {
            public uint AccentState;
            public uint AccentFlags;
            public uint GradientColor;
            public uint AnimationId;
        }

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
