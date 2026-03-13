using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using App1.Models;
using App1.Services;
using App1.Services.Api;
using App1.Services.Cache;
using App1.Services.Data;
using App1.Services.WallpaperSetter;
using App1.Config;
using App1.Helpers;

namespace App1
{
    /// <summary>
    /// 主窗口
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private readonly QihuApiService _qihuApi;
        private readonly BingApiService _bingApi;
        private readonly PexelsApiService _pexelsApi;
        private readonly UnsplashApiService _unsplashApi;
        private DatabaseService? _databaseService;
        private WallpaperRepository? _repository;
        private readonly CacheService _cacheService;
        private readonly WallpaperService _wallpaperService;
        private TrayManager? _trayManager; // 托盘管理器
        
        private readonly ObservableCollection<Wallpaper> _wallpapers = new();
        private bool _isLoadingMore = false;
        private int _currentPage = 0;
        private const int PAGE_SIZE = 18;  // 每次加载18张（5张Pexels + 10张Bing + 3张360）
        private bool _isImmersiveMode = true;  // 默认沉浸式浏览模式
        
        // 沉浸式模式独立数据源
        private readonly ObservableCollection<Wallpaper> _immersiveWallpapers = new();
        private bool _isLoadingImmersive = false;
        private int _immersiveLoadCount = 0;  // 已加载批次计数
        private int _savedImmersiveIndex = 0;  // 保存的沉浸式模式位置
        
        // 右键菜单相关
        private Wallpaper? _currentContextWallpaper = null;  // 当前右键点击的壁纸
        
        // 定时切换功能
        private DispatcherTimer? _desktopWallpaperTimer = null;  // 桌面壁纸定时器
        private DispatcherTimer? _lockScreenWallpaperTimer = null;  // 锁屏壁纸定时器
        private bool _isDesktopTimerEnabled = false;  // 桌面定时器是否启用
        private bool _isLockScreenTimerEnabled = false;  // 锁屏定时器是否启用
        
        // 设置管理
        private AppSettings _appSettings = new AppSettings();
        
        // 允许真正关闭的标志（用于从托盘退出时绕过最小化行为）
        private bool _allowClose = false;
        
        // 网络状态
        private int _consecutiveNetworkErrors = 0;  // 连续网络错误次数
        private const int MAX_NETWORK_ERRORS = 3;  // 最大连续错误次数，超过后提示用户
        
        // 滚轮事件节流
        private DateTime _lastWheelTime = DateTime.MinValue;
        private const int WHEEL_THROTTLE_MS = 200;  // 滚轮事件节流：200毫秒
        
        // 单击/双击判断
        private bool _isWaitingForDoubleClick = false;
        private Wallpaper? _pendingClickWallpaper = null;
        
        // Windows 版本检测（Win11 可以直接加载 HTTPS，Win10 需要下载）
        private readonly bool _isWindows11;
        private readonly bool _startHiddenOnLaunch;

        public MainWindow(bool startHiddenOnLaunch = false)
        {
            this.InitializeComponent();
            _startHiddenOnLaunch = startHiddenOnLaunch;
            
            // 检测 Windows 版本
            _isWindows11 = IsWindows11OrGreater();
            System.Diagnostics.Debug.WriteLine($"[系统检测] Windows 版本: {(_isWindows11 ? "Win11+" : "Win10")}");
            System.Diagnostics.Debug.WriteLine($"[系统检测] 图片加载模式: {(_isWindows11 ? "直接HTTPS加载" : "下载后加载")}");
            
            // 配置网络支持（提高 Win10 兼容性）
            ConfigureNetworkSupport();
            
            // 根据屏幕分辨率自适应设置窗口大小（保持16:9比例）
            SetAdaptiveWindowSize();
            
            // 设置窗口图标（Unpackaged 模式）
            try
            {
                // 在 Unpackaged 模式下使用 AppContext.BaseDirectory
                var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "app.ico");
                
                if (System.IO.File.Exists(iconPath))
                {
                    AppWindow.SetIcon(iconPath);
                    System.Diagnostics.Debug.WriteLine($"[窗口图标] ✅ 设置成功: {iconPath}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[窗口图标] ⚠️ 文件不存在: {iconPath}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[窗口图标] ⚠️ 设置失败（可忽略）: {ex.Message}");
            }
            
            // 隐藏标题栏，使用无边框模式
            var titleBar = AppWindow.TitleBar;
            titleBar.ExtendsContentIntoTitleBar = true;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            
            // 快速初始化轻量级服务
            _qihuApi = new QihuApiService();
            _bingApi = new BingApiService();
            _pexelsApi = new PexelsApiService();
            _unsplashApi = new UnsplashApiService();
            _cacheService = new CacheService();
            _wallpaperService = new WallpaperService();
            
            // 异步初始化数据库（避免阻塞UI）
            _ = InitializeDatabaseAsync();
            
            // 绑定数据源
            WallpaperGridView.ItemsSource = _wallpapers;  // 网格模式
            ImmersiveFlipView.ItemsSource = _immersiveWallpapers;  // 沉浸式模式独立数据源
            
            // 启动每日自动清理检查
            CheckAndCleanCacheDaily();
            
            // 默认启动沉浸式模式（不加载多图模式数据）
            SetImmersiveMode(true);
            
            // 1.5秒后隐藏启动画面（加快启动速度）
            var splashTimer = new DispatcherTimer();
            splashTimer.Interval = TimeSpan.FromMilliseconds(1500);
            splashTimer.Tick += (s, e) =>
            {
                splashTimer.Stop();
                HideSplashScreen();
            };
            splashTimer.Start();
            
            // 监听窗口关闭事件
            this.Closed += MainWindow_Closed;
            
            // 加载应用设置
            LoadAppSettings();
            
            // 初始化系统托盘
            _trayManager = new TrayManager(this, () =>
            {
                // 在UI线程执行安全关闭
                this.DispatcherQueue.TryEnqueue(() => Shutdown());
            });

            // 开机自启后台启动：初始化后直接隐藏窗口，仅保留托盘
            if (_startHiddenOnLaunch)
            {
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        this.AppWindow.Hide();
                        System.Diagnostics.Debug.WriteLine("[启动] 后台模式启动，主窗口已隐藏到托盘");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[启动] 后台隐藏失败: {ex.Message}");
                    }
                });
            }
            
            // 检查开机启动状态（后台检查，不显示提示）
            _ = CheckStartupTaskAsync();
        }
        
        /// <summary>
        /// 检测是否为 Windows 11 或更高版本
        /// </summary>
        private static bool IsWindows11OrGreater()
        {
            try
            {
                var version = Environment.OSVersion.Version;
                // Windows 11 的版本号是 10.0.22000 或更高
                // Windows 10 的版本号是 10.0.xxxxx (xxxxx < 22000)
                if (version.Major >= 10 && version.Build >= 22000)
                {
                    return true;
                }
                return false;
            }
            catch
            {
                // 如果检测失败，默认使用 Win10 模式（更安全）
                return false;
            }
        }
        
        /// <summary>
        /// 配置网络支持（提高 Win10 兼容性）
        /// </summary>
        private void ConfigureNetworkSupport()
        {
            try
            {
                // 启用 TLS 1.2 和 1.3（Win10 需要）
                System.Net.ServicePointManager.SecurityProtocol = 
                    System.Net.SecurityProtocolType.Tls12 | 
                    System.Net.SecurityProtocolType.Tls13;
                
                // 设置默认连接数限制
                System.Net.ServicePointManager.DefaultConnectionLimit = 10;
                
                // 启用 Expect100Continue 优化
                System.Net.ServicePointManager.Expect100Continue = true;
                
                System.Diagnostics.Debug.WriteLine("[网络配置] ✅ TLS 1.2/1.3 已启用");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[网络配置] ⚠️ 配置失败: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 异步初始化数据库（避免阻塞UI线程）
        /// </summary>
        private async Task InitializeDatabaseAsync()
        {
            try
            {
                _databaseService = new DatabaseService();
                await _databaseService.InitializeAsync();
                _repository = new WallpaperRepository(_databaseService);
                System.Diagnostics.Debug.WriteLine("[数据库] ✅ 异步初始化完成");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[数据库] ❌ 初始化失败: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 根据屏幕分辨率自适应设置窗口大小
        /// </summary>
        private void SetAdaptiveWindowSize()
        {
            try
            {
                // 获取当前显示器的显示区域
                var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
                var workArea = displayArea.WorkArea;
                
                // 输出显示器信息（帮助诊断）
                System.Diagnostics.Debug.WriteLine($"[显示器信息] 工作区域: {workArea.Width}x{workArea.Height}");
                
                // 计算窗口大小（屏幕工作区域的65%）
                var targetWidth = (int)(workArea.Width * 0.65);
                var targetHeight = (int)(workArea.Height * 0.65);
                
                // 保持16:9比例，以宽度为准计算高度
                var aspectRatio = 16.0 / 9.0;
                var calculatedHeight = (int)(targetWidth / aspectRatio);
                
                // 如果计算的高度超过目标高度，则以高度为准重新计算宽度
                if (calculatedHeight > targetHeight)
                {
                    calculatedHeight = targetHeight;
                    targetWidth = (int)(calculatedHeight * aspectRatio);
                }
                else
                {
                    targetHeight = calculatedHeight;
                }
                
                // 设置最小尺寸限制（不小于 1280x720）
                if (targetWidth < 1280)
                {
                    targetWidth = 1280;
                    targetHeight = 720;
                }
                
                // 支持8K及以上分辨率 - 不设最大限制
                // 窗口大小会根据屏幕自适应，8K屏幕可以显示更大的窗口
                
                // 应用窗口大小
                AppWindow.Resize(new Windows.Graphics.SizeInt32 { Width = targetWidth, Height = targetHeight });
                
                System.Diagnostics.Debug.WriteLine($"[窗口大小] 屏幕: {workArea.Width}x{workArea.Height}, 窗口: {targetWidth}x{targetHeight}");
            }
            catch (Exception ex)
            {
                // 如果自适应失败，使用默认大小 1600x900
                System.Diagnostics.Debug.WriteLine($"[窗口大小] 自适应失败，使用默认大小: {ex.Message}");
                AppWindow.Resize(new Windows.Graphics.SizeInt32 { Width = 1600, Height = 900 });
            }
        }
        
        /// <summary>
        /// 窗口关闭时的处理：默认最小化到托盘，除非允许真正关闭
        /// </summary>
        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[主窗口] 关闭事件触发");

                if (!_allowClose)
                {
                    // 取消关闭并最小化到托盘
                    args.Handled = true;
                    this.AppWindow.Hide();

                    string message = _isDesktopTimerEnabled || _isLockScreenTimerEnabled
                        ? "已最小化到后台，定时器继续运行"
                        : "已最小化到后台";
                    _trayManager?.ShowNotification("小K壁纸", message);

                    System.Diagnostics.Debug.WriteLine("[主窗口] 已最小化到托盘");
                    return;
                }

                System.Diagnostics.Debug.WriteLine("[主窗口] 允许关闭，开始释放资源...");

                // 停止所有定时器并释放资源（防止进程残留）
                StopAllTimers();
                _trayManager?.Dispose();
                _pexelsApi?.Dispose();
                _databaseService?.Dispose();

                System.Diagnostics.Debug.WriteLine("[主窗口] 资源已释放，窗口将关闭");
             }
             catch (Exception ex)
             {
                 System.Diagnostics.Debug.WriteLine($"[主窗口] 最小化到托盘失败: {ex.Message}");
             }
         }

        /// <summary>
        /// 统一安全关闭流程：停止后台任务、释放资源并退出进程
        /// </summary>
        private void Shutdown()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[退出] 开始执行安全关闭流程...");

                // 停止并清理所有定时器
                StopAllTimers();

                // 释放资源
                try { _pexelsApi?.Dispose(); } catch { }
                try { _qihuApi?.Dispose(); } catch { }
                try { _unsplashApi?.Dispose(); } catch { }
                try { _databaseService?.Dispose(); } catch { }

                // 移除托盘图标并释放托盘管理器
                try { _trayManager?.Dispose(); _trayManager = null; } catch { }

                // 允许窗口真正关闭并触发关闭流程
                _allowClose = true;

                // 最后强制退出进程以确保无残留
                System.Diagnostics.Debug.WriteLine("[退出] 触发 Environment.Exit(0)");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[退出] 安全关闭失败: {ex.Message}");
                try { Environment.Exit(0); } catch { }
            }
        }

        /// <summary>
        /// 完全退出应用程序
        /// </summary>
        public void ExitApplication()
        {
            // 调用统一的安全关闭流程
            Shutdown();
        }
        
        /// <summary>
        /// 停止所有定时器
        /// </summary>
        private void StopAllTimers()
        {
            if (_desktopWallpaperTimer != null)
            {
                _desktopWallpaperTimer.Stop();
                _desktopWallpaperTimer.Tick -= OnDesktopTimerTick;
                _desktopWallpaperTimer = null;
                _isDesktopTimerEnabled = false;
                System.Diagnostics.Debug.WriteLine("[定时器] 桌面定时器已停止");
            }
            
            if (_lockScreenWallpaperTimer != null)
            {
                _lockScreenWallpaperTimer.Stop();
                _lockScreenWallpaperTimer.Tick -= OnLockScreenTimerTick;
                _lockScreenWallpaperTimer = null;
                _isLockScreenTimerEnabled = false;
                System.Diagnostics.Debug.WriteLine("[定时器] 锁屏定时器已停止");
            }
        }
        
        /// <summary>
        /// 检查并执行每日自动清理（每天自动清理一次）
        /// </summary>
        private void CheckAndCleanCacheDaily()
        {
            try
            {
                // Unpackaged 模式：使用文件存储设置
                var settingsFolder = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "小K壁纸");
                var settingsFile = System.IO.Path.Combine(settingsFolder, "settings.txt");
                
                DateTime? lastCleanupDate = null;
                
                // 读取上次清理日期
                if (System.IO.File.Exists(settingsFile))
                {
                    try
                    {
                        var content = System.IO.File.ReadAllText(settingsFile);
                        if (!string.IsNullOrEmpty(content))
                        {
                            lastCleanupDate = DateTime.Parse(content);
                        }
                    }
                    catch { }
                }
                
                var today = DateTime.Now.Date;
                
                // 如果从未清理过（首次启动），只记录时间不清理
                if (!lastCleanupDate.HasValue)
                {
                    System.IO.File.WriteAllText(settingsFile, today.ToString("yyyy-MM-dd"));
                    System.Diagnostics.Debug.WriteLine($"[每日清理] 首次启动，记录时间: {today:yyyy-MM-dd}（不清理文件）");
                }
                // 如果距离上次清理已经超过1天，执行清理
                else if ((today - lastCleanupDate.Value.Date).TotalDays >= 1)
                {
                    System.Diagnostics.Debug.WriteLine("[每日清理] 开始执行自动清理（删除旧壁纸文件）...");
                    _wallpaperService.CleanupAllWallpapers();
                    
                    // 记录清理时间
                    System.IO.File.WriteAllText(settingsFile, today.ToString("yyyy-MM-dd"));
                    
                    System.Diagnostics.Debug.WriteLine($"[每日清理] 清理完成，记录时间: {today:yyyy-MM-dd}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[每日清理] 今天已清理过，跳过（上次: {lastCleanupDate.Value:yyyy-MM-dd}）");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[每日清理] 失败: {ex.Message}");
            }
        }
        
        private void SetDragRegion()
        {
            // 设置标题栏区域可拖动
            AppWindow.TitleBar.SetDragRectangles(new Windows.Graphics.RectInt32[] 
            { 
                new Windows.Graphics.RectInt32(0, 0, 100000, 48) 
            });
        }
        
        /// <summary>
        /// 检查网络连接
        /// </summary>
        private bool IsNetworkAvailable()
        {
            return System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
        }
        
        /// <summary>
        /// 加载应用设置
        /// </summary>
        private void LoadAppSettings()
        {
            try
            {
                _appSettings = SettingsManager.LoadSettings();
                
                // 恢复定时器状态
                if (_appSettings.IsDesktopTimerEnabled && _appSettings.DesktopTimerInterval > 0)
                {
                    StartWallpaperTimer(isDesktop: true, intervalMinutes: _appSettings.DesktopTimerInterval);
                }
                
                if (_appSettings.IsLockScreenTimerEnabled && _appSettings.LockScreenTimerInterval > 0)
                {
                    StartWallpaperTimer(isDesktop: false, intervalMinutes: _appSettings.LockScreenTimerInterval);
                }
                
                // 恢复开机自启状态
                if (_appSettings.IsStartupEnabled)
                {
                    EnableStartup();
                }
                else if (IsStartupEnabled())
                {
                    DisableStartup();
                }
                
                System.Diagnostics.Debug.WriteLine("[设置] 应用设置已恢复");
                
                // 更新定时状态指示器
                UpdateTimerStatusIndicator();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[设置] 加载设置失败: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 保存应用设置
        /// </summary>
        private void SaveAppSettings()
        {
            try
            {
                _appSettings.IsDesktopTimerEnabled = _isDesktopTimerEnabled;
                _appSettings.IsLockScreenTimerEnabled = _isLockScreenTimerEnabled;
                
                SettingsManager.SaveSettings(_appSettings);
                System.Diagnostics.Debug.WriteLine("[设置] 配置已保存");
                
                // 更新定时状态指示器
                UpdateTimerStatusIndicator();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[设置] 保存设置失败: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 更新定时状态指示器
        /// </summary>
        private void UpdateTimerStatusIndicator()
        {
            try
            {
                if (_isDesktopTimerEnabled || _isLockScreenTimerEnabled)
                {
                    TimerStatusIndicator.Visibility = Visibility.Visible;
                    
                    string statusText = "定时切换: ";
                    if (_isDesktopTimerEnabled && _isLockScreenTimerEnabled)
                    {
                        statusText += "桌面和锁屏已启用";
                    }
                    else if (_isDesktopTimerEnabled)
                    {
                        statusText += "桌面已启用";
                    }
                    else
                    {
                        statusText += "锁屏已启用";
                    }
                    
                    TimerStatusText.Text = statusText;
                }
                else
                {
                    TimerStatusIndicator.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UI] 更新定时状态指示器失败: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 无限滚动：加载更多壁纸（边获取边显示）
        /// </summary>
        private async System.Threading.Tasks.Task LoadMoreWallpapersAsync()
        {
            if (_isLoadingMore)
            {
                System.Diagnostics.Debug.WriteLine("[加载更多] 已在加载中，跳过");
                return;
            }
            
            // 检查是否仍在多图模式
            if (_isImmersiveMode)
            {
                System.Diagnostics.Debug.WriteLine("[加载更多] 已切换到沉浸式模式，停止加载");
                return;
            }
            
            // 检查网络连接
            if (!IsNetworkAvailable())
            {
                System.Diagnostics.Debug.WriteLine("[加载更多] ❌ 无网络连接");
                ShowToast("无网络连接，请检查网络设置", isSuccess: false, durationMs: 3000);
                ShowEmpty(true);
                return;
            }
            
            try
            {
                _isLoadingMore = true;
                ShowLoadingMore(true);
                
                System.Diagnostics.Debug.WriteLine($"[加载更多] ========== 开始加载第 {_currentPage + 1} 页 ==========");
                
                int totalLoaded = 0;
                var newWallpapersForDb = new List<Wallpaper>();
                
                System.Diagnostics.Debug.WriteLine("[流式加载] 开始边获取边显示（Pexels + Bing + 360）...");
                
                // 【优先】获取Pexels高质量壁纸（请求15张，过滤后取5张）
                try
                {
                    System.Diagnostics.Debug.WriteLine("[Pexels壁纸] 请求 15 张（过滤后取5张横向）...");
                    var pexelsWallpapers = await _pexelsApi.GetCuratedWallpapersAsync(perPage: 15);
                    
                    if (pexelsWallpapers.Count > 0)
                    {
                        // 只取前5张（已过滤为横向图片）
                        var selectedWallpapers = pexelsWallpapers.Take(5).ToList();
                        
                        foreach (var wallpaper in selectedWallpapers)
                        {
                            // 立即显示
                            _wallpapers.Add(wallpaper);
                            newWallpapersForDb.Add(wallpaper);
                            totalLoaded++;
                            
                            ShowEmpty(false);
                        }
                        
                        System.Diagnostics.Debug.WriteLine($"[Pexels壁纸] ✓ {selectedWallpapers.Count}/{pexelsWallpapers.Count} 张已显示（已过滤横向）");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Pexels壁纸] 获取失败: {ex.Message}");
                }
                
                // 【其次】循环5次，每次获取2张Bing壁纸（边获取边显示）
                for (int i = 0; i < 5; i++)
                {
                    try
                    {
                        System.Diagnostics.Debug.WriteLine($"[Bing壁纸] 第 {i + 1}/5 批，获取 2 张...");
                        var bingWallpapers = await _bingApi.GetDailyWallpapersAsync(2);
                        
                        if (bingWallpapers.Count > 0)
                        {
                            foreach (var wallpaper in bingWallpapers)
                            {
                                // 立即显示
                                _wallpapers.Add(wallpaper);
                                newWallpapersForDb.Add(wallpaper);
                                totalLoaded++;
                                
                                ShowEmpty(false);
                            }
                            
                            System.Diagnostics.Debug.WriteLine($"[Bing壁纸] ✓ 第 {i + 1} 批 {bingWallpapers.Count} 张已显示");
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Bing壁纸] 第 {i + 1} 批获取失败: {ex.Message}");
                    }
                }
                
                // 【最后】一次性获取3张360壁纸
                try
                {
                    System.Diagnostics.Debug.WriteLine("[360壁纸] 一次性获取 3 张...");
                    var qihu360Wallpapers = await _qihuApi.GetWallpapersAsync(countPerCategory: 3);
                    
                    if (qihu360Wallpapers.Count > 0)
                    {
                        foreach (var wallpaper in qihu360Wallpapers)
                        {
                            // 立即显示
                            _wallpapers.Add(wallpaper);
                            newWallpapersForDb.Add(wallpaper);
                            totalLoaded++;
                            
                            ShowEmpty(false);
                        }
                        
                        System.Diagnostics.Debug.WriteLine($"[360壁纸] ✓ {qihu360Wallpapers.Count} 张已显示");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[360壁纸] 获取失败: {ex.Message}");
                }
                
                // 批量保存到数据库（后台操作，不阻塞UI）
                if (newWallpapersForDb.Count > 0 && _repository != null)
                {
                    _ = Task.Run(async () => await _repository.SaveWallpapersAsync(newWallpapersForDb));
                }
                
                if (totalLoaded > 0)
                {
                    _currentPage++;
                    _consecutiveNetworkErrors = 0;  // 重置错误计数
                    System.Diagnostics.Debug.WriteLine($"[加载更多] ✅ 共加载 {totalLoaded} 张壁纸");
                }
                else
                {
                    _consecutiveNetworkErrors++;
                    System.Diagnostics.Debug.WriteLine($"[加载更多] ⚠️ 连续加载失败 {_consecutiveNetworkErrors} 次");
                    
                    if (_wallpapers.Count == 0)
                    {
                        ShowEmpty(true);
                    }
                    
                    // 连续失败3次，提示用户检查网络
                    if (_consecutiveNetworkErrors >= MAX_NETWORK_ERRORS)
                    {
                        ShowToast("加载失败次数过多，请检查网络连接", isSuccess: false, durationMs: 4000);
                        _consecutiveNetworkErrors = 0;  // 重置计数
                    }
                }
            }
            catch (Exception ex)
            {
                _consecutiveNetworkErrors++;
                System.Diagnostics.Debug.WriteLine($"[加载更多] ❌ 异常: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[加载更多] 堆栈跟踪: {ex.StackTrace}");
                
                // 检查是否是网络异常
                if (ex is HttpRequestException || ex is TaskCanceledException)
                {
                    System.Diagnostics.Debug.WriteLine("[加载更多] 检测到网络异常");
                    
                    if (_consecutiveNetworkErrors >= MAX_NETWORK_ERRORS)
                    {
                        ShowToast("网络连接失败，请检查网络设置", isSuccess: false, durationMs: 4000);
                        _consecutiveNetworkErrors = 0;
                    }
                }
                
                if (_wallpapers.Count == 0)
                {
                    ShowEmpty(true);
                }
            }
            finally
            {
                System.Diagnostics.Debug.WriteLine($"[加载更多] ========== 结束（共加载 {_wallpapers.Count} 张）==========");
                _isLoadingMore = false;
                ShowLoadingMore(false);
            }
        }
        
        /// <summary>
        /// 滚动事件：检测是否滚动到底部
        /// </summary>
        private async void WallpaperScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            var scrollViewer = sender as ScrollViewer;
            if (scrollViewer == null) return;
            
            // 检测是否接近底部（距离底部200像素时触发）
            var verticalOffset = scrollViewer.VerticalOffset;
            var maxVerticalOffset = scrollViewer.ScrollableHeight;
            
            if (maxVerticalOffset - verticalOffset < 200 && !_isLoadingMore)
            {
                System.Diagnostics.Debug.WriteLine("[滚动检测] 接近底部，加载更多...");
                try
                {
                    await LoadMoreWallpapersAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[滚动加载] 异常: {ex.Message}");
                }
           }
        }
        
        /// <summary>
        /// 刷新按钮 - 加载更多壁纸
        /// </summary>
        private async void btnRefresh_Click(object sender, RoutedEventArgs e)
        {
            await LoadMoreWallpapersAsync();
        }
        
        /// <summary>
        /// 壁纸双击 - 直接设置为桌面壁纸
        /// </summary>
        private async void WallpaperItem_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            // 取消单击等待，防止触发全屏查看
            _isWaitingForDoubleClick = false;
            _pendingClickWallpaper = null;
            
            // 从DataContext获取Wallpaper对象
            if (sender is FrameworkElement element && element.DataContext is Wallpaper wallpaper)
            {
                System.Diagnostics.Debug.WriteLine($"[双击设置] {wallpaper.Title}");
                
                // 双击直接设置壁纸，无需确认
                await SetWallpaperAsync(wallpaper);
            }
        }
        
        /// <summary>
        /// 设置壁纸（优化版：使用缓存图片）
        /// </summary>
        private async System.Threading.Tasks.Task SetWallpaperAsync(Wallpaper wallpaper)
        {
            try
            {
                // 显示全屏进度提示
                SettingWallpaperPanel.Visibility = Visibility.Visible;
                
                // 优先使用FullUrl（UHD 4K），如果没有则使用ThumbUrl
                var imageUrl = !string.IsNullOrEmpty(wallpaper.FullUrl) ? wallpaper.FullUrl : wallpaper.ThumbUrl;
                
                // 关键改进：SetWallpaperAsync内部会自动检查缓存
                // 如果图片已缓存，会直接使用；如果没缓存，会下载并缓存
                var success = await _wallpaperService.SetWallpaperAsync(
                    imageUrl, 
                    wallpaper.Title,
                    wallpaper.LocalPath  // 传入可能已存在的本地路径
                );
                
                // 隐藏进度提示
                SettingWallpaperPanel.Visibility = Visibility.Collapsed;
                
                if (success)
                {
                    // 显示成功通知（左下角）
                    ShowToast("壁纸设置成功！", isSuccess: true);
                }
                else
                {
                    // 显示失败通知
                    ShowToast("壁纸设置失败", isSuccess: false);
                }
            }
            catch (Exception ex)
            {
                // 隐藏进度提示
                SettingWallpaperPanel.Visibility = Visibility.Collapsed;
                
                System.Diagnostics.Debug.WriteLine($"设置壁纸失败: {ex.Message}");
                
                // 显示错误通知
                ShowToast("壁纸设置失败", isSuccess: false);
            }
        }
        
        /// <summary>
        /// 隐藏启动画面（渐出动画）
        /// </summary>
        private void HideSplashScreen()
        {
            var fadeOut = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(300)),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.QuadraticEase()
            };
            
            var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            storyboard.Children.Add(fadeOut);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fadeOut, SplashScreen);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fadeOut, "Opacity");
            
            storyboard.Completed += (s, e) =>
            {
                SplashScreen.Visibility = Visibility.Collapsed;
                System.Diagnostics.Debug.WriteLine("[启动画面] 已隐藏");
            };
            
            storyboard.Begin();
        }
        
        /// <summary>
        /// 显示加载动画
        /// </summary>
        private void ShowLoading(bool show)
        {
            LoadingPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            WallpaperGridView.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        }
        
        /// <summary>
        /// 显示或隐藏沉浸式加载进度条
        /// </summary>
        private void ShowImmersiveLoading(bool show)
        {
            ImmersiveLoadingIndicator.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }
        
        /// <summary>
        /// 显示Toast通知（左下角）
        /// </summary>
        private async void ShowToast(string message, bool isSuccess = true, int durationMs = 2000)
        {
            // 设置图标和颜色
            if (isSuccess)
            {
                ToastIcon.Glyph = "\uE73E";  // CheckMark
                ToastIcon.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(255, 76, 175, 80));  // Green
            }
            else
            {
                ToastIcon.Glyph = "\uE783";  // Error
                ToastIcon.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(255, 244, 67, 54));  // Red
            }
            
            ToastMessage.Text = message;
            
            // 渐入动画
            ToastNotification.Opacity = 0;
            ToastNotification.Visibility = Visibility.Visible;
            
            var fadeInAnimation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.QuadraticEase()
            };
            
            var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            storyboard.Children.Add(fadeInAnimation);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fadeInAnimation, ToastNotification);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fadeInAnimation, "Opacity");
            storyboard.Begin();
            
            // 等待指定时间
            await System.Threading.Tasks.Task.Delay(durationMs);
            
            // 渐出动画
            var fadeOutAnimation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.QuadraticEase()
            };
            
            var fadeOutStoryboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            fadeOutStoryboard.Children.Add(fadeOutAnimation);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fadeOutAnimation, ToastNotification);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fadeOutAnimation, "Opacity");
            
            fadeOutStoryboard.Completed += (s, e) =>
            {
                ToastNotification.Visibility = Visibility.Collapsed;
            };
            
            fadeOutStoryboard.Begin();
        }
        
        /// <summary>
        /// 动态更新沉浸式加载进度条：只在浏览到最后一张且正在加载时显示
        /// </summary>
        private void UpdateImmersiveLoadingIndicator()
        {
            var currentIndex = ImmersiveFlipView.SelectedIndex;
            var isLastItem = currentIndex == _immersiveWallpapers.Count - 1;
            
            // 只在：1. 浏览到最后一张 且 2. 正在加载 时显示进度条
            var shouldShow = isLastItem && _isLoadingImmersive;
            ShowImmersiveLoading(shouldShow);
        }
        
        /// <summary>
        /// 显示或隐藏"加载更多"指示器
        /// </summary>
        private void ShowLoadingMore(bool show)
        {
            txtLoadingMore.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }
        
        /// <summary>
        /// 显示空状态
        /// </summary>
        private void ShowEmpty(bool show)
        {
            EmptyPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            WallpaperGridView.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        }
        
        
        /// <summary>
        /// 显示通知消息（3秒后自动关闭）
        /// </summary>
        private void ShowNotification(string message, Microsoft.UI.Xaml.Controls.InfoBarSeverity severity)
        {
            // 设置消息文本
            NotificationText.Text = message;
            
            // 隐藏所有图标
            NotificationProgress.Visibility = Visibility.Collapsed;
            NotificationSuccessIcon.Visibility = Visibility.Collapsed;
            NotificationErrorIcon.Visibility = Visibility.Collapsed;
            NotificationCheckBox.Visibility = Visibility.Collapsed;
            NotificationProgress.IsActive = false;
            
            // 根据类型显示对应图标
            switch (severity)
            {
                case Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success:
                    NotificationSuccessIcon.Visibility = Visibility.Visible;
                    NotificationCheckBox.Visibility = Visibility.Visible;
                    break;
                case Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error:
                    NotificationErrorIcon.Visibility = Visibility.Visible;
                    break;
                case Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational:
                    NotificationProgress.Visibility = Visibility.Visible;
                    NotificationProgress.IsActive = true;
                    break;
            }
            
            // 显示通知栏
            NotificationBar.Visibility = Visibility.Visible;
            
            // 3秒后自动关闭（进度类型不自动关闭）
            if (severity != Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational)
            {
                var timer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(3)
                };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    NotificationBar.Visibility = Visibility.Collapsed;
                };
                timer.Start();
            }
        }
        
        /// <summary>
        /// 通知栏关闭按钮点击
        /// </summary>
        private void NotificationCloseButton_Click(object sender, RoutedEventArgs e)
        {
            NotificationBar.Visibility = Visibility.Collapsed;
        }
        
        /// <summary>
        /// 切换浏览模式（网格模式 ↔ 沉浸式模式）
        /// </summary>
        private void btnToggleViewMode_Click(object sender, RoutedEventArgs e)
        {
            _isImmersiveMode = !_isImmersiveMode;
            SetImmersiveMode(_isImmersiveMode);
        }
        
        /// <summary>
        /// 设置浏览模式
        /// </summary>
        private void SetImmersiveMode(bool isImmersive)
        {
            _isImmersiveMode = isImmersive;
            
            if (_isImmersiveMode)
            {
                // 切换到沉浸式模式
                System.Diagnostics.Debug.WriteLine("[资源管理] 切换到沉浸式模式 - 停止多图模式");
                
                // 停止多图模式的加载
                _isLoadingMore = false;
                
                WallpaperScrollViewer.Visibility = Visibility.Collapsed;
                ImmersiveFlipView.Visibility = Visibility.Visible;
                CustomTitleBar.Visibility = Visibility.Collapsed;  // 隐藏自定义标题栏内容
                
                // 隐藏标题栏行（高度设为0）
                TitleBarRow.Height = new GridLength(0);
                
                btnToggleViewMode.Content = "\uE8A9";  // 网格图标
                ToolTipService.SetToolTip(btnToggleViewMode, "切换到网格模式");
                
                // 如果沉浸式数据源为空，立即加载第一批（加载两次，确保有足够缓冲）
                if (_immersiveWallpapers.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine("[浏览模式] 切换到沉浸式模式 - 开始加载第一批壁纸");
                    _ = Task.Run(async () =>
                    {
                        await LoadImmersiveWallpapersAsync();  // 第1批：4张
                        await LoadImmersiveWallpapersAsync();  // 第2批：4张，共8张
                    });
                }
                else
                {
                    // 恢复保存的位置
                    if (_savedImmersiveIndex >= 0 && _savedImmersiveIndex < _immersiveWallpapers.Count)
                    {
                        ImmersiveFlipView.SelectedIndex = _savedImmersiveIndex;
                        System.Diagnostics.Debug.WriteLine($"[浏览模式] 恢复沉浸式模式 - 位置: {_savedImmersiveIndex}/{_immersiveWallpapers.Count}");
                    }
                    else
                    {
                        ImmersiveFlipView.SelectedIndex = 0;
                        System.Diagnostics.Debug.WriteLine($"[浏览模式] 恢复沉浸式模式 - 共有 {_immersiveWallpapers.Count} 张壁纸");
                    }
                }
            }
            else
            {
                // 切换到网格模式 - 保存当前位置
                System.Diagnostics.Debug.WriteLine("[资源管理] 切换到网格模式 - 停止沉浸式模式");
                
                // 保存沉浸式模式的当前位置
                if (ImmersiveFlipView.SelectedIndex >= 0)
                {
                    _savedImmersiveIndex = ImmersiveFlipView.SelectedIndex;
                    System.Diagnostics.Debug.WriteLine($"[浏览模式] 保存沉浸式模式位置: {_savedImmersiveIndex}");
                }
                
                // 停止沉浸式模式的加载
                _isLoadingImmersive = false;
                
                WallpaperScrollViewer.Visibility = Visibility.Visible;
                ImmersiveFlipView.Visibility = Visibility.Collapsed;
                CustomTitleBar.Visibility = Visibility.Visible;  // 显示自定义标题栏
                
                // 显示标题栏行（高度设为48）
                TitleBarRow.Height = new GridLength(48);
                
                btnToggleViewMode.Content = "\uE740";  // 全屏图标
                ToolTipService.SetToolTip(btnToggleViewMode, "切换到沉浸式模式");
                
                // 如果多图模式数据为空，初始化并加载
                if (_wallpapers.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine("[浏览模式] 开始多图模式 - 加载壁纸");
                    _ = LoadMoreWallpapersAsync();
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[浏览模式] 恢复多图模式 - 共有 {_wallpapers.Count} 张壁纸");
                }
            }
        }
        
        /// <summary>
        /// 沉浸式模式 - 加载壁纸（每次加载3张，每个源1张）
        /// </summary>
        private async System.Threading.Tasks.Task LoadImmersiveWallpapersAsync()
        {
            if (_isLoadingImmersive)
            {
                System.Diagnostics.Debug.WriteLine("[沉浸式加载] 已在加载中，跳过");
                return;
            }
            
            // 检查是否仍在沉浸式模式
            if (!_isImmersiveMode)
            {
                System.Diagnostics.Debug.WriteLine("[沉浸式加载] 已切换到多图模式，停止加载");
                return;
            }
            
            // 检查网络连接
            if (!IsNetworkAvailable())
            {
                System.Diagnostics.Debug.WriteLine("[沉浸式加载] ❌ 无网络连接");
                if (_immersiveWallpapers.Count == 0)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        ShowToast("无网络连接，请检查网络设置", isSuccess: false, durationMs: 3000);
                    });
                }
                return;
            }
            
            try
            {
                _isLoadingImmersive = true;
                _immersiveLoadCount++;
                
                System.Diagnostics.Debug.WriteLine($"[沉浸式加载] ========== 开始加载第 {_immersiveLoadCount} 批（4个源各1张）==========");
                
                // 开始加载时，如果用户在最后一张，显示进度条
                DispatcherQueue.TryEnqueue(() =>
                {
                    UpdateImmersiveLoadingIndicator();
                });
                
                int loadedCount = 0;
                bool isFirstLoad = _immersiveWallpapers.Count == 0;
                
                // 并行加载四个源，每获取一张立即添加
                var tasks = new List<System.Threading.Tasks.Task>
                {
                    // Pexels
                    System.Threading.Tasks.Task.Run(async () =>
                    {
                        try
                        {
                            var pexelsWallpapers = await _pexelsApi.GetCuratedWallpapersAsync(perPage: 5);
                            if (pexelsWallpapers.Count > 0)
                            {
                                var wallpaper = pexelsWallpapers.First();
                                
                                // 立即添加到UI线程
                                DispatcherQueue.TryEnqueue(() =>
                                {
                                    _immersiveWallpapers.Add(wallpaper);
                                    loadedCount++;
                                    
                                    // 如果是第一张，立即选中
                                    if (isFirstLoad && _immersiveWallpapers.Count == 1)
                                    {
                                        ImmersiveFlipView.SelectedIndex = 0;
                                    }
                                    
                                    System.Diagnostics.Debug.WriteLine($"[沉浸式加载] ✓ Pexels: {wallpaper.Title} (立即显示)");
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[沉浸式加载] ❌ Pexels失败: {ex.GetType().Name} - {ex.Message}");
                            System.Diagnostics.Debug.WriteLine($"[沉浸式加载] 详细信息: {ex}");
                        }
                    }),
                    
                    // Bing
                    System.Threading.Tasks.Task.Run(async () =>
                    {
                        try
                        {
                            var bingWallpapers = await _bingApi.GetDailyWallpapersAsync(1);
                            if (bingWallpapers.Count > 0)
                            {
                                var wallpaper = bingWallpapers.First();
                                
                                // 立即添加到UI线程
                                DispatcherQueue.TryEnqueue(() =>
                                {
                                    _immersiveWallpapers.Add(wallpaper);
                                    loadedCount++;
                                    
                                    // 如果是第一张，立即选中
                                    if (isFirstLoad && _immersiveWallpapers.Count == 1)
                                    {
                                        ImmersiveFlipView.SelectedIndex = 0;
                                    }
                                    
                                    System.Diagnostics.Debug.WriteLine($"[沉浸式加载] ✓ Bing: {wallpaper.Title} (立即显示)");
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[沉浸式加载] ❌ Bing失败: {ex.GetType().Name} - {ex.Message}");
                            System.Diagnostics.Debug.WriteLine($"[沉浸式加载] 详细信息: {ex}");
                        }
                    }),
                    
                    // 360
                    System.Threading.Tasks.Task.Run(async () =>
                    {
                        try
                        {
                            var qihuWallpapers = await _qihuApi.GetWallpapersAsync(countPerCategory: 1);
                            if (qihuWallpapers.Count > 0)
                            {
                                var wallpaper = qihuWallpapers.First();
                                
                                // 立即添加到UI线程
                                DispatcherQueue.TryEnqueue(() =>
                                {
                                    _immersiveWallpapers.Add(wallpaper);
                                    loadedCount++;
                                    
                                    // 如果是第一张，立即选中
                                    if (isFirstLoad && _immersiveWallpapers.Count == 1)
                                    {
                                        ImmersiveFlipView.SelectedIndex = 0;
                                    }
                                    
                                    System.Diagnostics.Debug.WriteLine($"[沉浸式加载] ✓ 360: {wallpaper.Title} (立即显示)");
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[沉浸式加载] ❌ 360失败: {ex.GetType().Name} - {ex.Message}");
                            System.Diagnostics.Debug.WriteLine($"[沉浸式加载] 详细信息: {ex}");
                        }
                    }),
                    
                    // Unsplash
                    System.Threading.Tasks.Task.Run(async () =>
                    {
                        try
                        {
                            var unsplashWallpapers = await _unsplashApi.GetRandomWallpapersAsync(1);
                            if (unsplashWallpapers.Count > 0)
                            {
                                var wallpaper = unsplashWallpapers.First();
                                
                                // 立即添加到UI线程
                                DispatcherQueue.TryEnqueue(() =>
                                {
                                    _immersiveWallpapers.Add(wallpaper);
                                    loadedCount++;
                                    
                                    // 如果是第一张，立即选中
                                    if (isFirstLoad && _immersiveWallpapers.Count == 1)
                                    {
                                        ImmersiveFlipView.SelectedIndex = 0;
                                    }
                                    
                                    System.Diagnostics.Debug.WriteLine($"[沉浸式加载] ✓ Unsplash: {wallpaper.Title} (立即显示)");
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[沉浸式加载] ❌ Unsplash失败: {ex.GetType().Name} - {ex.Message}");
                            System.Diagnostics.Debug.WriteLine($"[沉浸式加载] 详细信息: {ex}");
                        }
                    })
                };
                
                // 等待所有任务完成
                await System.Threading.Tasks.Task.WhenAll(tasks);
                
                System.Diagnostics.Debug.WriteLine($"[沉浸式加载] ✅ 第{_immersiveLoadCount}批完成，加载{loadedCount}张，当前共{_immersiveWallpapers.Count}张");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[沉浸式加载] ❌ 总体失败: {ex.GetType().Name} - {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[沉浸式加载] 详细信息: {ex}");
            }
            finally
            {
                _isLoadingImmersive = false;
                
                // 加载完成后，根据当前位置决定是否显示进度条和继续加载
                DispatcherQueue.TryEnqueue(() =>
                {
                    UpdateImmersiveLoadingIndicator();
                    
                    // 如果当前还在浏览（距离末尾较近），继续加载下一批
                    var currentIndex = ImmersiveFlipView.SelectedIndex;
                    var remainingCount = _immersiveWallpapers.Count - currentIndex - 1;
                    
                    if (remainingCount <= 5)
                    {
                        System.Diagnostics.Debug.WriteLine($"[沉浸式持续加载] 完成一批后检查，剩余{remainingCount}张，继续加载");
                        _ = LoadImmersiveWallpapersAsync();
                    }
                });
            }
        }
        
        /// <summary>
        /// 沉浸式模式 - 滚轮切换图片
        /// </summary>
        private void ImmersiveFlipView_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (!_isImmersiveMode) return;
            
            // 事件节流：防止滚轮滚动过快导致卡顿
            var now = DateTime.Now;
            if ((now - _lastWheelTime).TotalMilliseconds < WHEEL_THROTTLE_MS)
            {
                e.Handled = true;
                return;
            }
            _lastWheelTime = now;
            
            var delta = e.GetCurrentPoint(ImmersiveFlipView).Properties.MouseWheelDelta;
            var currentIndex = ImmersiveFlipView.SelectedIndex;
            var totalCount = _immersiveWallpapers.Count;
            var remainingCount = totalCount - currentIndex - 1;  // 剩余图片数量
            
            if (delta > 0)
            {
                // 向上滚动 - 上一张
                if (currentIndex > 0)
                {
                    ImmersiveFlipView.SelectedIndex = currentIndex - 1;
                    System.Diagnostics.Debug.WriteLine($"[沉浸式滚轮] 上一张: {currentIndex} → {currentIndex - 1} / 共{totalCount}张");
                }
            }
            else if (delta < 0)
            {
                // 向下滚动 - 下一张
                if (currentIndex < totalCount - 1)
                {
                    ImmersiveFlipView.SelectedIndex = currentIndex + 1;
                    System.Diagnostics.Debug.WriteLine($"[沉浸式滚轮] 下一张: {currentIndex} → {currentIndex + 1} / 共{totalCount}张 (剩余{remainingCount - 1}张)");
                    
                    // 预加载策略：剩余图片 <= 5张时，触发后台加载
                    if (remainingCount <= 5 && !_isLoadingImmersive)
                    {
                        System.Diagnostics.Debug.WriteLine($"[沉浸式预加载] 剩余{remainingCount - 1}张，触发后台加载");
                        _ = LoadImmersiveWallpapersAsync();
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[沉浸式滚轮] 已到最后一张 ({currentIndex + 1}/{totalCount})");
                    
                    // 到达最后一张，立即触发加载
                    if (!_isLoadingImmersive)
                    {
                        System.Diagnostics.Debug.WriteLine("[沉浸式预加载] 最后一张，立即加载");
                        _ = LoadImmersiveWallpapersAsync();
                    }
                }
            }
            
            // 动态控制进度条显示：只在浏览到最后一张且正在加载时显示
            UpdateImmersiveLoadingIndicator();
            
            e.Handled = true;
        }
        
        /// <summary>
        /// 沉浸式模式 - 图片加载时设置源（优先FullUrl原始分辨率）
        /// Win11: 直接 HTTPS 加载（快速）
        /// Win10: 先下载到本地再加载（兼容）
        /// </summary>
        private async void ImmersiveImage_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Microsoft.UI.Xaml.Controls.Image image && image.DataContext is Wallpaper wallpaper)
            {
                // 优先使用FullUrl（原始高清图），如果为空则使用ThumbUrl（缩略图）
                var imageUrl = !string.IsNullOrEmpty(wallpaper.FullUrl) ? wallpaper.FullUrl : wallpaper.ThumbUrl;
                
                try
                {
                    var index = _immersiveWallpapers.IndexOf(wallpaper);
                    System.Diagnostics.Debug.WriteLine($"[沉浸式图片] 开始加载第 {index + 1}/{_immersiveWallpapers.Count} 张: {wallpaper.Title}");
                    
                    // Win11: 直接加载 HTTPS URL（快速，无缓存）
                    if (_isWindows11)
                    {
                        System.Diagnostics.Debug.WriteLine("[沉浸式图片] Win11 模式 - 直接加载 HTTPS");
                        TryLoadImageFromUrl(image, imageUrl, wallpaper.Title);
                    }
                    // Win10: 下载到本地再加载（兼容，有缓存）
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[沉浸式图片] Win10 模式 - 下载后加载");
                        
                        if (!string.IsNullOrEmpty(wallpaper.LocalPath) && System.IO.File.Exists(wallpaper.LocalPath))
                        {
                            // 如果已有本地缓存，直接使用
                            System.Diagnostics.Debug.WriteLine("[沉浸式图片] ✅ 使用本地缓存");
                            LoadImageFromLocalPath(image, wallpaper.LocalPath, wallpaper.Title);
                        }
                        else
                        {
                            // 下载图片到本地
                            System.Diagnostics.Debug.WriteLine("[沉浸式图片] 开始下载图片...");
                            var localPath = await DownloadImageToLocalAsync(imageUrl, wallpaper.Title);
                            
                            if (!string.IsNullOrEmpty(localPath) && System.IO.File.Exists(localPath))
                            {
                                wallpaper.LocalPath = localPath;  // 保存本地路径
                                System.Diagnostics.Debug.WriteLine("[沉浸式图片] ✅ 下载成功");
                                LoadImageFromLocalPath(image, localPath, wallpaper.Title);
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine("[沉浸式图片] ❌ 下载失败");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[沉浸式图片] ❌ 加载失败: {wallpaper.Title}");
                    System.Diagnostics.Debug.WriteLine($"[沉浸式图片] 错误: {ex.Message}");
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[沉浸式图片] ⚠️ DataContext为空或类型不匹配");
            }
        }
        
        /// <summary>
        /// 从本地路径加载图片（Win10/Win11 通用）
        /// </summary>
        private void LoadImageFromLocalPath(Microsoft.UI.Xaml.Controls.Image image, string localPath, string title)
        {
            try
            {
                var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                bitmap.UriSource = new Uri($"file:///{localPath.Replace("\\", "/")}");
                image.Source = bitmap;
                System.Diagnostics.Debug.WriteLine($"[沉浸式图片] ✅ 从本地加载成功: {title}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[沉浸式图片] ❌ 本地加载失败: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 尝试直接从 URL 加载图片（降级方案）
        /// </summary>
        private void TryLoadImageFromUrl(Microsoft.UI.Xaml.Controls.Image image, string imageUrl, string title)
        {
            try
            {
                var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                bitmap.UriSource = new Uri(imageUrl);
                image.Source = bitmap;
                System.Diagnostics.Debug.WriteLine($"[沉浸式图片] ⚠️ 降级到直接 URL 加载: {title}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[沉浸式图片] ❌ URL 加载也失败: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 下载图片到本地临时文件夹
        /// </summary>
        private async System.Threading.Tasks.Task<string> DownloadImageToLocalAsync(string imageUrl, string title)
        {
            try
            {
                var picturesFolder = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyPictures);
                var cacheFolder = System.IO.Path.Combine(picturesFolder, "小K壁纸");
                
                if (!System.IO.Directory.Exists(cacheFolder))
                {
                    System.IO.Directory.CreateDirectory(cacheFolder);
                }
                
                // 使用 URL 哈希作为文件名
                var urlHash = Math.Abs(imageUrl.GetHashCode()).ToString();
                var fileName = $"wallpaper_{urlHash}.jpg";
                var localPath = System.IO.Path.Combine(cacheFolder, fileName);
                
                // 如果已存在，直接返回
                if (System.IO.File.Exists(localPath))
                {
                    return localPath;
                }
                
                // 下载图片
                using var httpClient = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler
                {
                    UseProxy = false,
                    Proxy = null
                })
                {
                    Timeout = TimeSpan.FromSeconds(30)
                };
                
                var imageBytes = await httpClient.GetByteArrayAsync(imageUrl);
                await System.IO.File.WriteAllBytesAsync(localPath, imageBytes);
                
                System.Diagnostics.Debug.WriteLine($"[沉浸式图片] 图片已下载: {imageBytes.Length / 1024} KB");
                return localPath;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[沉浸式图片] 下载失败: {ex.Message}");
                return string.Empty;
            }
        }
        
        /// <summary>
        /// 多图模式 - 图片加载时设置源
        /// Win11: 直接 HTTPS 加载
        /// Win10: 先下载到本地再加载
        /// </summary>
        private async void GridImage_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Microsoft.UI.Xaml.Controls.Image image && image.DataContext is Wallpaper wallpaper)
            {
                try
                {
                    var imageUrl = wallpaper.ThumbUrl;  // 多图模式使用缩略图
                    
                    // Win11: 直接加载 HTTPS URL
                    if (_isWindows11)
                    {
                        TryLoadImageFromUrl(image, imageUrl, wallpaper.Title);
                    }
                    // Win10: 下载到本地再加载
                    else
                    {
                        if (!string.IsNullOrEmpty(wallpaper.LocalPath) && System.IO.File.Exists(wallpaper.LocalPath))
                        {
                            LoadImageFromLocalPath(image, wallpaper.LocalPath, wallpaper.Title);
                        }
                        else
                        {
                            var localPath = await DownloadImageToLocalAsync(imageUrl, wallpaper.Title);
                            
                            if (!string.IsNullOrEmpty(localPath) && System.IO.File.Exists(localPath))
                            {
                                wallpaper.LocalPath = localPath;
                                LoadImageFromLocalPath(image, localPath, wallpaper.Title);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[多图模式] 图片加载失败: {wallpaper.Title} | {ex.Message}");
                }
            }
        }
        
        /// <summary>
        /// 多图模式 - 图片加载完成
        /// </summary>
        private void GridImage_Opened(object sender, RoutedEventArgs e)
        {
            // 可以在这里添加淡入动画等效果
            if (sender is Microsoft.UI.Xaml.Controls.Image image)
            System.Diagnostics.Debug.WriteLine("[多图模式] ✅ 图片显示成功");
        }
        
        /// <summary>
        /// 沉浸式模式 - 图片加载完成后渐显
        /// </summary>
        private void ImmersiveImage_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is Microsoft.UI.Xaml.Controls.Image image)
            {
                // 隐藏加载指示器
                if (image.Parent is Grid grid)
                {
                    var loadingRing = grid.FindName("LoadingRing") as ProgressRing;
                    if (loadingRing != null)
                    {
                        loadingRing.IsActive = false;
                        loadingRing.Visibility = Visibility.Collapsed;
                    }
                }
                
                // 图片渐显动画
                var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                {
                    From = 0,
                    To = 1.0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(300)),
                    EasingFunction = new Microsoft.UI.Xaml.Media.Animation.QuadraticEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut }
                };
                
                var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
                storyboard.Children.Add(animation);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, image);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, "Opacity");
                storyboard.Begin();
                
                System.Diagnostics.Debug.WriteLine("[沉浸式模式] 图片加载完成，开始渐显");
            }
        }
        
        /// <summary>
        /// 沉浸式模式 - 双击图片设置壁纸
        /// </summary>
        private async void ImmersiveImage_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            // 从FlipView的当前选中项获取壁纸
            if (ImmersiveFlipView.SelectedItem is Wallpaper wallpaper)
            {
                await SetWallpaperAsync(wallpaper);
            }
        }
        
        /// <summary>
        /// 沉浸式模式 - 右键点击图片
        /// </summary>
        private void ImmersiveImage_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {
            // 保存当前右键点击的壁纸
            if (ImmersiveFlipView.SelectedItem is Wallpaper wallpaper)
            {
                _currentContextWallpaper = wallpaper;
                System.Diagnostics.Debug.WriteLine($"[右键菜单] 当前壁纸: {wallpaper.Title}");
            }
        }
        
        /// <summary>
        /// 网格项右键点击
        /// </summary>
        private void WallpaperGridItem_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {
            // 从 DataContext 获取壁纸对象
            if (sender is FrameworkElement element && element.DataContext is Wallpaper wallpaper)
            {
                _currentContextWallpaper = wallpaper;
                System.Diagnostics.Debug.WriteLine($"[右键菜单] 网格项: {wallpaper.Title}");
            }
        }
        
        /// <summary>
        /// 右键菜单 - 设为桌面背景
        /// </summary>
        private async void SetAsDesktopWallpaper_Click(object sender, RoutedEventArgs e)
        {
            if (_currentContextWallpaper != null)
            {
                System.Diagnostics.Debug.WriteLine($"[右键菜单] 设置桌面背景: {_currentContextWallpaper.Title}");
                await SetWallpaperAsync(_currentContextWallpaper);
            }
        }
        
        /// <summary>
        /// 右键菜单 - 设为锁屏界面
        /// </summary>
        private async void SetAsLockScreen_Click(object sender, RoutedEventArgs e)
        {
            if (_currentContextWallpaper != null)
            {
                System.Diagnostics.Debug.WriteLine($"[右键菜单] 设置锁屏界面: {_currentContextWallpaper.Title}");
                await SetLockScreenAsync(_currentContextWallpaper);
            }
        }
        
        /// <summary>
        /// 右键菜单 - 设为锁屏壁纸（别名方法）
        /// </summary>
        private async void SetAsLockScreenWallpaper_Click(object sender, RoutedEventArgs e)
        {
            if (_currentContextWallpaper != null)
            {
                System.Diagnostics.Debug.WriteLine($"[右键菜单] 设置锁屏壁纸: {_currentContextWallpaper.Title}");
                await SetLockScreenAsync(_currentContextWallpaper);
            }
        }
        
        /// <summary>
        /// 设置锁屏壁纸
        /// </summary>
        private async System.Threading.Tasks.Task SetLockScreenAsync(Wallpaper wallpaper)
        {
            try
            {
                var result = await _wallpaperService.SetLockScreenAsync(wallpaper.FullUrl, wallpaper.Title);
                
                if (result)
                {
                    System.Diagnostics.Debug.WriteLine($"[锁屏设置] ✅ 成功: {wallpaper.Title}");
                    ShowToast("锁屏界面设置成功！", isSuccess: true);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[锁屏设置] ❌ 失败: {wallpaper.Title}");
                    ShowToast("锁屏界面设置失败", isSuccess: false);
                }
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[锁屏设置] ❌ COM异常: {ex.Message}");
                ShowToast("权限不足，无法设置锁屏", isSuccess: false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[锁屏设置] ❌ 异常: {ex.Message}");
                ShowToast("锁屏界面设置失败", isSuccess: false);
            }
        }
        
        /// <summary>
        /// 获取时间间隔文本
        /// </summary>
        private string GetIntervalText(double minutes)
        {
            if (minutes < 1)
            {
                var seconds = (int)(minutes * 60);
                return $"每{seconds}秒";
            }

            return (int)minutes switch
            {
                10 => "每10分钟",
                30 => "每30分钟",
                60 => "每1小时",
                120 => "每2小时",
                360 => "每6小时",
                720 => "每12小时",
                1440 => "每1天",
                _ => minutes >= 60 ? $"每{(int)(minutes / 60)}小时" : $"每{(int)minutes}分钟"
            };
        }

        /// <summary>
        /// 停止桌面定时器
        /// </summary>
        private void StopDesktopTimer()
        {
            if (_desktopWallpaperTimer != null)
            {
                _desktopWallpaperTimer.Stop();
                _desktopWallpaperTimer.Tick -= OnDesktopTimerTick;
                _desktopWallpaperTimer = null;
                _isDesktopTimerEnabled = false;

                _appSettings.IsDesktopTimerEnabled = false;
                SaveAppSettings();

                System.Diagnostics.Debug.WriteLine("[定时器] 桌面定时器已停止");
                _trayManager?.ShowNotification("小K壁纸", "桌面壁纸定时切换已关闭");
            }
        }

        /// <summary>
        /// 停止锁屏定时器
        /// </summary>
        private void StopLockScreenTimer()
        {
            if (_lockScreenWallpaperTimer != null)
            {
                _lockScreenWallpaperTimer.Stop();
                _lockScreenWallpaperTimer.Tick -= OnLockScreenTimerTick;
                _lockScreenWallpaperTimer = null;
                _isLockScreenTimerEnabled = false;

                _appSettings.IsLockScreenTimerEnabled = false;
                SaveAppSettings();

                System.Diagnostics.Debug.WriteLine("[定时器] 锁屏定时器已停止");
                _trayManager?.ShowNotification("小K壁纸", "锁屏壁纸定时切换已关闭");
            }
        }

        /// <summary>
        /// 右键菜单 - 设置桌面定时切换间隔
        /// </summary>
        private void SetDesktopTimerInterval_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag != null)
            {
                double minutes = item.Tag is int tagInt ? tagInt :
                                item.Tag is double tagDouble ? tagDouble :
                                double.Parse(item.Tag.ToString() ?? "0");

                if (minutes == 0)
                {
                    StopDesktopTimer();
                    ShowToast("已关闭桌面定时切换", isSuccess: true);
                }
                else
                {
                    StartWallpaperTimer(isDesktop: true, intervalMinutes: minutes);
                    var intervalText = GetIntervalText(minutes);
                    ShowToast($"已启用桌面定时切换（{intervalText}）", isSuccess: true);
                }
            }
        }

        /// <summary>
        /// 右键菜单 - 设置锁屏定时切换间隔
        /// </summary>
        private void SetLockScreenTimerInterval_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag != null)
            {
                double minutes = item.Tag is int tagInt ? tagInt :
                                item.Tag is double tagDouble ? tagDouble :
                                double.Parse(item.Tag.ToString() ?? "0");

                if (minutes == 0)
                {
                    StopLockScreenTimer();
                    ShowToast("已关闭锁屏定时切换", isSuccess: true);
                }
                else
                {
                    StartWallpaperTimer(isDesktop: false, intervalMinutes: minutes);
                    var intervalText = GetIntervalText(minutes);
                    ShowToast($"已启用锁屏定时切换（{intervalText}）", isSuccess: true);
                }
            }
        }

        /// <summary>
        /// 启动壁纸定时切换
        /// </summary>
        private void StartWallpaperTimer(bool isDesktop, double intervalMinutes)
        {
            try
            {
                if (isDesktop)
                {
                    // 停止现有的桌面定时器
                    if (_desktopWallpaperTimer != null)
                    {
                        _desktopWallpaperTimer.Stop();
                        _desktopWallpaperTimer.Tick -= OnDesktopTimerTick;
                        _desktopWallpaperTimer = null;
                    }

                    // 创建新的桌面定时器
                    _desktopWallpaperTimer = new DispatcherTimer();
                    _desktopWallpaperTimer.Interval = TimeSpan.FromMinutes(intervalMinutes);
                    _desktopWallpaperTimer.Tick += OnDesktopTimerTick;
                    _desktopWallpaperTimer.Start();
                    _isDesktopTimerEnabled = true;

                    // 更新设置
                    _appSettings.IsDesktopTimerEnabled = true;
                    _appSettings.DesktopTimerInterval = intervalMinutes;
                    SaveAppSettings();

                    System.Diagnostics.Debug.WriteLine($"[定时器] 桌面定时器已启动，间隔: {intervalMinutes}分钟");
                }
                else
                {
                    // 停止现有的锁屏定时器
                    if (_lockScreenWallpaperTimer != null)
                    {
                        _lockScreenWallpaperTimer.Stop();
                        _lockScreenWallpaperTimer.Tick -= OnLockScreenTimerTick;
                        _lockScreenWallpaperTimer = null;
                    }

                    // 创建新的锁屏定时器
                    _lockScreenWallpaperTimer = new DispatcherTimer();
                    _lockScreenWallpaperTimer.Interval = TimeSpan.FromMinutes(intervalMinutes);
                    _lockScreenWallpaperTimer.Tick += OnLockScreenTimerTick;
                    _lockScreenWallpaperTimer.Start();
                    _isLockScreenTimerEnabled = true;

                    // 更新设置
                    _appSettings.IsLockScreenTimerEnabled = true;
                    _appSettings.LockScreenTimerInterval = intervalMinutes;
                    SaveAppSettings();

                    System.Diagnostics.Debug.WriteLine($"[定时器] 锁屏定时器已启动，间隔: {intervalMinutes}分钟");
                }

                // 更新定时状态指示器
                UpdateTimerStatusIndicator();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[定时器] 启动失败: {ex.Message}");
                ShowToast("定时切换启动失败", isSuccess: false);
            }
        }

        /// <summary>
        /// 桌面壁纸定时器回调
        /// </summary>
        private async void OnDesktopTimerTick(object sender, object e)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[定时器] 桌面壁纸定时切换触发");

                // 随机选择一张壁纸
                Wallpaper? wallpaper = null;
                if (_wallpapers.Count > 0)
                {
                    var random = new Random();
                    wallpaper = _wallpapers[random.Next(_wallpapers.Count)];
                }
                else if (_immersiveWallpapers.Count > 0)
                {
                    var random = new Random();
                    wallpaper = _immersiveWallpapers[random.Next(_immersiveWallpapers.Count)];
                }

                if (wallpaper != null)
                {
                    await SetWallpaperAsync(wallpaper);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[定时器] 桌面壁纸切换失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 锁屏壁纸定时器回调
        /// </summary>
        private async void OnLockScreenTimerTick(object sender, object e)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[定时器] 锁屏壁纸定时切换触发");

                // 随机选择一张壁纸
                Wallpaper? wallpaper = null;
                if (_wallpapers.Count > 0)
                {
                    var random = new Random();
                    wallpaper = _wallpapers[random.Next(_wallpapers.Count)];
                }
                else if (_immersiveWallpapers.Count > 0)
                {
                    var random = new Random();
                    wallpaper = _immersiveWallpapers[random.Next(_immersiveWallpapers.Count)];
                }

                if (wallpaper != null)
                {
                    await SetLockScreenAsync(wallpaper);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[定时器] 锁屏壁纸切换失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 右键菜单 - 切换开机启动
        /// </summary>
        private void ToggleStartup_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                bool isEnabled = IsStartupEnabled();
                if (isEnabled)
                {
                    DisableStartup();
                    ShowToast("已关闭开机启动", isSuccess: true);
                }
                else
                {
                    EnableStartup();
                    ShowToast("已启用开机启动", isSuccess: true);
                }

                // 更新设置
                _appSettings.IsStartupEnabled = !isEnabled;
                SaveAppSettings();

                // 更新菜单项状态
                UpdateStartupMenuItemStatus();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[开机启动] 切换失败: {ex.Message}");
                ShowToast("开机启动设置失败", isSuccess: false);
            }
        }

        /// <summary>
        /// 启用开机启动
        /// </summary>
        private void EnableStartup()
        {
            try
            {
                // 使用注册表实现开机启动
                var keyPath = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath, true);
                if (key != null)
                {
                    var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        // 添加开机启动项，使用 /background 参数
                        key.SetValue("小K壁纸", $"\"{exePath}\" /background");
                        System.Diagnostics.Debug.WriteLine("[开机启动] 已启用");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[开机启动] 启用失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 禁用开机启动
        /// </summary>
        private void DisableStartup()
        {
            try
            {
                // 从注册表移除开机启动项
                var keyPath = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath, true);
                if (key != null)
                {
                    key.DeleteValue("小K壁纸", false);
                    System.Diagnostics.Debug.WriteLine("[开机启动] 已禁用");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[开机启动] 禁用失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 检查开机启动是否启用
        /// </summary>
        private bool IsStartupEnabled()
        {
            try
            {
                var keyPath = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath);
                if (key != null)
                {
                    var value = key.GetValue("小K壁纸");
                    return value != null;
                }
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[开机启动] 检查失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 更新开机启动菜单项状态
        /// </summary>
        private void UpdateStartupMenuItemStatus()
        {
            try
            {
                // 由于 UI 元素可能尚未初始化，暂时注释掉菜单项更新
                // 在实际使用中，菜单项的文本会在右键菜单打开时动态更新
                System.Diagnostics.Debug.WriteLine("[开机启动] 菜单项状态已更新");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UI] 更新开机启动菜单项失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 检查开机启动任务状态
        /// </summary>
        private async Task CheckStartupTaskAsync()
        {
            try
            {
                await Task.Run(() =>
                {
                    // 后台检查开机启动状态
                    bool isEnabled = IsStartupEnabled();
                    _appSettings.IsStartupEnabled = isEnabled;
                    SaveAppSettings();

                    // 在UI线程更新菜单项
                    this.DispatcherQueue.TryEnqueue(() =>
                    {
                        UpdateStartupMenuItemStatus();
                    });
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[开机启动] 检查任务失败: {ex.Message}");
            }
        }

        #region 缺失的UI事件处理方法

        /// <summary>
        /// 右键菜单打开事件
        /// </summary>
        private void ContextMenu_Opening(object sender, object e)
        {
            try
            {
                // 可以在这里添加右键菜单的动态更新逻辑
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UI] 右键菜单打开失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 切换模式按钮鼠标进入事件
        /// </summary>
        private void btnToggleViewMode_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            try
            {
                btnToggleViewMode.Opacity = 1.0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UI] 按钮鼠标进入事件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 切换模式按钮鼠标离开事件
        /// </summary>
        private void btnToggleViewMode_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            try
            {
                btnToggleViewMode.Opacity = 0.3;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UI] 按钮鼠标离开事件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 全屏图片查看器点击事件
        /// </summary>
        private void FullScreenImageViewer_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            try
            {
                FullScreenImageViewer.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UI] 全屏查看器点击事件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 下载图片事件
        /// </summary>
        private async void DownloadImage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_currentContextWallpaper != null)
                {
                    // 这里可以添加下载图片的逻辑
                    ShowToast("图片下载功能开发中", isSuccess: false);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[下载] 下载图片失败: {ex.Message}");
                ShowToast("下载失败", isSuccess: false);
            }
        }

        /// <summary>
        /// 清理缓存事件
        /// </summary>
        private async void ClearCache_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 这里可以添加清理缓存的逻辑
                ShowToast("缓存清理功能开发中", isSuccess: false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[缓存] 清理失败: {ex.Message}");
                ShowToast("清理失败", isSuccess: false);
            }
        }

        /// <summary>
        /// 壁纸网格项点击事件
        /// </summary>
        private void WallpaperGridItem_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            try
            {
                // 这里可以添加壁纸点击的逻辑
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UI] 壁纸点击事件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 壁纸网格项鼠标进入事件
        /// </summary>
        private void WallpaperGridItem_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            try
            {
                // 这里可以添加鼠标进入的动画效果
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UI] 鼠标进入事件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 壁纸网格项鼠标离开事件
        /// </summary>
        private void WallpaperGridItem_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            try
            {
                // 这里可以添加鼠标离开的动画效果
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UI] 鼠标离开事件失败: {ex.Message}");
            }
        }

        #endregion
    }
}