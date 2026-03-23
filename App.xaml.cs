using Microsoft.UI.Xaml;
using System;
using System.Threading;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Linq;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace App1
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        // 单实例检查的Mutex
        private static Mutex? _mutex;
        private const string MUTEX_NAME = "Global\\小K壁纸单实例Mutex";

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            // 自启动时以后台模式启动（仅托盘，不抬起桌面窗口）
            var launchArgs = args?.Arguments ?? string.Empty;
            var startHidden = launchArgs.Contains("--background", System.StringComparison.OrdinalIgnoreCase) || 
                             launchArgs.Contains("/background", System.StringComparison.OrdinalIgnoreCase);

            var currentProcess = Process.GetCurrentProcess();
            System.Diagnostics.Debug.WriteLine($"[启动] PID={currentProcess.Id}, Args='{launchArgs}', startHidden={startHidden}");

            // 检查是否已有实例在运行
            bool createdNew;
            _mutex = new Mutex(true, MUTEX_NAME, out createdNew);
            System.Diagnostics.Debug.WriteLine($"[单实例] createdNew={createdNew}, PID={currentProcess.Id}");

            if (!createdNew)
            {
                bool activated = false;

                if (startHidden)
                {
                    System.Diagnostics.Debug.WriteLine("[单实例] 后台启动参数检测到已有实例，保持后台运行，不激活窗口");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[单实例] 已有实例在运行，激活现有实例");
                    activated = ActivateExistingInstance();
                }

                if (!startHidden && !activated)
                {
                    System.Diagnostics.Debug.WriteLine("[单实例] 唤醒失败，尝试自动清理旧实例并接管运行");
                    if (TryRecoverFromStaleInstance(currentProcess.Id))
                    {
                        System.Diagnostics.Debug.WriteLine("[单实例] 旧实例清理完成，当前实例接管运行");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[单实例] 旧实例清理失败或仍有实例存活，退出当前新进程");
                        Environment.Exit(0);
                        return;
                    }
                }
                else
                {
                    // 重复启动场景必须结束当前新进程，避免后台残留同名进程。
                    System.Diagnostics.Debug.WriteLine("[单实例] 重复启动处理完成，退出当前新进程");
                    Environment.Exit(0);
                    return;
                }
            }

            m_window = new MainWindow(startHidden);
            m_window.Activate();

            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }

        private void OnProcessExit(object? sender, EventArgs e)
        {
            PersistSettingsBeforeExit();
        }

        private void PersistSettingsBeforeExit()
        {
            try
            {
                if (m_window != null)
                {
                    var method = m_window.GetType().GetMethod(
                        "PersistSettingsForSystemExit",
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic);
                    method?.Invoke(m_window, null);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[退出] 兜底保存失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 激活现有实例
        /// </summary>
        private bool ActivateExistingInstance()
        {
            try
            {
                var current = Process.GetCurrentProcess();
                var candidate = Process.GetProcessesByName(current.ProcessName)
                    .Where(p => p.Id != current.Id)
                    .OrderBy(p => p.StartTime)
                    .FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);

                var handle = candidate?.MainWindowHandle ?? IntPtr.Zero;

                if (handle == IntPtr.Zero)
                {
                    // 回退到标题匹配，兼容旧版本窗口。
                    handle = FindWindowByTitle("小K壁纸");
                }

                if (handle != IntPtr.Zero)
                {
                    // 显示窗口
                    ShowWindow(handle, SW_RESTORE);
                    // 激活窗口
                    var foregroundOk = SetForegroundWindow(handle);
                    System.Diagnostics.Debug.WriteLine($"[单实例] 已尝试激活窗口，handle={handle}, foregroundOk={foregroundOk}");
                    return foregroundOk;
                }

                System.Diagnostics.Debug.WriteLine("[单实例] 未找到可激活的窗口句柄");
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[单实例] 激活现有实例失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 清理疑似僵尸旧实例，并尝试让当前实例接管运行。
        /// </summary>
        private bool TryRecoverFromStaleInstance(int currentPid)
        {
            try
            {
                var current = Process.GetCurrentProcess();
                var staleProcesses = Process.GetProcessesByName(current.ProcessName)
                    .Where(p => p.Id != currentPid)
                    .ToList();

                if (staleProcesses.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine("[单实例恢复] 未发现旧实例进程");
                }

                foreach (var process in staleProcesses)
                {
                    try
                    {
                        process.Refresh();
                        System.Diagnostics.Debug.WriteLine($"[单实例恢复] 尝试结束旧实例 PID={process.Id}");

                        if (!process.HasExited)
                        {
                            process.CloseMainWindow();
                            if (!process.WaitForExit(2000))
                            {
                                process.Kill(true);
                                process.WaitForExit(3000);
                            }
                        }

                        System.Diagnostics.Debug.WriteLine($"[单实例恢复] 旧实例已结束 PID={process.Id}");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[单实例恢复] 结束旧实例失败 PID={process.Id}: {ex.Message}");
                    }
                }

                _mutex?.Dispose();
                _mutex = null;

                bool createdNewAfterCleanup;
                _mutex = new Mutex(true, MUTEX_NAME, out createdNewAfterCleanup);
                System.Diagnostics.Debug.WriteLine($"[单实例恢复] createdNewAfterCleanup={createdNewAfterCleanup}");
                return createdNewAfterCleanup;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[单实例恢复] 恢复失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 根据窗口标题查找窗口
        /// </summary>
        private IntPtr FindWindowByTitle(string title)
        {
            return FindWindow(null, title);
        }

        private Window? m_window;

        // Win32 API 声明
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private const int SW_RESTORE = 9;
    }
}
