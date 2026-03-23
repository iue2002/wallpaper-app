using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System;

namespace App1
{
    /// <summary>
    /// 启动页窗口（已废弃，现在使用主窗口内的启动画面）
    /// </summary>
    public sealed partial class SplashWindow : Window
    {
        public SplashWindow()
        {
            this.InitializeComponent();
            ConfigureSplashLogoSource();
        }

        private void ConfigureSplashLogoSource()
        {
            try
            {
                var logoUri = ResolveAssetUri("SplashScreen.png");
                SplashLogoImage.Source = new BitmapImage(logoUri);
            }
            catch
            {
                // 启动画面为辅助窗口，失败时保持静默。
            }
        }

        private static Uri ResolveAssetUri(string fileName)
        {
            try
            {
                _ = Windows.ApplicationModel.Package.Current;
                return new Uri($"ms-appx:///Assets/{fileName}");
            }
            catch
            {
                var localPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
                var normalized = localPath.Replace("\\", "/");
                return new Uri($"file:///{normalized}");
            }
        }
    }
}
