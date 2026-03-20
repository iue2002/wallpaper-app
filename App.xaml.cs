using Microsoft.UI.Xaml;
using System;
using System.Threading;
using System.Runtime.InteropServices;

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
        private const string MUTEX_NAME = "小K壁纸单实例Mutex";

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

            // 检查是否已有实例在运行
            bool createdNew;
            _mutex = new Mutex(true, MUTEX_NAME, out createdNew);

            if (!createdNew)
            {
                if (startHidden)
                {
                    System.Diagnostics.Debug.WriteLine("[单实例] 后台启动参数检测到已有实例，保持后台运行，不激活窗口");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[单实例] 已有实例在运行，激活现有实例");
                    ActivateExistingInstance();
                }
                return;
            }

            m_window = new MainWindow(startHidden);
            m_window.Activate();
        }

        /// <summary>
        /// 激活现有实例
        /// </summary>
        private void ActivateExistingInstance()
        {
            try
            {
                // 查找并激活现有窗口
                var handle = FindWindowByTitle("小K壁纸");
                if (handle != IntPtr.Zero)
                {
                    // 显示窗口
                    ShowWindow(handle, SW_RESTORE);
                    // 激活窗口
                    SetForegroundWindow(handle);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[单实例] 激活现有实例失败: {ex.Message}");
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
