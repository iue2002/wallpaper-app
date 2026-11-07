using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using App1.Services.Diagnostics;

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
        private const uint SMTO_BLOCK = 0x0001;

        /// <summary>
        /// 广播设置变更时的等待上限。这个调用要等系统里每一个顶层窗口应答，
        /// 给太长等于把命交给别人 —— 只要有一个窗口卡住，这边就跟着卡。
        /// </summary>
        private const uint RefreshTimeoutMs = 1000;

        private readonly HttpClient _httpClient;
        private readonly string _wallpaperFolder;
        /// <summary>
        /// 换壁纸串行化。COM 的 IDesktopWallpaper 与 SystemParametersInfo 都会去动
        /// Explorer，快速连点多张时并发调用容易把 Explorer 拖成无响应，界面就假死了。
        /// </summary>
        private readonly SemaphoreSlim _applyLock = new(1, 1);

        public WallpaperService()
        {
            // SocketsHttpHandler 而不是 HttpClientHandler：需要 KeepAlivePing 来识别
            // “连接还在、但对端已经不吐数据”的半死 TCP。普通超时管不到这种状态。
            var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                Proxy = null,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),

                // 请求活跃但 20 秒没有任何数据往返，就发一次 ping；15 秒还没回应，
                // 判定连接已死、直接丢掉。没有这个，半死连接会一直挂到进程退出。
                KeepAlivePingDelay = TimeSpan.FromSeconds(20),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(15),
                KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests
            };

            _httpClient = new HttpClient(handler)
            {
                // 这个超时只管到「拿到响应头」。响应体是流，要另设总超时，
                // 见 DownloadImageAsync 里的 downloadCts。
                Timeout = TimeSpan.FromSeconds(60)
            };

            // 创建壁纸缓存文件夹 - 使用Pictures文件夹，Windows壁纸API可以访问
            var picturesFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            _wallpaperFolder = Path.Combine(picturesFolder, "小K壁纸");

            if (!Directory.Exists(_wallpaperFolder))
            {
                Directory.CreateDirectory(_wallpaperFolder);
            }

            CleanupStalePartFiles();

            AppLog.Write($"[壁纸设置] 壁纸文件夹: {_wallpaperFolder}");
        }

        /// <summary>
        /// 清掉上次没下完的半截文件。
        ///
        /// 正常失败路径会在 finally 里删掉 .part，但进程被强杀、或者 await 永久挂住时
        /// 走不到 finally —— 11-07 那次就在缓存目录里留了一个 1.7MB 的 .part 躺了两小时。
        /// 这些东西没有任何价值（下载完整了才会改名），启动时一律清掉。
        /// </summary>
        private void CleanupStalePartFiles()
        {
            try
            {
                var parts = Directory.GetFiles(_wallpaperFolder, "*.part");
                foreach (var part in parts)
                {
                    try { File.Delete(part); } catch { }
                }

                if (parts.Length > 0)
                {
                    AppLog.Write($"[壁纸设置] 已清理 {parts.Length} 个未完成的 .part 文件");
                }
            }
            catch (Exception ex)
            {
                AppLog.Write($"[壁纸设置] 清理 .part 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 设置桌面壁纸（优先使用本地缓存）
        /// </summary>
        public async Task<bool> SetWallpaperAsync(string imageUrl, string title, string? localCachePath = null)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[壁纸设置] 开始设置壁纸: {title}");

                string localPath;
                
                // 1. 优先使用已缓存的本地图片
                if (!string.IsNullOrEmpty(localCachePath) && File.Exists(localCachePath))
                {
                    localPath = localCachePath;
                    System.Diagnostics.Debug.WriteLine($"[壁纸设置] ✅ 使用已缓存的图片: {localPath}");
                }
                else
                {
                    // 2. 如果没有缓存，则下载图片
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

                // 4. 规范成完整路径（反斜杠格式），系统接口对路径写法比较挑
                var normalizedPath = Path.GetFullPath(localPath).Replace("/", "\\");
                System.Diagnostics.Debug.WriteLine($"[壁纸设置] 规范化路径: {normalizedPath}");
                System.Diagnostics.Debug.WriteLine($"[壁纸设置] 文件存在检查: {File.Exists(normalizedPath)}");
                
                // 真正的落地动作（COM / SystemParametersInfo / 注册表）全部丢到后台线程。
                // 这几个 API 都是同步阻塞的：SystemParametersInfo 带 SPIF_SENDCHANGE 时会
                // 向系统里所有顶层窗口广播一次并逐个等待应答，Explorer 稍一卡顿，
                // 界面就跟着一起假死 —— 这正是"换壁纸时直接卡死"的来源。
                var applied = false;

                // 串行执行，并给等待设上限。上一个请求万一卡在系统调用里，
                // 也不能让后面的请求无限期干等。
                if (await _applyLock.WaitAsync(TimeSpan.FromSeconds(3)))
                {
                    try
                    {
                        // 12 秒是硬上限：系统调用不返回也不再陪着等，
                        // 否则"正在设置壁纸"会一直挂着，看着就像整个软件卡死了。
                        // 超时返回 default(bool) = false，按设置失败处理。
                        applied = await RunOnStaThreadAsync(
                            () => ApplyWallpaperCore(normalizedPath),
                            TimeSpan.FromSeconds(12));
                    }
                    finally
                    {
                        _applyLock.Release();
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[壁纸设置] ⚠️ 上一个设置请求还没结束，放弃本次");
                    return false;
                }

                // 旧缓存清理放后台慢慢做，别挡着返回；排除掉正在使用的这一张
                _ = Task.Run(() => CleanupOldWallpapers(30, normalizedPath));

                System.Diagnostics.Debug.WriteLine(applied
                    ? "[壁纸设置] ✅ 壁纸设置成功"
                    : "[壁纸设置] ⚠️ 壁纸设置未确认成功");

                return applied;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[壁纸设置] ❌ 设置失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 广播系统设置变更。
        /// 注意：这个调用会同步等待系统里每一个顶层窗口应答，只能在后台线程调用，
        /// 而且超时要给得很短，否则一个挂起的窗口就能拖住整个界面。
        /// </summary>
        private void RefreshExplorer()
        {
            var lParam = IntPtr.Zero;
            try
            {
                // 参数是"什么变了"的名字，壁纸对应 SPI_SETDESKWALLPAPER。
                // 原来传的是 "Environment"（环境变量变更），跟壁纸毫无关系，
                // 等于每次换壁纸都白广播一趟。
                lParam = Marshal.StringToHGlobalUni("SPI_SETDESKWALLPAPER");

                SendMessageTimeout(
                    new IntPtr(HWND_BROADCAST),
                    WM_SETTINGCHANGE,
                    IntPtr.Zero,
                    lParam,
                    SMTO_ABORTIFHUNG | SMTO_BLOCK,
                    RefreshTimeoutMs,
                    out _);

                System.Diagnostics.Debug.WriteLine("[壁纸设置] 已广播设置变更");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[壁纸设置] 广播失败: {ex.Message}");
            }
            finally
            {
                // 原来这块非托管内存从不释放，每换一次壁纸漏一块
                if (lParam != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(lParam);
                }
            }
        }
        
        /// <summary>
        /// 备用方法：使用注册表 + SystemParametersInfo 设置壁纸
        /// </summary>
        /// <summary>
        /// 备用方法：使用注册表 + SystemParametersInfo 设置壁纸。
        /// 全程同步阻塞，只允许在后台线程调用。
        /// </summary>
        private bool SetWallpaperFallback(string imagePath)
        {
            var spiSuccess = false;

            try
            {
                System.Diagnostics.Debug.WriteLine("[壁纸设置] 使用备用方法（SystemParametersInfo + 注册表）");

                // 方法1: SystemParametersInfo（最可靠）
                try
                {
                    var result = SystemParametersInfo(
                        SPI_SETDESKWALLPAPER,
                        0,
                        imagePath,
                        SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);

                    if (result != 0)
                    {
                        spiSuccess = true;
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
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[壁纸设置] ❌ 备用方法失败: {ex.Message}");
            }

            // SPIF_SENDCHANGE 本身已经让系统通知各窗口了，正常情况不必再广播一遍。
            // 只有 SPI 明确失败时才补一次兜底。
            if (!spiSuccess)
            {
                RefreshExplorer();
            }

            return spiSuccess;
        }

        /// <summary>
        /// 下载图片到本地（带缓存检查）
        /// </summary>
        private async Task<string> DownloadImageAsync(string imageUrl, string title)
        {
            var tempPath = string.Empty;

            try
            {
                // 用稳定哈希做文件名：原来用 string.GetHashCode()，.NET 每个进程的
                // 哈希种子都不同，重启后算出来的名字和上次不一样，缓存永远命中不了，
                // 结果每设一张壁纸就重下一张 4MB 图，磁盘还越堆越大。
                var fileName = App1.Services.Imaging.ImageCachePeek.BuildFileName("wallpaper", imageUrl);
                var localPath = Path.GetFullPath(Path.Combine(_wallpaperFolder, fileName));  // 确保是绝对路径

                // 检查是否已经缓存
                if (File.Exists(localPath))
                {
                    System.Diagnostics.Debug.WriteLine($"[壁纸设置] 使用缓存图片: {localPath}");
                    return localPath;
                }

                System.Diagnostics.Debug.WriteLine($"[壁纸设置] 开始下载图片...");
                System.Diagnostics.Debug.WriteLine($"[壁纸设置] 图片URL: {imageUrl}");

                // 直接流到磁盘，不在内存里攒整张图。
                // 原来用 GetByteArrayAsync：4K 原图动辄十几 MB，MemoryStream 按倍增法
                // 先要一块更大的缓冲，再 ToArray() 复制一遍，一次换壁纸托管堆能冲到 36MB
                // （日志：[内存] 已回收(换壁纸后) 托管堆 36MB → 9MB），任务管理器跟着顶到 66MB。
                // 改成 64KB 缓冲边下边写，托管堆峰值基本只剩这点缓冲。
                tempPath = localPath + ".part";

                // 整条下载链的总超时，必须自己管。
                //
                // HttpClient.Timeout 在 ResponseHeadersRead 模式下只覆盖到「拿到响应头」，
                // 之后读响应体是一段裸流，读多久它完全不管。11-07 那次就是 Pexels 的 CDN
                // 发了 1.7MB 之后停住不吐数据、连接又没断，CopyToAsync 永远等下去，
                // 上层 SemaphoreSlim 的 finally 永远执行不到，自动换壁纸停了两个多小时。
                // 这里给 90 秒：4K 图在慢速网络下也够，超时就当这张失败。
                using var downloadCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

                using (var response = await _httpClient.GetAsync(
                           imageUrl, HttpCompletionOption.ResponseHeadersRead, downloadCts.Token))
                {
                    response.EnsureSuccessStatusCode();

                    using var source = await response.Content.ReadAsStreamAsync(downloadCts.Token);
                    using var target = new FileStream(
                        tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                        bufferSize: 64 * 1024, useAsync: true);

                    await source.CopyToAsync(target, 64 * 1024, downloadCts.Token);
                }

                // 下载完整了才改名，避免半截文件被当成缓存命中
                File.Move(tempPath, localPath, overwrite: true);
                tempPath = string.Empty;

                System.Diagnostics.Debug.WriteLine($"[壁纸设置] 图片下载完成: {new FileInfo(localPath).Length / 1024} KB");
                System.Diagnostics.Debug.WriteLine($"[壁纸设置] 保存路径: {localPath}");

                return localPath;
            }
            catch (OperationCanceledException)
            {
                // 总超时（或 HttpClient.Timeout）触发。必须写 AppLog：
                // 后台模式没有调试器，Debug.WriteLine 不会进文件，出问题时等于什么都看不到。
                AppLog.Write($"[壁纸设置] 下载超时，放弃这张: {imageUrl}");
                return string.Empty;
            }
            catch (Exception ex)
            {
                AppLog.Write($"[壁纸设置] 下载失败: {ex.Message} | {imageUrl}");
                System.Diagnostics.Debug.WriteLine($"[壁纸设置] 下载图片失败: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[壁纸设置] 异常堆栈: {ex.StackTrace}");
                return string.Empty;
            }
            finally
            {
                // 半截文件一定删掉。正常完成时 tempPath 已经清空（改名成正式文件了）。
                if (!string.IsNullOrEmpty(tempPath))
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                }
            }
        }

        /// <summary>
        /// 清理旧的壁纸文件（保留最近N个）
        /// </summary>
        /// <summary>
        /// 清理旧的壁纸文件（保留最近N个）
        /// </summary>
        /// <param name="keepCount">保留数量</param>
        /// <param name="excludePath">不参与清理的文件（正在使用的壁纸）</param>
        public void CleanupOldWallpapers(int keepCount = 30, string? excludePath = null)
        {
            try
            {
                string? excludeFull = null;
                if (!string.IsNullOrEmpty(excludePath))
                {
                    try { excludeFull = Path.GetFullPath(excludePath); } catch { }
                }

                var jpgFiles = Directory.GetFiles(_wallpaperFolder, "*.jpg");
                var bmpFiles = Directory.GetFiles(_wallpaperFolder, "*.bmp");

                var allFiles = jpgFiles.Concat(bmpFiles)
                    .Where(f => excludeFull == null
                                || !string.Equals(Path.GetFullPath(f), excludeFull, StringComparison.OrdinalIgnoreCase))
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.CreationTime)
                    .ToArray();

                // 保留最近N个文件，删除其他
                if (allFiles.Length > keepCount)
                {
                    for (var i = keepCount; i < allFiles.Length; i++)
                    {
                        try
                        {
                            allFiles[i].Delete();
                        }
                        catch (Exception ex)
                        {
                            // 文件可能正被系统占用，跳过就好，不必让整个清理失败
                            System.Diagnostics.Debug.WriteLine($"[壁纸设置] 跳过占用中的文件: {allFiles[i].Name} | {ex.Message}");
                        }
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
        /// 清理指定时间点之前（含该时间点）的壁纸文件
        /// </summary>
        public void CleanupWallpapersOlderThan(DateTime cutoffTime)
        {
            try
            {
                var jpgFiles = Directory.GetFiles(_wallpaperFolder, "*.jpg");
                var bmpFiles = Directory.GetFiles(_wallpaperFolder, "*.bmp");

                var allFiles = jpgFiles.Concat(bmpFiles).ToArray();
                var deletedCount = 0;

                foreach (var file in allFiles)
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.CreationTime <= cutoffTime)
                        {
                            info.Delete();
                            deletedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[壁纸设置] 跳过删除失败文件: {file} | {ex.Message}");
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[壁纸设置] 按日期清理完成，删除 {deletedCount} 个文件（阈值: {cutoffTime:yyyy-MM-dd HH:mm:ss}）");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[壁纸设置] 按日期清理失败: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 设置锁屏界面壁纸
        /// </summary>
        public async Task<bool> SetLockScreenAsync(string imageUrl, string title = "LockScreen")
        {
            // 下载本身有 90 秒总超时；这里再给整条锁屏链一个上限。
            // 防的是 WinRT 的 GetFileFromPathAsync / SetImageFileAsync 在后台无窗口
            // 进程里挂住 —— 这两个 API 没有任何超时参数，挂起来就是永远。
            try
            {
                return await SetLockScreenCoreAsync(imageUrl, title)
                    .WaitAsync(TimeSpan.FromSeconds(120));
            }
            catch (TimeoutException)
            {
                AppLog.Write("[锁屏设置] 超过 120 秒未返回，放弃本次（闸门照常释放）");
                return false;
            }
            catch (Exception ex)
            {
                AppLog.Write($"[锁屏设置] 失败: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> SetLockScreenCoreAsync(string imageUrl, string title)
        {
            System.Diagnostics.Debug.WriteLine($"[锁屏设置] 开始设置锁屏界面...");

            // 1. 下载图片到本地
            var localPath = await DownloadImageAsync(imageUrl, title);
            if (string.IsNullOrEmpty(localPath))
            {
                AppLog.Write("[锁屏设置] 图片下载失败，放弃本次");
                return false;
            }

            // 2. 验证文件存在
            if (!File.Exists(localPath))
            {
                AppLog.Write($"[锁屏设置] 图片文件不存在: {localPath}");
                return false;
            }

            // 3. 确保使用绝对路径
            var absolutePath = Path.GetFullPath(localPath);

            // 4. 尝试设置锁屏界面（兼容 Windows 11 26200+）
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(absolutePath);
                await Windows.System.UserProfile.LockScreen.SetImageFileAsync(file);

                System.Diagnostics.Debug.WriteLine("[锁屏设置] ✅ 锁屏界面设置成功");
                return true;
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                AppLog.Write($"[锁屏设置] COM 异常 0x{ex.HResult:X8}: {ex.Message}，退回注册表方式");

                // 尝试备用方法：使用注册表设置
                return SetLockScreenUsingRegistry(absolutePath);
            }
        }
        
        /// <summary>
        /// 使用注册表设置锁屏壁纸（备用方法）
        /// </summary>
        private bool SetLockScreenUsingRegistry(string imagePath)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[锁屏设置] 尝试使用注册表设置...");
                
                // 设置锁屏壁纸注册表项
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true))
                {
                    if (key != null)
                    {
                        key.SetValue("LockScreenImage", imagePath);
                        key.Flush();
                        System.Diagnostics.Debug.WriteLine($"[锁屏设置] ✅ 注册表更新成功");
                    }
                }
                
                // 刷新系统设置。这个调用要等系统里所有顶层窗口应答，
                // 丢到后台去发，别让界面陪着一起等
                _ = Task.Run(RefreshExplorer);
                
                System.Diagnostics.Debug.WriteLine($"[锁屏设置] ✅ 备用方法设置成功");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[锁屏设置] ❌ 备用方法失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 在独立 STA 线程上执行一段同步代码，并设硬性等待上限。
        /// 系统调用有可能永远不返回（explorer 卡住、显示驱动挂起），
        /// 没有上限的话 await 会一直悬着，"正在设置壁纸"的提示也就永远收不掉。
        /// </summary>
        /// <summary>
        /// 系统调用是否还有一次在飞。0=空闲 1=有线程正在执行。
        /// 超时返回时**不**复位 —— 只有线程真正结束才复位。
        /// 这样系统调用一旦卡死，后续换壁纸会立刻跳过，而不是每分钟再堆一个线程上去；
        /// 等那个线程恢复并退出，功能自动恢复。
        /// </summary>
        private int _applyThreadInFlight;

        private async Task<T?> RunOnStaThreadAsync<T>(Func<T> func, TimeSpan timeout)
        {
            if (Interlocked.CompareExchange(ref _applyThreadInFlight, 1, 0) != 0)
            {
                AppLog.Write("[壁纸设置] 上一次系统调用仍未返回，本次跳过（不再叠加新线程）");
                return default;
            }

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            var thread = new Thread(() =>
            {
                try
                {
                    tcs.TrySetResult(func());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    // 线程真正结束才允许下一次调用
                    Volatile.Write(ref _applyThreadInFlight, 0);
                }
            })
            {
                IsBackground = true,
                Name = "WallpaperApply"
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            var finished = await Task.WhenAny(tcs.Task, Task.Delay(timeout)).ConfigureAwait(false);
            if (finished != tcs.Task)
            {
                AppLog.Write(
                    $"[壁纸设置] ⚠️ 系统调用超过 {timeout.TotalSeconds:0} 秒未返回，放弃等待（线程仍在后台跑，后续换壁纸会跳过直到它退出）");
                return default;
            }

            return await tcs.Task.ConfigureAwait(false);
        }

        /// <summary>
        /// 真正把图片设为壁纸。COM 与 Win32 都是同步阻塞调用，
        /// 只能由后台线程执行，绝不能碰 UI 线程。
        /// </summary>
        private bool ApplyWallpaperCore(string normalizedPath)
        {
            // 先走 SystemParametersInfo：一次 API 调用就能落地，不创建任何 COM 对象。
            //
            // 顺序原来是反的 —— 先 COM 再 SPI，等于每次换壁纸都做两遍。
            // 更要命的是 IDesktopWallpaper 属于 explorer 进程里的 LocalServer，
            // 在没有消息泵的 STA 线程上同步调用它，一旦 explorer 正好在处理我们的
            // WM_SETTINGCHANGE 广播，两边就会互相等着对方释放 —— 桌面和我们一起挂住。
            if (SetWallpaperFallback(normalizedPath))
            {
                System.Diagnostics.Debug.WriteLine("[壁纸设置] ✅ 已通过 SystemParametersInfo 设置");
                return true;
            }

            System.Diagnostics.Debug.WriteLine("[壁纸设置] SPI 未成功，退回 IDesktopWallpaper COM...");

            try
            {
                var wallpaper = (IDesktopWallpaper)new DesktopWallpaperClass();

                try
                {
                    // 先设置壁纸样式为填充（Fill）
                    wallpaper.SetPosition(DesktopWallpaperPosition.Fill);

                    // 设置壁纸（null 表示所有显示器）
                    wallpaper.SetWallpaper(null!, normalizedPath);
                    System.Diagnostics.Debug.WriteLine("[壁纸设置] ✅ COM 接口调用成功");
                    return true;
                }
                finally
                {
                    // 原来只在成功路径上释放，异常时每失败一次就漏一个 COM 引用
                    if (Marshal.IsComObject(wallpaper))
                    {
                        Marshal.ReleaseComObject(wallpaper);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[壁纸设置] ❌ COM 接口失败: {ex.GetType().Name} / {ex.Message}");
                try
                {
                    System.Diagnostics.Debug.WriteLine($"[壁纸设置] HRESULT: 0x{Marshal.GetHRForException(ex):X8}");
                }
                catch
                {
                    // 部分异常拿不到 HRESULT，忽略
                }
            }

            return false;
        }

    }
}
