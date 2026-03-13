using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace App1
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
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

            m_window = new MainWindow(startHidden);
            m_window.Activate();
        }

        private Window? m_window;
    }
}
