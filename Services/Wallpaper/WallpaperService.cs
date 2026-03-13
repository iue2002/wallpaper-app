using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace App1.Services.WallpaperSetter
{
    /// <summary>
    /// 壁纸设置服务
    /// </summary>
    public class WallpaperService
    {
        // Windows IDesktopWallpaper COM接口（推荐方式）
        [ComImport]
        [Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDesktopWallpaper
        {
            void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
            [return: MarshalAs(UnmanagedType.LPWStr)]
            string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID);
            [return: MarshalAs(UnmanagedType.LPWStr)]
            string GetMonitorDevicePathAt(uint monitorIndex);
            uint GetMonitorDevicePathCount();
            void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorID, out RECT displayRect);
            void SetBackgroundColor(uint color);
            uint GetBackgroundColor();
            void SetPosition(DesktopWallpaperPosition position);
            DesktopWallpaperPosition GetPosition();
            void SetSlideshow(IntPtr items);
            IntPtr GetSlideshow();
            void SetSlideshowOptions(uint options, uint slideshowTick);
            void GetSlideshowOptions(out uint options, out uint slideshowTick);
            void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string monitorID, DesktopSlideshowDirection direction);
            void GetStatus(out DesktopSlideshowState state);
            void Enable(bool enable);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private enum DesktopWallpaperPosition
        {
            Center = 0,
            Tile = 1,
            Stretch = 2,
            Fit = 3,
            Fill = 4,
            Span = 5
        }

        private enum DesktopSlideshowDirection
        {
            Forward = 0,
            Backward = 1
        }

        private enum DesktopSlideshowState
        {
            Enabled = 0x01,
            Slideshow = 0x02,
            DisabledByRemoteSession = 0x04
        }

        [ComImport]
        [Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")]
        private class DesktopWallpaperClass
        {
        }

        // 备用方法的Windows API
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
        
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);
        
        private const int HWND_BROADCAST = 0xFFFF;
        private const uint WM_SETTINGCHANGE = 0x001A;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private const int SPI_SETDESKWALLPAPER = 0x0014;
        private const int SPIF_UPDATEINIFILE = 0x01;
        private const int SPIF_SENDCHANGE = 0x02;

        private readonly HttpClient _httpClient;
        private readonly string _wallpaperFolder;

        public WallpaperService()
        {
            // 配置 HttpClientHandler 禁用代理
            var handler = new HttpClientHandler
            {
                UseProxy = false,
                Proxy = null
            };
            
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(60)  // 增加超时时间到60秒
            };
            
            // 创建壁纸缓存文件夹 - 使用Pictures文件夹，Windows壁纸API可以访问
            var picturesFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            _wallpaperFolder = Path.Combine(picturesFolder, "小K壁纸");
            
            if (!Directory.Exists(_wallpaperFolder))
            {
                Directory.CreateDirectory(_wallpaperFolder);
            }
            
            System.Diagnostics.Debug.WriteLine($"[壁纸设置] 壁纸文件夹: {_wallpaperFolder}");
        }

        /// <summary>
        /// 设置桌面壁纸（优先使用本地缓存）
        /// </summary>
        public async Task<bool> SetWallpaperAsync(string imageUrl, string title, string? localCachePath = null)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[壁纸设置] 开始设置壁纸: {title}");
                
                // 1. 清理旧壁纸文件（保留最近3个，节省空间）
                CleanupOldWallpapers(3);
                
                string localPath;
                
                // 2. 优先使用已缓存的本地图片
                if (!string.IsNullOrEmpty(localCachePath) && File.Exists(localCachePath))
                {
                    localPath = localCachePath;
                    System.Diagnostics.Debug.WriteLine($"[壁纸设置] ✅ 使用已缓存的图片: {localPath}");
                }
                else
                {
                    // 3. 如果没有缓存，则下载图片
                    localPath = await DownloadImageAsync(imageUrl, title);
                    if (string.IsNullOrEmpty(localPath))
                    {
                        System.Diagnostics.Debug.WriteLine("[壁纸设置] ❌ 下载图片失败");
                        return false;
                    }
                    System.Diagnostics.Debug.WriteLine($"[壁纸设置] 图片已下载到: {localPath}");
                }

                // 3. 验证文件存在
                if (!File.Exists(localPath))
                {
                    System.Diagnostics.Debug.WriteLine($"[壁纸设置] ❌ 文件不存在: {localPath}");
                    return false;
                }
                
                var fileInfo = new FileInfo(localPath);
                System.Diagnostics.Debug.WriteLine($"[壁纸设置] 文件大小: {fileInfo.Length / 1024} KB");

                // 4. 使用IDesktopWallpaper COM接口设置壁纸（Windows 8+推荐方式）
                // 确保路径格式正确（使用反斜杠，并且是完整路径）
                var normalizedPath = Path.GetFullPath(localPath).Replace("/", "\\");
                System.Diagnostics.Debug.WriteLine($"[壁纸设置] 规范化路径: {normalizedPath}");
                System.Diagnostics.Debug.WriteLine($"[壁纸设置] 文件存在检查: {File.Exists(normalizedPath)}");
                
                bool comSuccess = false;
                
                // 尝试使用 COM 接口
                try
                {
                    System.Diagnostics.Debug.WriteLine("[壁纸设置] 尝试使用 COM 接口（IDesktopWallpaper）...");
                    var wallpaper = (IDesktopWallpaper)new DesktopWallpaperClass();
                    
                    // 先设置壁纸样式为填充（Fill）
                    wallpaper.SetPosition(DesktopWallpaperPosition.Fill);
                    System.Diagnostics.Debug.WriteLine("[壁纸设置] ✅ 壁纸样式设置为填充模式");
                    
                    // 设置壁纸（null表示所有显示器）
                    wallpaper.SetWallpaper(null!, normalizedPath);
                    System.Diagnostics.Debug.WriteLine("[壁纸设置] ✅ COM 接口调用成功");
                    
                    // 释放COM对象
                    Marshal.ReleaseComObject(wallpaper);
                    
                    comSuccess = true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[壁纸设置] ❌ COM 接口失败: {ex.GetType().Name}");
                    System.Diagnostics.Debug.WriteLine($"[壁纸设置] 错误消息: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"[壁纸设置] HRESULT: 0x{Marshal.GetHRForException(ex):X8}");
                    comSuccess = false;
                }
                
                // 无论 COM 是否成功，都使用备用方法加强（Win10 兼容性）
                if (!comSuccess)
                {
                    System.Diagnostics.Debug.WriteLine($"[壁纸设置] 使用备用方法...");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[壁纸设置] COM 成功，使用备用方法加强设置...");
                }
                
                SetWallpaperFallback(normalizedPath);
                
                // 等待系统应用更改
                await Task.Delay(1500);

                System.Diagnostics.Debug.WriteLine("[壁纸设置] ✅ 壁纸设置成功");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[壁纸设置] ❌ 设置失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 刷新Explorer进程，强制应用壁纸更改
        /// </summary>
        private void RefreshExplorer()
        {
            try
            {
                // 广播设置更改消息给所有窗口
                IntPtr result;
                SendMessageTimeout(
                    new IntPtr(HWND_BROADCAST), 
                    WM_SETTINGCHANGE, 
                    IntPtr.Zero, 
                    Marshal.StringToHGlobalUni("Environment"),
                    SMTO_ABORTIFHUNG, 
                    5000, 
                    out result
                );
                
                System.Diagnostics.Debug.WriteLine("[壁纸设置] 已刷新Explorer");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[壁纸设置] 刷新Explorer失败: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 备用方法：使用注册表 + SystemParametersInfo 设置壁纸
        /// </summary>
        private void SetWallpaperFallback(string imagePath)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[壁纸设置] 使用备用方法（SystemParametersInfo + 注册表）");
                
                // 方法1: SystemParametersInfo（最可靠）
                try
                {
                    int result = SystemParametersInfo(
                        SPI_SETDESKWALLPAPER,
                        0,
                        imagePath,
                        SPIF_UPDATEINIFILE | SPIF_SENDCHANGE
                    );
                    
                    if (result != 0)
                    {
                        System.Diagnostics.Debug.WriteLine("[壁纸设置] ✅ SystemParametersInfo 设置成功");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[壁纸设置] ⚠️ SystemParametersInfo 返回失败，错误码: {Marshal.GetLastWin32Error()}");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[壁纸设置] ❌ SystemParametersInfo 异常: {ex.Message}");
                }
                
                // 方法2: 注册表（补充）
                try
                {
                    using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true))
                    {
                        if (key != null)
                        {
                            key.SetValue("Wallpaper", imagePath);
                            key.SetValue("WallpaperStyle", "10");  // 10 = 填充
                            key.SetValue("TileWallpaper", "0");
                            key.Flush();
                            System.Diagnostics.Debug.WriteLine("[壁纸设置] ✅ 注册表更新成功");
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[壁纸设置] ⚠️ 注册表更新失败: {ex.Message}");
                }
                
                // 强制刷新
                RefreshExplorer();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[壁纸设置] ❌ 备用方法失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 下载图片到本地（带缓存检查）
        /// </summary>
        private async Task<string> DownloadImageAsync(string imageUrl, string title)
        {
            try
            {
                // 使用URL的哈希作为文件名
                var urlHash = Math.Abs(imageUrl.GetHashCode()).ToString();
                var fileName = $"wallpaper_{urlHash}.jpg";
                var localPath = Path.GetFullPath(Path.Combine(_wallpaperFolder, fileName));  // 确保是绝对路径

                // 检查是否已经缓存
                if (File.Exists(localPath))
                {
                    System.Diagnostics.Debug.WriteLine($"[壁纸设置] 使用缓存图片: {localPath}");
                    return localPath;
                }

                System.Diagnostics.Debug.WriteLine($"[壁纸设置] 开始下载图片...");
                System.Diagnostics.Debug.WriteLine($"[壁纸设置] 图片URL: {imageUrl}");
                
                // 下载图片
                var imageBytes = await _httpClient.GetByteArrayAsync(imageUrl);
                await File.WriteAllBytesAsync(localPath, imageBytes);
                
                System.Diagnostics.Debug.WriteLine($"[壁纸设置] 图片下载完成: {imageBytes.Length / 1024} KB");
                System.Diagnostics.Debug.WriteLine($"[壁纸设置] 保存路径: {localPath}");
                
                return localPath;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[壁纸设置] 下载图片失败: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[壁纸设置] 异常堆栈: {ex.StackTrace}");
                return string.Empty;
            }
        }

        /// <summary>
        /// 清理旧的壁纸文件（保留最近N个）
        /// </summary>
        public void CleanupOldWallpapers(int keepCount = 30)
        {
            try
            {
                var jpgFiles = Directory.GetFiles(_wallpaperFolder, "*.jpg");
                var bmpFiles = Directory.GetFiles(_wallpaperFolder, "*.bmp");
                
                var allFiles = jpgFiles.Concat(bmpFiles)
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.CreationTime)
                    .ToArray();

                // 保留最近N个文件，删除其他
                if (allFiles.Length > keepCount)
                {
                    for (int i = keepCount; i < allFiles.Length; i++)
                    {
                        allFiles[i].Delete();
                    }
                    System.Diagnostics.Debug.WriteLine($"[壁纸设置] 清理了 {allFiles.Length - keepCount} 个旧壁纸文件");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[壁纸设置] 清理失败: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 清理所有壁纸文件
        /// </summary>
        public void CleanupAllWallpapers()
        {
            try
            {
                var jpgFiles = Directory.GetFiles(_wallpaperFolder, "*.jpg");
                var bmpFiles = Directory.GetFiles(_wallpaperFolder, "*.bmp");
                
                var allFiles = jpgFiles.Concat(bmpFiles).ToArray();
                
                foreach (var file in allFiles)
                {
                    File.Delete(file);
                }
                
                System.Diagnostics.Debug.WriteLine($"[壁纸设置] 已清理所有壁纸文件，共 {allFiles.Length} 个");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[壁纸设置] 清理失败: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 设置锁屏界面壁纸
        /// </summary>
        public async Task<bool> SetLockScreenAsync(string imageUrl, string title = "LockScreen")
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[锁屏设置] 开始设置锁屏界面...");
                
                // 1. 下载图片到本地
                var localPath = await DownloadImageAsync(imageUrl, title);
                if (string.IsNullOrEmpty(localPath))
                {
                    System.Diagnostics.Debug.WriteLine("[锁屏设置] 图片下载失败");
                    return false;
                }
                
                // 2. 设置锁屏界面
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(localPath);
                await Windows.System.UserProfile.LockScreen.SetImageFileAsync(file);
                
                System.Diagnostics.Debug.WriteLine($"[锁屏设置] ✅ 锁屏界面设置成功");
                return true;
            }
            catch (UnauthorizedAccessException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[锁屏设置] ❌ 权限不足: {ex.Message}");
                return false;
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[锁屏设置] ❌ COM异常: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[锁屏设置] 错误代码: 0x{ex.HResult:X8}");
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[锁屏设置] ❌ 设置失败: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[锁屏设置] 异常类型: {ex.GetType().Name}");
                return false;
            }
        }
    }
}
