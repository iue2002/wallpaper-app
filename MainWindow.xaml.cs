using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using App1.Models;
using App1.Services;
using App1.Services.Api;
using App1.Services.Cache;
using App1.Services.Data;
using App1.Services.Network;
using App1.Services.Taskbar;
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
        private readonly DmoeApiService _dmoeApi;
        private readonly PaaaApiService _paaaApi;
        private readonly Upx8ApiService _upx8Api;
        private readonly SeovxApiService _seovxApi;
        private readonly PixabayApiService _pixabayApi;
        private readonly PicsumApiService _picsumApi;
        private readonly WikimediaCommonsApiService _commonsApi;
        private DatabaseService? _databaseService;
        private WallpaperRepository? _repository;
        private readonly CacheService _cacheService;
        private readonly WallpaperService _wallpaperService;

        /// <summary>
        /// 换壁纸串行闸门。连点卡片、双击加右键同时触发时，
        /// 多个设置流程叠在一起会让遮罩状态错乱（第一个请求就把遮罩收了，
        /// 后面的还在跑），看着就像点了没反应。
        /// </summary>
        private readonly System.Threading.SemaphoreSlim _wallpaperSetGate = new(1, 1);

        /// <summary>单击卡片的待执行任务，双击时用它取消掉预览</summary>
        private System.Threading.CancellationTokenSource? _cardTapCts;

        /// <summary>当前全屏预览的是哪一张，用于判断高清图回来时还该不该换上去</summary>
        private Wallpaper? _fullScreenWallpaper;

        /// <summary>
        /// 预览层版本号。关闭动画有延迟，期间用户可能已经点开了另一张，
        /// 靠它判断"这次关闭还算不算数"。
        /// </summary>
        private int _previewVersion;
        
        private readonly ObservableCollection<Wallpaper> _wallpapers = new();
        private bool _isLoadingMore = false;

        /// <summary>
        /// 窗口是否已经真正关闭。关掉之后还在跑的加载流程要立刻停手 ——
        /// 否则这些 async 任务会一直攥着 MainWindow 和它的可视树，
        /// 强制 GC 也收不掉，看起来就是"关了窗口内存没降"。
        /// </summary>
        private bool _isWindowClosed;
        // GridView 模板内部的滚动容器，用于检测"滚到底部"触发补页
        private ScrollViewer? _gridScrollViewer;
        private int _currentPage = 0;
        private const int PAGE_SIZE = 18;  // 每次加载18张（5张Pexels + 10张Bing + 3张360）
        private bool _isImmersiveMode = true;  // 默认沉浸式浏览模式
        
        // 沉浸式模式独立数据源
        private readonly ObservableCollection<Wallpaper> _immersiveWallpapers = new();
        private bool _isLoadingImmersive = false;
        // 沉浸式每批轮换源时用到：记住下一批从哪个源开始，避免总打同样几个
        private int _immersiveSourceCursor = 0;
        private int _immersiveLoadCount = 0;  // 已加载批次计数
        private int _savedImmersiveIndex = 0;  // 保存的沉浸式模式位置
        
        // 右键菜单相关
        private Wallpaper? _currentContextWallpaper = null;  // 当前右键点击的壁纸
        
        // 定时切换功能。
        // 真正的调度在 App 层的 AutoWallpaperService 里跑（System.Threading.Timer，
        // 不依赖 UI）。主窗口关掉销毁之后它照样工作，所以这里只保留 UI 需要的状态：
        // 菜单上打不打勾、状态栏显示什么。
        private bool _isDesktopTimerEnabled = false;  // 桌面定时器是否启用
        private bool _isLockScreenTimerEnabled = false;  // 锁屏定时器是否启用
        
        // 设置管理
        private AppSettings _appSettings = new AppSettings();
        
        // 允许真正关闭的标志（用于从托盘退出时绕过最小化行为）
        
        // 网络状态
        private int _consecutiveNetworkErrors = 0;  // 连续网络错误次数
        private const int MAX_NETWORK_ERRORS = 3;  // 最大连续错误次数，超过后提示用户
        
        // 滚轮事件节流
        private DateTime _lastWheelTime = DateTime.MinValue;
        private const int WHEEL_THROTTLE_MS = 200;  // 滚轮事件节流：200毫秒
        private const int LOAD_MORE_MIN_INTERVAL_MS = 1200;  // 两页之间的最小间隔，防止滚动到底部时连环触发
        private double _lastScrollOffset = 0;
        
        // Windows 版本检测（Win11 可以直接加载 HTTPS，Win10 需要下载）
        private readonly bool _isWindows11;

        public MainWindow()
        {
            this.InitializeComponent();
            
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
            _dmoeApi = new DmoeApiService();
            _paaaApi = new PaaaApiService();
            _upx8Api = new Upx8ApiService();
            _seovxApi = new SeovxApiService();
            _pixabayApi = new PixabayApiService();
            _picsumApi = new PicsumApiService();
            _commonsApi = new WikimediaCommonsApiService();
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

            // 任务栏外观由 App 层负责（关窗口后仍然生效），这里只读它的状态

            // 托盘图标由 App 层的 TrayHost 负责 —— 它是个独立的隐藏窗口，
            // 主窗口关掉之后仍然活着。这里不再自己挂托盘。
            
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
        /// <summary>
        /// 窗口关闭。这里不再拦截 —— 关闭就是真的销毁。
        ///
        /// 原来是把窗口 Hide() 掉，窗口对象、XAML 树、已经解码的图片全都留在内存里，
        /// 所以"最小化到托盘"之后内存其实一点没降。现在窗口关掉，这些全部交给 GC，
        /// 剩下的只有托盘图标和一个定时器。
        ///
        /// 定时器不在这里停：它是 App 层的常驻服务，关窗口不等于关自动换壁纸。
        /// </summary>
        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            // 先立旗子：正在跑的加载循环看到它就停手，不再往集合里塞东西
            _isWindowClosed = true;

            try
            {
                // 先存设置。必须放在释放资源之前，否则"任务栏外观"会被写成关闭。
                SaveAppSettings();

                System.Diagnostics.Debug.WriteLine("[主窗口] 关闭，释放窗口级资源");

                // 任务栏外观不在这里还原：它归 App 层管，关窗口后要继续生效，
                // 只有退出进程时才由 TaskbarAppearanceService.Dispose 还原。

                // 先把数据源摘掉。ItemsSource 还指着集合的话，GridView / FlipView
                // 会一直持有容器，容器里的 Image 又持有位图，整棵树跟着窗口对象一起留着。
                try { WallpaperGridView.ItemsSource = null; } catch { }
                try { ImmersiveFlipView.ItemsSource = null; } catch { }

                // 把可视树里所有 Image.Source 置空，让位图的引用计数归零。
                // 这是"关了窗口内存不降"的真正原因：XAML 的 Image 不释放 Source，
                // 后端的 BitmapImage 就一直活着，几十张图就是几百 MB 的 native 内存，
                // 托管 GC 完全看不到它。
                try { ClearImageSources(Content as DependencyObject); } catch { }

                // 整棵树从窗口上摘下来，XamlRoot 与可视树之间的引用一并断掉
                try { Content = null; } catch { }

                _wallpapers.Clear();
                _immersiveWallpapers.Clear();

                try { _pexelsApi?.Dispose(); } catch { }
                try { _pixabayApi?.Dispose(); } catch { }
                try { _picsumApi?.Dispose(); } catch { }
                try { _commonsApi?.Dispose(); } catch { }
                try { _databaseService?.Dispose(); } catch { }
                _databaseService = null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[主窗口] 关闭时释放资源失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 递归把可视树里所有 Image 的 Source 置空。
        /// 只清集合是不够的：集合里的 Wallpaper 是数据，真正吃内存的是
        /// 每个 Image 背后那张已经解码好的位图。
        /// </summary>
        private static void ClearImageSources(Microsoft.UI.Xaml.DependencyObject? root)
        {
            if (root == null)
            {
                return;
            }

            try
            {
                var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);

                for (var i = 0; i < count; i++)
                {
                    var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);

                    if (child is Microsoft.UI.Xaml.Controls.Image image)
                    {
                        image.Source = null;
                    }

                    ClearImageSources(child);
                }
            }
            catch
            {
                // 树正在销毁时个别节点取不到子元素，忽略
            }
        }

        /// <summary>
        /// 统一安全关闭流程：停止后台任务、释放资源并退出进程
        /// </summary>
        private void Shutdown()
        {
            // 退出的是整个进程，不只是这个窗口。
            // 托盘图标、后台定时器都在 App 层，由它统一收尾。
            try
            {
                SaveAppSettings();

                if (Application.Current is App app)
                {
                    app.ExitApplication();
                    return;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[退出] 安全关闭失败: {ex.Message}");
            }

            Environment.Exit(0);
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
            _isDesktopTimerEnabled = false;
            _isLockScreenTimerEnabled = false;

            _appSettings.IsDesktopTimerEnabled = false;
            _appSettings.IsLockScreenTimerEnabled = false;

            (Application.Current as App)?.ApplyAutoWallpaperSettings(_appSettings);
            System.Diagnostics.Debug.WriteLine("[定时器] 已全部停止");
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

                    // 删文件是纯 IO，别放在 UI 线程上：目录里攒了几百张时
                    // 同步删完界面会明显停顿一下
                    _ = System.Threading.Tasks.Task.Run(() => _wallpaperService.CleanupAllWallpapers());
                    
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
                
                // 启动恢复时先快照，避免恢复桌面定时器时的保存动作覆盖锁屏状态
                bool desktopEnabled = _appSettings.IsDesktopTimerEnabled;
                double desktopInterval = _appSettings.DesktopTimerInterval;
                bool lockScreenEnabled = _appSettings.IsLockScreenTimerEnabled;
                double lockScreenInterval = _appSettings.LockScreenTimerInterval;
                
                // 恢复定时器状态
                if (desktopEnabled && desktopInterval > 0)
                {
                    StartWallpaperTimer(isDesktop: true, intervalMinutes: desktopInterval, saveSettings: false);
                    _isDesktopTimerEnabled = true;
                }
                
                if (lockScreenEnabled && lockScreenInterval > 0)
                {
                    StartWallpaperTimer(isDesktop: false, intervalMinutes: lockScreenInterval, saveSettings: false);
                    _isLockScreenTimerEnabled = true;
                }
                
                // 开机自启以注册表为准。这里只读不写 —— 原来配置里写着启用就顺手
                // 写一次注册表，用户手动关掉之后，下次开窗口又会被塞回去。
                _appSettings.IsStartupEnabled = IsStartupEnabled();

                // 任务栏外观在 App 层启动时已经应用过了，这里不再重复挂
                System.Diagnostics.Debug.WriteLine("[设置] 应用设置已恢复");

                // 把从磁盘读到的定时状态推给后台调度。
                // 两个都关着的时候 StartWallpaperTimer 不会被调，所以这里必须补一次，
                // 否则窗口里显示"关闭"，后台可能还在按旧间隔跑。
                (Application.Current as App)?.ApplyAutoWallpaperSettings(_appSettings);

                // 更新定时状态指示器
                UpdateTimerStatusIndicator();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[设置] 加载设置失败: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 退出前保存设置。App 层要调，所以必须是 public。
        /// （原来走的是反射找一个叫 PersistSettingsForSystemExit 的方法，
        ///   那个方法压根不存在，所以退出时其实一次都没保存过。）
        /// </summary>
        public void SaveSettingsNow()
        {
            SaveAppSettings();
        }

        /// <summary>
        /// 保存应用设置
        /// </summary>
        private void SaveAppSettings()
        {
            try
            {
                _appSettings.IsStartupEnabled = IsStartupEnabled();
                _appSettings.IsDesktopTimerEnabled = _isDesktopTimerEnabled;
                _appSettings.IsLockScreenTimerEnabled = _isLockScreenTimerEnabled;

                // TaskbarEffect 在用户切档位时就已经写进 _appSettings 并保存过，
                // 这里不再从服务反读，免得两个来源打架。

                // 间隔时间在 StartWallpaperTimer 里已经写进 _appSettings，
                // 定时器本身挪到后台了，这里不再从 UI 对象上反读
                if (_appSettings.DesktopTimerInterval <= 0)
                {
                    _appSettings.DesktopTimerInterval = 60; // 默认1小时
                }
                if (_appSettings.LockScreenTimerInterval <= 0)
                {
                    _appSettings.LockScreenTimerInterval = 60; // 默认1小时
                }
                
                SettingsManager.SaveSettings(_appSettings);
                System.Diagnostics.Debug.WriteLine("[设置] 配置已保存");
                System.Diagnostics.Debug.WriteLine($"[设置] 桌面定时器: {(_appSettings.IsDesktopTimerEnabled ? "启用" : "禁用")}, 间隔: {_appSettings.DesktopTimerInterval}分钟");
                System.Diagnostics.Debug.WriteLine($"[设置] 锁屏定时器: {(_appSettings.IsLockScreenTimerEnabled ? "启用" : "禁用")}, 间隔: {_appSettings.LockScreenTimerInterval}分钟");
                
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
                    
                    // 添加定时器，2秒后逐渐消失
                    var fadeTimer = new DispatcherTimer();
                    fadeTimer.Interval = TimeSpan.FromSeconds(2);
                    fadeTimer.Tick += (s, e) =>
                    {
                        fadeTimer.Stop();
                        
                        // 渐出动画
                        var fadeOut = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                        {
                            From = 1,
                            To = 0,
                            Duration = new Duration(TimeSpan.FromMilliseconds(500)),
                            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.QuadraticEase()
                        };
                        
                        var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
                        storyboard.Children.Add(fadeOut);
                        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fadeOut, TimerStatusIndicator);
                        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fadeOut, "Opacity");
                        
                        storyboard.Completed += (s2, e2) =>
                        {
                            TimerStatusIndicator.Visibility = Visibility.Collapsed;
                            TimerStatusIndicator.Opacity = 1; // 重置透明度，以便下次显示
                        };
                        
                        storyboard.Begin();
                    };
                    fadeTimer.Start();
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
                var existingKeys = new HashSet<string>(
                    _wallpapers.Select(w => !string.IsNullOrEmpty(w.FullUrl) ? w.FullUrl : w.ThumbUrl)
                               .Where(k => !string.IsNullOrEmpty(k)),
                    StringComparer.OrdinalIgnoreCase);
                
                void AddWallpapersToUi(IEnumerable<Wallpaper> wallpapers, string sourceTag, int? take = null)
                {
                    if (_isWindowClosed)
                    {
                        return;
                    }

                    var items = take.HasValue ? wallpapers.Take(take.Value) : wallpapers;
                    foreach (var wallpaper in items)
                    {
                        var key = !string.IsNullOrEmpty(wallpaper.FullUrl) ? wallpaper.FullUrl : wallpaper.ThumbUrl;
                        if (string.IsNullOrEmpty(key) || !existingKeys.Add(key))
                        {
                            continue;
                        }
                        
                        _wallpapers.Add(wallpaper);
                        newWallpapersForDb.Add(wallpaper);
                        totalLoaded++;
                        ShowEmpty(false);

                        // 首屏够 8 张就收起转圈，不再等最慢的源（Bing/Seovx 等可能超时或直接失败）
                        if (totalLoaded >= 8)
                        {
                            ShowLoadingMore(false);
                        }

                        System.Diagnostics.Debug.WriteLine($"[{sourceTag}] ✓ 已显示: {wallpaper.Title}");
                    }
                }
                
                // 所有源统一走 SourceGate 并行池，谁先回来谁先上屏。
                // 原来 Pexels / Bing / 360 是串行 await 的，三个加起来要 4.4 秒才轮到后面的源；
                // 而 Bing 本身 0.77 秒就能回，却被排在最后才显示，首屏等于白等了 4 秒多。
                var otherTasks = new Dictionary<string, Task<List<Wallpaper>>>();

                void QueueSource(string tag, string gateKey, Func<Task<List<Wallpaper>>> fetch)
                {
                    otherTasks[tag] = Task.Run(() => SourceGate.RunAsync(gateKey, async () =>
                    {
                        System.Diagnostics.Debug.WriteLine($"[{tag}] 开始获取...");
                        return await fetch();
                    }));
                }

                // Bing 最轻最快（缩略图单张只有 21 KB），放在首屏主力位置，
                // 通常 0.8 秒内就能凑齐首屏，用户不用盯着转圈。
                QueueSource("Bing壁纸", "bing", () => _bingApi.GetDailyWallpapersAsync(8));

                QueueSource("Pexels壁纸", "pexels", async () =>
                    (await _pexelsApi.GetCuratedWallpapersAsync(perPage: 80)).Take(8).ToList());

                QueueSource("360壁纸", "qihu", () => _qihuApi.GetWallpapersAsync(countPerCategory: 2));

                // Dmoe / PAAA / Seovx 实测已失效，默认不排期：
                // 它们不会抛异常（Paaa 返回 200 + 空体，Dmoe 只出 1080p 被规则拦），
                // 所以熔断机制兜不住，每批都固定白等几秒，还占着宝贵的并发槽位。
                // 代码保留，开关一改就能恢复。
                if (App1.Config.AppConfig.EnableDmoe)
                {
                    QueueSource("Dmoe壁纸", "dmoe", () => _dmoeApi.GetWallpapersAsync(8));
                }

                if (App1.Config.AppConfig.EnablePaaa)
                {
                    QueueSource("PAAA壁纸", "paaa", () => _paaaApi.GetWallpapersAsync(6));
                }

                QueueSource("Upx8壁纸", "upx8", () => _upx8Api.GetWallpapersAsync(6));

                if (App1.Config.AppConfig.EnableSeovx)
                {
                    QueueSource("Seovx壁纸", "seovx", () => _seovxApi.GetWallpapersAsync(6));
                }

                QueueSource("Unsplash壁纸", "unsplash", () => _unsplashApi.GetRandomWallpapersAsync(6));
                QueueSource("Commons壁纸", "commons", () => _commonsApi.GetWallpapersAsync(6));
                QueueSource("Pixabay壁纸", "pixabay", () => _pixabayApi.GetWallpapersAsync(6));
                QueueSource("Picsum壁纸", "picsum", () => _picsumApi.GetWallpapersAsync(4));


                var pendingTasks = otherTasks.ToList();
                while (pendingTasks.Count > 0)
                {
                    var completedTask = await Task.WhenAny(pendingTasks.Select(t => t.Value));
                    var completedEntry = pendingTasks.First(t => t.Value == completedTask);
                    pendingTasks.Remove(completedEntry);
                    
                    var result = await completedEntry.Value;
                    if (result.Count > 0)
                    {
                        AddWallpapersToUi(result, completedEntry.Key);
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[流式加载] 加载完成，共加载 {totalLoaded} 张壁纸");
                
                
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
        /// GridView 的滚动由它自己模板内部的 ScrollViewer 承担，视图上并没有
        /// ViewChanged 事件可挂，只能等模板应用后从视觉树里把那个 ScrollViewer 取出来。
        /// </summary>
        private void WallpaperGridView_Loaded(object sender, RoutedEventArgs e)
        {
            if (_gridScrollViewer != null)
            {
                return;   // 已经挂过，避免重复订阅
            }

            WallpaperGridView.ApplyTemplate();

            var inner = FindDescendant<ScrollViewer>(WallpaperGridView);
            if (inner == null)
            {
                System.Diagnostics.Debug.WriteLine("[滚动检测] ⚠️ 未找到 GridView 内部 ScrollViewer，加载更多将只能靠按钮触发");
                return;
            }

            _gridScrollViewer = inner;
            inner.ViewChanged += WallpaperScrollViewer_ViewChanged;
            System.Diagnostics.Debug.WriteLine("[滚动检测] ✅ 已接管 GridView 内部滚动");
        }

        /// <summary>
        /// 在视觉树里深度优先查找第一个 T 类型子元素。
        /// </summary>
        private static T? FindDescendant<T>(Microsoft.UI.Xaml.DependencyObject root) where T : class
        {
            var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);

            for (var i = 0; i < count; i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);

                if (child is T match)
                {
                    return match;
                }

                var deeper = FindDescendant<T>(child);
                if (deeper != null)
                {
                    return deeper;
                }
            }

            return null;
        }

        /// <summary>
        /// 滚动事件：检测是否滚动到底部
        /// </summary>
        private async void WallpaperScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            var scrollViewer = sender as ScrollViewer;
            if (scrollViewer == null) return;
            if (_isImmersiveMode || _isLoadingMore) return;
            
            var verticalOffset = scrollViewer.VerticalOffset;
            // 只有「快到底部」才补页，而不是随便滚一下就来一页。
            // 原来是任何超过 10px 的滚动都会触发加载，滚一次就多一页、多几十张图，
            // 带宽和内存全花在用户根本没看的位置上。
            var remainingPixels = scrollViewer.ScrollableHeight - verticalOffset - scrollViewer.ViewportHeight;
            var offsetDelta = Math.Abs(verticalOffset - _lastScrollOffset);
            var now = DateTime.Now;
            var elapsedMs = (now - _lastWheelTime).TotalMilliseconds;

            if (offsetDelta > 10)
            {
                _lastScrollOffset = verticalOffset;
            }

            if (remainingPixels > 600)
            {
                return;
            }

            if (elapsedMs < LOAD_MORE_MIN_INTERVAL_MS)
            {
                return;
            }

            _lastWheelTime = now;
            System.Diagnostics.Debug.WriteLine($"[滚动检测] 距底部 {remainingPixels:F0}px，加载更多...");
            try
            {
                await LoadMoreWallpapersAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[滚动加载] 异常: {ex.Message}");
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
            // 双击已经在"设为壁纸"了，这一下不要再让父级当成普通点击处理
            e.Handled = true;

            // 双击是「设为壁纸」，不该顺带弹预览 —— 把待执行的单击预览取消掉
            _cardTapCts?.Cancel();

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
            // 闸门必须带超时：上一次如果因为系统调用不返回而挂着，
            // 后来者的 await 会永远排在那里，"正在设置壁纸"也就永远收不掉。
            if (!await _wallpaperSetGate.WaitAsync(TimeSpan.FromSeconds(20)))
            {
                System.Diagnostics.Debug.WriteLine("[壁纸设置] ⚠️ 上一次设置还没收尾，忽略本次点击");
                ShowToast("上一次设置还没结束，请稍候", isSuccess: false);
                return;
            }

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
                System.Diagnostics.Debug.WriteLine($"设置壁纸失败: {ex.Message}");
                
                // 显示错误通知
                ShowToast("壁纸设置失败", isSuccess: false);
            }
            finally
            {
                // 无论成功失败，遮罩都要收掉，闸门也必须放开，
                // 否则一次异常就会让后续的换壁纸请求再也进不来
                SettingWallpaperPanel.Visibility = Visibility.Collapsed;
                _wallpaperSetGate.Release();
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
            
            // 顶部不留标题栏行：两种模式内容都直接顶到窗口最上方，
            // 只让系统绘制的窗口按钮浮在内容之上。
            // 原来网格模式会开一条 48px 的标题栏行，里面只有左侧一个标题文字，
            // 其余全是背景色，看上去就是顶部一条突兀的空白遮罩。
            TitleBarRow.Height = new GridLength(0);
            CustomTitleBar.Visibility = Visibility.Collapsed;
            
            if (_isImmersiveMode)
            {
                // 切换到沉浸式模式
                System.Diagnostics.Debug.WriteLine("[资源管理] 切换到沉浸式模式 - 停止多图模式");
                
                // 停止多图模式的加载
                _isLoadingMore = false;
                
                WallpaperGridView.Visibility = Visibility.Collapsed;
                ImmersiveFlipView.Visibility = Visibility.Visible;
                
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
                
                WallpaperGridView.Visibility = Visibility.Visible;
                ImmersiveFlipView.Visibility = Visibility.Collapsed;
                
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
                
                void AddImmersiveWallpaper(Wallpaper wallpaper, string sourceTag)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        // 入队之后窗口可能已经关了，这里再确认一次
                        if (_isWindowClosed)
                        {
                            return;
                        }

                        _immersiveWallpapers.Add(wallpaper);
                        loadedCount++;
                        
                        if (isFirstLoad && _immersiveWallpapers.Count == 1)
                        {
                            ImmersiveFlipView.SelectedIndex = 0;
                        }
                        
                        System.Diagnostics.Debug.WriteLine($"[沉浸式加载] ✓ {sourceTag}: {wallpaper.Title} (立即显示)");
                    });
                }

                // 首轮优先顺序：Pexels -> Bing -> 360
                try
                {
                    var pexelsWallpapers = await _pexelsApi.GetCuratedWallpapersAsync(perPage: 30);
                    if (pexelsWallpapers.Count > 0)
                    {
                        AddImmersiveWallpaper(pexelsWallpapers.First(), "Pexels");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[沉浸式加载] ❌ Pexels失败: {ex.GetType().Name} - {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"[沉浸式加载] 详细信息: {ex}");
                }

                try
                {
                    var bingWallpapers = await _bingApi.GetDailyWallpapersAsync(1);
                    if (bingWallpapers.Count > 0)
                    {
                        AddImmersiveWallpaper(bingWallpapers.First(), "Bing");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[沉浸式加载] ❌ Bing失败: {ex.GetType().Name} - {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"[沉浸式加载] 详细信息: {ex}");
                }

                try
                {
                    var qihuWallpapers = await _qihuApi.GetWallpapersAsync(countPerCategory: 1);
                    if (qihuWallpapers.Count > 0)
                    {
                        AddImmersiveWallpaper(qihuWallpapers.First(), "360");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[沉浸式加载] ❌ 360失败: {ex.GetType().Name} - {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"[沉浸式加载] 详细信息: {ex}");
                }
                
                // 其余源：每批只轮到 4 个（轮换），不再每批打满全部源。
                // 每批其实只取 1 张，打满 8 个源等于用 8 次请求换 8 张图，
                // 而用户一次只看一张，多出来的全是提前烧掉的带宽和内存。
                var remainingSources = new List<(string Tag, string GateKey, Func<Task<List<Wallpaper>>> Fetch)>
                {
                    ("Unsplash", "unsplash", () => _unsplashApi.GetRandomWallpapersAsync(1)),
                    ("Upx8", "upx8", () => _upx8Api.GetWallpapersAsync(1)),
                    ("Commons", "commons", () => _commonsApi.GetWallpapersAsync(1)),
                    ("Pixabay", "pixabay", () => _pixabayApi.GetWallpapersAsync(1)),
                    ("Picsum", "picsum", () => _picsumApi.GetWallpapersAsync(1))
                };

                // 失效源默认不进轮换表 —— 放进来的话，每轮到它就是白等一次
                if (App1.Config.AppConfig.EnableDmoe)
                {
                    remainingSources.Add(("Dmoe", "dmoe", () => _dmoeApi.GetWallpapersAsync(1)));
                }

                if (App1.Config.AppConfig.EnablePaaa)
                {
                    remainingSources.Add(("PAAA", "paaa", () => _paaaApi.GetWallpapersAsync(1)));
                }

                if (App1.Config.AppConfig.EnableSeovx)
                {
                    remainingSources.Add(("Seovx", "seovx", () => _seovxApi.GetWallpapersAsync(1)));
                }

                const int sourcesPerBatch = 4;
                var batchSources = new List<(string Tag, string GateKey, Func<Task<List<Wallpaper>>> Fetch)>();
                for (var i = 0; i < sourcesPerBatch && i < remainingSources.Count; i++)
                {
                    batchSources.Add(remainingSources[(_immersiveSourceCursor + i) % remainingSources.Count]);
                }
                _immersiveSourceCursor = (_immersiveSourceCursor + sourcesPerBatch) % remainingSources.Count;

                System.Diagnostics.Debug.WriteLine($"[沉浸式加载] 本批轮到的源: {string.Join(" / ", batchSources.Select(s => s.Tag))}");

                var tasks = batchSources.Select(source => System.Threading.Tasks.Task.Run(async () =>
                {
                    var result = await SourceGate.RunAsync(source.GateKey, source.Fetch);
                    if (result.Count > 0)
                    {
                        AddImmersiveWallpaper(result.First(), source.Tag);
                    }
                })).ToList();
                
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
        /// 沉浸式图片的解码目标宽度。
        /// 全屏看图确实要清晰，但没必要按 4K 原图解码：3840x2160 一张位图就是 33MB，
        /// 而窗口再大也不可能显示比屏幕更多的像素。按屏幕物理宽度取证，上限 2560
        /// （2560x1440 约 14MB，只有原图的四成），视觉上看不出差别。
        /// </summary>
        private int GetImmersiveDecodeWidth()
        {
            try
            {
                var scale = ImmersiveFlipView.XamlRoot?.RasterizationScale ?? 1.0;
                var physicalWidth = ImmersiveFlipView.ActualWidth * scale;

                if (physicalWidth < 1024)
                {
                    physicalWidth = 1920;   // 布局还没完成，按 1080p 估一个
                }

                return (int)Math.Clamp(physicalWidth, 1280, 2560);
            }
            catch
            {
                return 1920;
            }
        }

        /// <summary>
        /// 沉浸式模式 - 图片加载时设置源（优先FullUrl原始分辨率）
        /// Win11: 直接 HTTPS 加载（快速）
        /// Win10: 先下载到本地再加载（兼容）
        /// </summary>
        private void ImmersiveImage_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Microsoft.UI.Xaml.Controls.Image image)
            {
                _ = LoadImmersiveImageAsync(image);
            }
        }

        /// <summary>
        /// 加载当前页的大图。解码宽度按屏幕算，见 GetImmersiveDecodeWidth。
        /// 独立成方法是因为翻页释放位图之后还要能重新调起来（SelectionChanged 里会用）。
        /// </summary>
        private async Task LoadImmersiveImageAsync(Microsoft.UI.Xaml.Controls.Image image)
        {
            if (image.DataContext is not Wallpaper wallpaper)
            {
                return;
            }

            // 优先用 FullUrl（原图），为空时退回缩略图
            var imageUrl = !string.IsNullOrEmpty(wallpaper.FullUrl) ? wallpaper.FullUrl : wallpaper.ThumbUrl;
            var decodeWidth = GetImmersiveDecodeWidth();

            try
            {
                var index = _immersiveWallpapers.IndexOf(wallpaper);
                System.Diagnostics.Debug.WriteLine($"[沉浸式图片] 开始加载第 {index + 1}/{_immersiveWallpapers.Count} 张: {wallpaper.Title}");

                // Win11: 直接加载 HTTPS URL（快速，无缓存）
                if (_isWindows11)
                {
                    TryLoadImageFromUrl(image, imageUrl, wallpaper.Title, decodeWidth);
                }
                // Win10: 下载到本地再加载（兼容，有缓存）
                else if (!string.IsNullOrEmpty(wallpaper.LocalPath) && System.IO.File.Exists(wallpaper.LocalPath))
                {
                    LoadImageFromLocalPath(image, wallpaper.LocalPath, wallpaper.Title, decodeWidth);
                }
                else
                {
                    var localPath = await DownloadImageToLocalAsync(imageUrl, wallpaper.Title);

                    if (!string.IsNullOrEmpty(localPath) && System.IO.File.Exists(localPath))
                    {
                        wallpaper.LocalPath = localPath;
                        LoadImageFromLocalPath(image, localPath, wallpaper.Title, decodeWidth);
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[沉浸式图片] ❌ 下载失败");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[沉浸式图片] ❌ 加载失败: {wallpaper.Title}");
                System.Diagnostics.Debug.WriteLine($"[沉浸式图片] 错误: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 从本地路径加载图片（Win10/Win11 通用）
        /// </summary>
        /// <param name="decodePixelWidth">解码目标宽度，0 为原图。列表卡片传小值可省大量解码开销。</param>
        private void LoadImageFromLocalPath(Microsoft.UI.Xaml.Controls.Image image, string localPath, string title, int decodePixelWidth = 0)
        {
            try
            {
                var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();

                if (decodePixelWidth > 0)
                {
                    bitmap.DecodePixelType = Microsoft.UI.Xaml.Media.Imaging.DecodePixelType.Physical;
                    bitmap.DecodePixelWidth = decodePixelWidth;
                }

                bitmap.UriSource = new Uri($"file:///{localPath.Replace("\\", "/")}");
                image.Source = bitmap;
                System.Diagnostics.Debug.WriteLine($"[图片] 本地加载成功: {title} (解码宽度 {decodePixelWidth})");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[图片] 本地加载失败: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 尝试直接从 URL 加载图片
        /// </summary>
        /// <param name="decodePixelWidth">
        /// 解码目标宽度。0 表示按原图解码。
        /// 列表卡片只有 280×200，如果按 2560×1440 原图解码，等于为了显示 5 万像素
        /// 去解 368 万像素，解码和显存都是几十倍浪费 —— 首屏卡顿主要来自这里。
        /// </param>
        private void TryLoadImageFromUrl(Microsoft.UI.Xaml.Controls.Image image, string imageUrl, string title, int decodePixelWidth = 0)
        {
            try
            {
                var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();

                if (decodePixelWidth > 0)
                {
                    // 显式按物理像素解码，避免被系统 DPI 缩放再放大一次
                    bitmap.DecodePixelType = Microsoft.UI.Xaml.Media.Imaging.DecodePixelType.Physical;
                    bitmap.DecodePixelWidth = decodePixelWidth;
                }

                bitmap.UriSource = new Uri(imageUrl);
                image.Source = bitmap;
                System.Diagnostics.Debug.WriteLine($"[图片] 已挂载: {title} (解码宽度 {decodePixelWidth})");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[图片] URL 加载失败: {ex.Message}");
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
                
                // 稳定哈希做文件名，跨进程一致 —— 和 WallpaperService 用的是同一套规则，
                // 列表里下过的图，点「设为壁纸」时能直接命中，不用再下一次。
                var fileName = App1.Services.Imaging.ImageCachePeek.BuildFileName("wallpaper", imageUrl);
                var localPath = System.IO.Path.Combine(cacheFolder, fileName);
                
                // 如果已存在，直接返回
                if (System.IO.File.Exists(localPath))
                {
                    return localPath;
                }
                
                // 下载图片（共享连接池，不再每次重新握手）
                var httpClient = App1.Services.Network.HttpClientProvider.Images;
                
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
        /// <summary>
        /// 卡片图片控件的状态。虚拟化会复用容器，需要记住这张容器当前挂的是哪张图，
        /// 以及圆角裁剪有没有做过（ApplyCardRoundedClip 会订阅 SizeChanged，重复调用会重复订阅）。
        /// </summary>
        private sealed class CardImageState
        {
            public bool ClipApplied;
            public string? LoadedUrl;
        }

        private void GridImage_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Microsoft.UI.Xaml.Controls.Image image)
            {
                return;
            }

            if (image.Tag is not CardImageState state)
            {
                state = new CardImageState();
                image.Tag = state;
            }

            // 圆角裁剪只跟控件本身有关，元素第一次挂载时做一次就够
            if (!state.ClipApplied)
            {
                state.ClipApplied = true;
                ApplyCardRoundedClip(image, 12f);
            }

            _ = LoadCardImageAsync(image);
        }

        /// <summary>
        /// GridView 恢复虚拟化后，容器会被回收再复用给别的项。
        /// 复用那一刻元素并没有离开可视树，Loaded 不会再触发 —— 只靠 Loaded 的话，
        /// 卡片会一直停在上一张图上。这里跟着 DataContext 变化重新取图。
        /// </summary>
        private void GridImage_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            if (sender is Microsoft.UI.Xaml.Controls.Image image)
            {
                _ = LoadCardImageAsync(image);
            }
        }

        /// <summary>
        /// 卡片缩略图的解码宽度。卡片逻辑尺寸是 280x200，按当前屏幕的缩放比例
        /// 换算成物理像素就够 —— 100% DPI 下 280 足矣，没必要一律按 560 解，
        /// 那是为 200% DPI 留的余量，在普通屏上等于白解 4 倍像素。
        /// </summary>
        private int GetCardDecodeWidth()
        {
            try
            {
                var scale = WallpaperGridView.XamlRoot?.RasterizationScale ?? 1.0;
                return (int)Math.Clamp(280 * scale, 280, App1.Config.AppConfig.ThumbnailWidth);
            }
            catch
            {
                return 400;
            }
        }

        /// <summary>
        /// 按卡片尺寸加载缩略图。Win10 走本地缓存，Win11 直接走 HTTPS。
        /// </summary>
        private async Task LoadCardImageAsync(Microsoft.UI.Xaml.Controls.Image image)
        {
            if (image.DataContext is not Wallpaper wallpaper)
            {
                // 容器被回收、数据已清空：把位图放掉，
                // 否则这张已经滚出屏幕的卡片还在替它占着解码出来的内存
                image.Source = null;

                if (image.Tag is CardImageState recycled)
                {
                    recycled.LoadedUrl = null;
                }

                return;
            }

            try
            {
                var imageUrl = wallpaper.ThumbUrl;  // 多图模式使用缩略图
                var decodeWidth = GetCardDecodeWidth();

                // Loaded 和 DataContextChanged 常常会连着来一次，同一张图没必要解两遍
                if (image.Tag is CardImageState state)
                {
                    if (state.LoadedUrl == imageUrl && image.Source != null)
                    {
                        return;
                    }

                    state.LoadedUrl = imageUrl;
                }

                // Win11: 直接加载 HTTPS URL（按卡片尺寸解码，不按原图）
                if (_isWindows11)
                {
                    TryLoadImageFromUrl(image, imageUrl, wallpaper.Title, decodeWidth);
                }
                // Win10: 下载到本地再加载
                else
                {
                    if (!string.IsNullOrEmpty(wallpaper.LocalPath) && System.IO.File.Exists(wallpaper.LocalPath))
                    {
                        LoadImageFromLocalPath(image, wallpaper.LocalPath, wallpaper.Title, decodeWidth);
                    }
                    else
                    {
                        var localPath = await DownloadImageToLocalAsync(imageUrl, wallpaper.Title);

                        if (!string.IsNullOrEmpty(localPath) && System.IO.File.Exists(localPath))
                        {
                            wallpaper.LocalPath = localPath;
                            LoadImageFromLocalPath(image, localPath, wallpaper.Title, decodeWidth);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[多图模式] 图片加载失败: {wallpaper.Title} | {ex.Message}");
            }
        }
        
        /// <summary>
        /// 多图模式 - 图片加载完成
        /// </summary>
        private void GridImage_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is Microsoft.UI.Xaml.Controls.Image)
            {
                System.Diagnostics.Debug.WriteLine("[多图模式] ✅ 图片显示成功");
            }
        }

        /// <summary>
        /// 给卡片图片加圆角裁切。Border 的 CornerRadius 只管背景和边框，
        /// 不裁子元素，图片放进去四角是直角顶出来的。用合成层的圆角几何裁剪，
        /// 边缘走 GPU 抗锯齿，比单纯靠 Border 干净得多。
        /// </summary>
        private static void ApplyCardRoundedClip(FrameworkElement element, float radius)
        {
            try
            {
                var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(element);
                var compositor = visual.Compositor;

                void Update(float width, float height)
                {
                    if (width <= 0 || height <= 0)
                    {
                        return;
                    }

                    var geometry = compositor.CreateRoundedRectangleGeometry();
                    geometry.Size = new System.Numerics.Vector2(width, height);
                    geometry.CornerRadius = new System.Numerics.Vector2(radius, radius);
                    visual.Clip = compositor.CreateGeometricClip(geometry);
                }

                if (element.ActualWidth > 0 && element.ActualHeight > 0)
                {
                    Update((float)element.ActualWidth, (float)element.ActualHeight);
                }

                element.SizeChanged += (_, args) =>
                    Update((float)args.NewSize.Width, (float)args.NewSize.Height);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[多图模式] 圆角裁切失败: {ex.Message}");
            }
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
            _isDesktopTimerEnabled = false;
            _appSettings.IsDesktopTimerEnabled = false;

            SaveAppSettings();
            (Application.Current as App)?.ApplyAutoWallpaperSettings(_appSettings);

            System.Diagnostics.Debug.WriteLine("[定时器] 桌面定时已关闭");
            (Application.Current as App)?.ShowTrayNotification("小K壁纸", "桌面壁纸定时切换已关闭");
        }

        /// <summary>
        /// 停止锁屏定时器
        /// </summary>
        private void StopLockScreenTimer()
        {
            _isLockScreenTimerEnabled = false;
            _appSettings.IsLockScreenTimerEnabled = false;

            SaveAppSettings();
            (Application.Current as App)?.ApplyAutoWallpaperSettings(_appSettings);

            System.Diagnostics.Debug.WriteLine("[定时器] 锁屏定时已关闭");
            (Application.Current as App)?.ShowTrayNotification("小K壁纸", "锁屏壁纸定时切换已关闭");
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
        private void StartWallpaperTimer(bool isDesktop, double intervalMinutes, bool saveSettings = true)
        {
            if (isDesktop)
            {
                _isDesktopTimerEnabled = true;
                _appSettings.IsDesktopTimerEnabled = true;
                _appSettings.DesktopTimerInterval = intervalMinutes;
            }
            else
            {
                _isLockScreenTimerEnabled = true;
                _appSettings.IsLockScreenTimerEnabled = true;
                _appSettings.LockScreenTimerInterval = intervalMinutes;
            }

            if (saveSettings)
            {
                SaveAppSettings();
            }

            // 真正排期的是 App 层的 AutoWallpaperService（System.Threading.Timer）。
            // 它不依赖 UI，主窗口关掉销毁之后照样按点换，这里只负责把新间隔告诉它。
            (Application.Current as App)?.ApplyAutoWallpaperSettings(_appSettings);

            UpdateTimerStatusIndicator();
            System.Diagnostics.Debug.WriteLine($"[定时器] {(isDesktop ? "桌面" : "锁屏")}定时已交给后台，间隔 {intervalMinutes} 分钟");
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

                // StartupRegistrar 抛的都是给用户看的说明（比如打包版被系统禁用，
                // 需要去「设置 → 应用 → 启动」手动开），直接显示比"设置失败"有用。
                ShowToast(
                    ex is InvalidOperationException ? ex.Message : "开机启动设置失败",
                    isSuccess: false);
            }
        }

        /// <summary>
        /// 右键菜单 - 任务栏外观档位（关闭 / 透明 / 亚克力）。
        /// 服务在 App 层，关掉窗口之后仍然生效。
        /// </summary>
        private async void SetTaskbarEffect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is not FrameworkElement element || element.Tag is not string tag)
                {
                    return;
                }

                if (!int.TryParse(tag, out var mode))
                {
                    return;
                }

                var effect = (TaskbarEffect)mode;
                var service = (Application.Current as App)?.TaskbarAppearance;
                if (service == null)
                {
                    ShowToast("任务栏服务未初始化", isSuccess: false);
                    return;
                }

                // 注入最长会等十几秒，扔到线程池上，别把界面卡住
                var ok = await Task.Run(() => service.Apply(effect));

                _appSettings.TaskbarEffect = mode;
                _appSettings.IsTaskbarBlurEnabled = effect != TaskbarEffect.Off;
                SettingsManager.SaveSettings(_appSettings);

                if (effect == TaskbarEffect.Off)
                {
                    ShowToast(ok ? "已恢复任务栏默认外观" : (service.LastError ?? "恢复失败"), ok);
                    return;
                }

                if (!ok)
                {
                    ShowToast(service.LastError ?? "任务栏透明设置失败", isSuccess: false);
                    return;
                }

                // 注入成功不等于画上了：还要看 DLL 有没有在 explorer 里找到任务栏背景。
                // 给它几百毫秒登记完再回读。
                await Task.Delay(900);

                var applied = service.AppliedCount;
                if (applied > 0)
                {
                    ShowToast(effect == TaskbarEffect.Clear ? "已开启任务栏透明" : "已开启任务栏亚克力", isSuccess: true);
                }
                else
                {
                    ShowToast("已注入资源管理器，但没找到任务栏背景（详见 tap.log）", isSuccess: false);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[任务栏] 切换失败: {ex.Message}");
                ShowToast("任务栏外观设置失败", isSuccess: false);
            }
        }

        private Flyout? _taskbarTintFlyout;
        private Slider? _taskbarTintSlider;
        private TextBlock? _taskbarTintValueText;
        private TextBlock? _taskbarTintHintText;
        private bool _taskbarTintSyncing;

        /// <summary>
        /// 菜单「调节浓度…」：弹一个小浮层，里面一个滑块。
        ///
        /// 拖动只改共享内存里的 alpha 再敲一下事件，DLL 那边直接换画刷，
        /// 不重新注入、不碰 explorer，所以可以边拖边看。
        /// </summary>
        private void AdjustTaskbarTint_Click(object sender, RoutedEventArgs e)
        {
            var service = (Application.Current as App)?.TaskbarAppearance;
            if (service == null)
            {
                ShowToast("任务栏服务未初始化", isSuccess: false);
                return;
            }

            if (service.Current == TaskbarEffect.Off)
            {
                ShowToast("先选「透明」或「亚克力」，再调浓度", isSuccess: false);
                return;
            }

            EnsureTaskbarTintFlyout();

            // 把当前档位的浓度同步到滑块上（这一步不要触发应用）
            _taskbarTintSyncing = true;
            _taskbarTintSlider!.Value = Math.Round(service.CurrentTintAlpha * 100.0 / 255.0);
            _taskbarTintSyncing = false;

            _taskbarTintValueText!.Text = $"浓度：{(int)_taskbarTintSlider.Value}%";
            _taskbarTintHintText!.Text = service.Current == TaskbarEffect.Clear
                ? "透明模式：0 为完全透明，数值越大越接近不透明的黑。"
                : "亚克力模式：数值越大，磨砂上的黑色着色越重；0 就是纯磨砂。";

            // 菜单项点击后菜单会自己关，light-dismiss 有可能顺手把刚弹的浮层一起关掉。
            // 投递到消息队列，等菜单收完再弹。
            DispatcherQueue.TryEnqueue(() =>
            {
                _taskbarTintFlyout!.ShowAt(WindowRoot, new FlyoutShowOptions
                {
                    Placement = FlyoutPlacementMode.Bottom,
                    Position = new Windows.Foundation.Point(
                        Math.Max(24, WindowRoot.ActualWidth / 2 - 150),
                        Math.Max(80, WindowRoot.ActualHeight / 2 - 80)),
                });
            });
        }

        private void EnsureTaskbarTintFlyout()
        {
            if (_taskbarTintFlyout != null)
            {
                return;
            }

            _taskbarTintValueText = new TextBlock { FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };

            _taskbarTintSlider = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                StepFrequency = 1,
                Width = 280,
                Margin = new Thickness(0, 6, 0, 2),
            };
            _taskbarTintSlider.ValueChanged += TaskbarTintSlider_ValueChanged;

            _taskbarTintHintText = new TextBlock
            {
                FontSize = 12,
                Opacity = 0.72,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 280,
            };

            var panel = new StackPanel
            {
                Padding = new Thickness(18, 14, 18, 14),
                Spacing = 2,
            };
            panel.Children.Add(new TextBlock
            {
                Text = "任务栏浓度",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            panel.Children.Add(_taskbarTintValueText);
            panel.Children.Add(_taskbarTintSlider);
            panel.Children.Add(_taskbarTintHintText);

            _taskbarTintFlyout = new Flyout { Content = panel };
            _taskbarTintFlyout.Closed += (_, _) => SaveTaskbarTint();
        }

        private void TaskbarTintSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_taskbarTintValueText != null)
            {
                _taskbarTintValueText.Text = $"浓度：{(int)Math.Round(e.NewValue)}%";
            }

            if (_taskbarTintSyncing)
            {
                return;
            }

            var service = (Application.Current as App)?.TaskbarAppearance;
            service?.SetTintAlpha((int)Math.Round(e.NewValue * 255.0 / 100.0));
        }

        private void SaveTaskbarTint()
        {
            var service = (Application.Current as App)?.TaskbarAppearance;
            if (service == null)
            {
                return;
            }

            _appSettings.TaskbarClearAlpha = service.ClearAlpha;
            _appSettings.TaskbarAcrylicAlpha = service.AcrylicAlpha;
            SettingsManager.SaveSettings(_appSettings);
        }

        /// <summary>
        /// 启用开机启动
        /// </summary>
        private void EnableStartup()
        {
            // 统一走 StartupRegistrar：托盘菜单和这里必须写同一个键、同一个参数，
            // 否则两边看到的开机启动状态会不一致。
            StartupRegistrar.Enable();
        }

        /// <summary>
        /// 禁用开机启动
        /// </summary>
        private void DisableStartup()
        {
            StartupRegistrar.Disable();
        }

        /// <summary>
        /// 检查开机启动是否启用
        /// </summary>
        private bool IsStartupEnabled()
        {
            return StartupRegistrar.IsEnabled();
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
                System.Diagnostics.Debug.WriteLine("[UI] 右键菜单打开事件触发");
                
                if (sender is MenuFlyout menuFlyout)
                {
                    // 更新开机自启菜单项状态
                    UpdateStartupMenuItemStatus(menuFlyout);

                    // 更新任务栏透明菜单项状态
                    UpdateTaskbarBlurMenuItemStatus(menuFlyout);

                    // 更新定时切换菜单项状态
                    UpdateTimerMenuItemStatus(menuFlyout);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UI] 右键菜单打开失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新开机自启菜单项状态（无参数重载）
        /// </summary>
        private void UpdateStartupMenuItemStatus()
        {
            // 当没有菜单引用时，不做任何操作
            // 此方法用于在设置更改后被调用，但此时菜单可能未打开
            System.Diagnostics.Debug.WriteLine("[UI] 调用无参数的UpdateStartupMenuItemStatus");
        }

        /// <summary>
        /// 更新开机自启菜单项状态
        /// </summary>
        private void UpdateStartupMenuItemStatus(MenuFlyout menuFlyout)
        {
            try
            {
                bool isEnabled = IsStartupEnabled();
                
                // 遍历菜单项找到开机自启菜单项
                foreach (var item in menuFlyout.Items)
                {
                    if (item is MenuFlyoutItem menuItem && menuItem.Text.Contains("开机启动"))
                    {
                        menuItem.Text = isEnabled ? "开机启动 ✓" : "开机启动";
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UI] 更新开机自启菜单项失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新定时切换菜单项状态
        /// </summary>
        private void UpdateTimerMenuItemStatus(MenuFlyout menuFlyout)
        {
            try
            {
                // 获取当前的定时设置
                double desktopInterval = _appSettings.DesktopTimerInterval;
                double lockScreenInterval = _appSettings.LockScreenTimerInterval;
                bool isDesktopEnabled = _appSettings.IsDesktopTimerEnabled;
                bool isLockScreenEnabled = _appSettings.IsLockScreenTimerEnabled;
                
                System.Diagnostics.Debug.WriteLine($"[定时设置] 桌面: {desktopInterval}分钟, 锁屏: {lockScreenInterval}分钟");
                
                // 遍历菜单项找到定时切换菜单项
                foreach (var item in menuFlyout.Items)
                {
                    if (item is MenuFlyoutSubItem subItem)
                    {
                        // 处理桌面背景定时切换
                        if (subItem.Text.Contains("桌面背景定时切换"))
                        {
                            UpdateTimerSubMenuItem(subItem, desktopInterval, isDesktopEnabled);
                        }
                        // 处理锁屏界面定时切换
                        else if (subItem.Text.Contains("锁屏界面定时切换"))
                        {
                            UpdateTimerSubMenuItem(subItem, lockScreenInterval, isLockScreenEnabled);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UI] 更新定时切换菜单项失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新定时切换子菜单项状态
        /// </summary>
        private void UpdateTimerSubMenuItem(MenuFlyoutSubItem subItem, double interval, bool isEnabled)
        {
            try
            {
                foreach (var item in subItem.Items)
                {
                    if (item is MenuFlyoutItem menuItem)
                    {
                        // 移除所有的对勾
                        menuItem.Text = menuItem.Text.Replace(" ✓", "");
                        
                        // 根据当前设置添加对勾
                        if (menuItem.Tag != null)
                        {
                            string tag = menuItem.Tag.ToString();
                            if (tag == "0" && !isEnabled)
                            {
                                menuItem.Text += " ✓";
                            }
                            else if (double.TryParse(tag, out double tagInterval) && tagInterval == interval && isEnabled)
                            {
                                menuItem.Text += " ✓";
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UI] 更新定时子菜单项失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新任务栏外观菜单项的勾选状态。
        /// 三个档位是 RadioMenuFlyoutItem，同一个 GroupName 里只会有一个被选中。
        /// </summary>
        private void UpdateTaskbarBlurMenuItemStatus(MenuFlyout menuFlyout)
        {
            try
            {
                var current = ((Application.Current as App)?.TaskbarAppearance?.Current
                               ?? TaskbarEffect.Off).ToString();

                SyncTaskbarRadioItems(menuFlyout.Items, current);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UI] 更新任务栏外观菜单项失败: {ex.Message}");
            }
        }

        private static void SyncTaskbarRadioItems(IList<MenuFlyoutItemBase> items, string current)
        {
            foreach (var item in items)
            {
                if (item is Microsoft.UI.Xaml.Controls.MenuFlyoutSubItem subItem)
                {
                    SyncTaskbarRadioItems(subItem.Items, current);
                    continue;
                }

                if (item is RadioMenuFlyoutItem radio &&
                    radio.Tag is string tag &&
                    radio.GroupName == "TaskbarEffect")
                {
                    radio.IsChecked = string.Equals(tag, current, StringComparison.Ordinal);
                }
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
            HideFullScreenImage();
        }

        /// <summary>
        /// 关闭全屏预览。先淡出，再真正收起并把图片引用断开 ——
        /// 一张 4K 预览图解码后是几十 MB，光隐藏控件不会释放。
        /// </summary>
        private void HideFullScreenImage()
        {
            try
            {
                if (FullScreenImageViewer.Visibility != Visibility.Visible)
                {
                    return;
                }

                // 先断引用，后台正在下载的高清回来后就换不上去了
                _fullScreenWallpaper = null;
                var closingVersion = _previewVersion;

                var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview
                    .GetElementVisual(FullScreenImageViewer);
                var compositor = visual.Compositor;

                var fade = compositor.CreateScalarKeyFrameAnimation();
                fade.InsertKeyFrame(0f, 1f);
                fade.InsertKeyFrame(1f, 0f);
                fade.Duration = TimeSpan.FromMilliseconds(140);
                visual.StartAnimation("Opacity", fade);

                _ = CollapseAfterFadeAsync(closingVersion);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UI] 关闭全屏预览失败: {ex.Message}");
                FullScreenImageViewer.Visibility = Visibility.Collapsed;
            }
        }

        private async Task CollapseAfterFadeAsync(int closingVersion)
        {
            await Task.Delay(150);

            // 这中间用户又点开了另一张，不能把新的预览收掉
            if (closingVersion != _previewVersion)
            {
                return;
            }

            FullScreenImageViewer.Visibility = Visibility.Collapsed;
            FullScreenImageLow.Source = null;
            FullScreenImageHigh.Source = null;

            // 这里不再对已经脱离可视树的元素调 GetElementVisual。
            // 下次打开时会在挂载之后重置一次 Opacity，没必要在这儿碰它。
        }

        /// <summary>
        /// 下载图片事件
        /// </summary>
        private async void DownloadImage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_currentContextWallpaper == null)
                {
                    ShowToast("请先选择要下载的图片", isSuccess: false);
                    return;
                }

                var downloadPath = await SaveWallpaperToDownloadFolderAsync(_currentContextWallpaper);
                if (!string.IsNullOrEmpty(downloadPath))
                {
                    ShowToast($"下载成功：{downloadPath}", isSuccess: true, durationMs: 4000);
                    System.Diagnostics.Debug.WriteLine($"[下载] 下载成功: {downloadPath}");
                    return;
                }

                ShowToast("下载失败", isSuccess: false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[下载] 下载图片失败: {ex.Message}");
                ShowToast("下载失败", isSuccess: false);
            }
        }

        private async Task<string> SaveWallpaperToDownloadFolderAsync(Wallpaper wallpaper)
        {
            var imageUrl = !string.IsNullOrWhiteSpace(wallpaper.FullUrl) ? wallpaper.FullUrl : wallpaper.ThumbUrl;
            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                return string.Empty;
            }

            var picturesFolder = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyPictures);
            var downloadFolder = System.IO.Path.Combine(picturesFolder, "小K壁纸", "下载");
            if (!System.IO.Directory.Exists(downloadFolder))
            {
                System.IO.Directory.CreateDirectory(downloadFolder);
            }

            var invalidChars = System.IO.Path.GetInvalidFileNameChars();
            var baseNameChars = (wallpaper.Title ?? "wallpaper")
                .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
                .ToArray();
            var baseName = new string(baseNameChars).Trim();
            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = "wallpaper";
            }
            if (baseName.Length > 60)
            {
                baseName = baseName.Substring(0, 60);
            }

            var extension = ".jpg";
            if (Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
            {
                var pathExtension = System.IO.Path.GetExtension(uri.AbsolutePath);
                if (!string.IsNullOrWhiteSpace(pathExtension) && pathExtension.Length <= 5)
                {
                    extension = pathExtension;
                }
            }

            var fileName = $"{baseName}_{DateTime.Now:yyyyMMdd_HHmmss}{extension}";
            var savePath = System.IO.Path.Combine(downloadFolder, fileName);

            if (!string.IsNullOrWhiteSpace(wallpaper.LocalPath) && System.IO.File.Exists(wallpaper.LocalPath))
            {
                System.IO.File.Copy(wallpaper.LocalPath, savePath, overwrite: false);
                return savePath;
            }

            var cachedPath = await DownloadImageToLocalAsync(imageUrl, wallpaper.Title ?? "wallpaper");
            if (!string.IsNullOrWhiteSpace(cachedPath) && System.IO.File.Exists(cachedPath))
            {
                wallpaper.LocalPath = cachedPath;
                System.IO.File.Copy(cachedPath, savePath, overwrite: false);
                return savePath;
            }

            var httpClient = App1.Services.Network.HttpClientProvider.Images;

            var imageBytes = await httpClient.GetByteArrayAsync(imageUrl);
            await System.IO.File.WriteAllBytesAsync(savePath, imageBytes);
            return savePath;
        }

        /// <summary>
        /// 清理缓存事件
        /// </summary>
        private void ClearCache_Click(object sender, RoutedEventArgs e)
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
        /// 随机选择API并获取新的随机图片
        /// </summary>
        private async Task<Wallpaper?> GetRandomWallpaperFromApiAsync()
        {
            try
            {
                var random = new Random();
                var apiChoice = random.Next(2); // 0: Pexels, 1: Bing
                List<Wallpaper> wallpapers = new List<Wallpaper>();

                switch (apiChoice)
                {
                    case 0:
                        // 从Pexels获取随机图片
                        System.Diagnostics.Debug.WriteLine("[定时器] 从Pexels API获取随机壁纸");
                        wallpapers = await _pexelsApi.GetCuratedWallpapersAsync(30);
                        break;
                    case 1:
                        // 从Bing获取随机图片
                        System.Diagnostics.Debug.WriteLine("[定时器] 从Bing API获取随机壁纸");
                        wallpapers = await _bingApi.GetDailyWallpapersAsync(5);
                        break;
                }

                if (wallpapers.Count > 0)
                {
                    // 从获取的壁纸中随机选择一张
                    var selectedWallpaper = wallpapers[random.Next(wallpapers.Count)];
                    System.Diagnostics.Debug.WriteLine($"[定时器] 选择壁纸: {selectedWallpaper.Title}");
                    return selectedWallpaper;
                }

                // 如果API获取失败，回退到本地缓存
                System.Diagnostics.Debug.WriteLine("[定时器] API获取失败，回退到本地缓存");
                if (_wallpapers.Count > 0)
                {
                    return _wallpapers[random.Next(_wallpapers.Count)];
                }
                else if (_immersiveWallpapers.Count > 0)
                {
                    return _immersiveWallpapers[random.Next(_immersiveWallpapers.Count)];
                }

                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[定时器] 获取随机壁纸失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 壁纸网格项点击事件
        /// </summary>
        private void WallpaperGridItem_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            // 标记已处理：不标记的话同一次点击会继续冒泡到 GridView，
            // 被外层再识别一轮，双击时容易和下面的 DoubleTapped 撞车。
            e.Handled = true;

            // 预览已经打开时，这一下点击归预览层自己处理（关闭），
            // 不能再启动一次"打开预览"，否则连点两下会疯狂开关。
            if (FullScreenImageViewer.Visibility == Visibility.Visible)
            {
                return;
            }

            if (sender is not FrameworkElement element || element.DataContext is not Wallpaper wallpaper)
            {
                return;
            }

            // 双击设置壁纸时，系统会先抛两次 Tapped。这里先等一小会儿再开预览，
            // 如果期间发生了双击，就把这次单击吞掉 —— 否则双击会先闪一下预览再关掉。
            _cardTapCts?.Cancel();
            _cardTapCts = new System.Threading.CancellationTokenSource();
            var token = _cardTapCts.Token;

            App1.Services.Diagnostics.UiTrace.Write("[tap] 卡片点击，进入 220ms 等待");
            _ = OpenPreviewAfterTapAsync(wallpaper, token);
        }

        private async Task OpenPreviewAfterTapAsync(Wallpaper wallpaper, System.Threading.CancellationToken token)
        {
            try
            {
                await Task.Delay(220, token);
            }
            catch (OperationCanceledException)
            {
                App1.Services.Diagnostics.UiTrace.Write("[tap] 取消（判定为双击）");
                return;   // 双击了，不弹预览
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            // 这 220ms 里用户可能已经又点了一下把预览关掉，或者系统补了一个
            // DoubleTapped。重复打开会让 _previewVersion 来回跳，状态就错位了。
            if (FullScreenImageViewer.Visibility == Visibility.Visible)
            {
                return;
            }

            App1.Services.Diagnostics.UiTrace.Write("[tap] 等待结束，准备打开预览");
            ShowFullScreenImage(wallpaper);
        }

        /// <summary>
        /// 单击卡片 → 全屏查看。
        /// 缩略图先把画面铺住，高清在后台下载完再淡入盖上去 —— 点下去立刻有画面，
        /// 又不会出现从糊到清晰的硬切。
        /// </summary>
        private void ShowFullScreenImage(Wallpaper wallpaper)
        {
            try
            {
                App1.Services.Diagnostics.UiTrace.Write("[preview] 开始");

                _fullScreenWallpaper = wallpaper;
                _previewVersion++;

                FullScreenTitle.Text = wallpaper.Title;
                FullScreenCopyright.Text = wallpaper.Copyright;

                // 顺序很关键：先把预览层挂进可视树，再碰它的子元素。
                // 原来是对还处于 Collapsed 状态的 FullScreenImageHigh 调
                // GetElementVisual —— 元素没进可视树时拿到的是一个游离 visual，
                // 紧接着 Visibility 变可见、visual 被重新插入，合成器里的旧状态
                // 会让渲染线程卡住，表现就是"点一下整个窗口不动了"。
                App1.Services.Diagnostics.UiTrace.Write("[preview] 即将设为可见");
                FullScreenImageViewer.Visibility = Visibility.Visible;
                App1.Services.Diagnostics.UiTrace.Write("[preview] 层已可见");

                FullScreenImageLow.Source = null;
                FullScreenImageHigh.Source = null;
                Microsoft.UI.Xaml.Hosting.ElementCompositionPreview
                    .GetElementVisual(FullScreenImageHigh).Opacity = 0f;
                App1.Services.Diagnostics.UiTrace.Write("[preview] 清空旧图、重置高清层");

                if (!string.IsNullOrEmpty(wallpaper.ThumbUrl))
                {
                    // 这一层只求"点下去立刻有画面"，按 1280 宽解码足够铺满屏幕。
                    // 原来传 0 = 不限制解码尺寸，而 Upx8 / Dmoe 这类源的 ThumbUrl
                    // 本身就是 4K 原图，单张位图就要 33MB，解码直接压住 UI 线程。
                    FullScreenImageLow.Source = CreateBitmap(wallpaper.ThumbUrl, 1280);
                }
                App1.Services.Diagnostics.UiTrace.Write("[preview] 缩略图源已设置");

                PlayOpenAnimation();
                App1.Services.Diagnostics.UiTrace.Write("[preview] 开场动画已启动");

                var hiResUrl = !string.IsNullOrEmpty(wallpaper.FullUrl) ? wallpaper.FullUrl : wallpaper.ThumbUrl;

                // Upx8 这类源的缩略图就是原图，不用再加载一遍
                if (!string.IsNullOrEmpty(hiResUrl) && hiResUrl != wallpaper.ThumbUrl)
                {
                    _ = UpgradeFullScreenImageAsync(hiResUrl, wallpaper);
                }

                App1.Services.Diagnostics.UiTrace.Write("[preview] 完成");
            }
            catch (Exception ex)
            {
                App1.Services.Diagnostics.UiTrace.Write($"[preview] 异常: {ex.GetType().Name} / {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[UI] 打开全屏预览失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 打开动画：整层淡入，图片从 0.86 回到原尺寸。
        /// 全部走合成线程，展开过程中 UI 线程还能继续干活，不会掉帧。
        /// </summary>
        private void PlayOpenAnimation()
        {
            var viewer = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview
                .GetElementVisual(FullScreenImageViewer);
            var compositor = viewer.Compositor;

            var easeOut = compositor.CreateCubicBezierEasingFunction(
                new System.Numerics.Vector2(0.16f, 1f),
                new System.Numerics.Vector2(0.3f, 1f));

            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(1f, 1f, easeOut);
            fade.Duration = TimeSpan.FromMilliseconds(180);
            viewer.StartAnimation("Opacity", fade);

            var host = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview
                .GetElementVisual(FullScreenImageHost);

            // 缩放原点取 host 自己的尺寸。它由图片撑开，合成层可以直接读，
            // 不需要为了拿 ActualWidth 去调 UpdateLayout() ——
            // 那个调用会同步重排整棵树（GridView 里几百张卡片也在其中），
            // 点一下就能把 UI 线程按住好几百毫秒，看着就是"点开就卡死"。
            var hostSize = host.Size;
            if (hostSize.X > 0 && hostSize.Y > 0)
            {
                host.CenterPoint = new System.Numerics.Vector3(hostSize.X / 2f, hostSize.Y / 2f, 0f);

                var scale = compositor.CreateVector3KeyFrameAnimation();
                scale.InsertKeyFrame(0f, new System.Numerics.Vector3(0.86f, 0.86f, 1f));
                scale.InsertKeyFrame(1f, System.Numerics.Vector3.One, easeOut);
                scale.Duration = TimeSpan.FromMilliseconds(260);
                host.StartAnimation("Scale", scale);
            }
            // 尺寸还没算出来时只做整层淡入，不要为了一个 0.86 → 1 的缩放去强制布局
        }

        /// <summary>
        /// 后台加载高清版并淡入。
        ///
        /// 这里刻意不用 BitmapImage.UriSource 直接指网络地址：那条路走的是系统
        /// 图片栈（跟着系统代理和 UA 走），不少图床会直接拒掉，结果就是点开之后
        /// 永远停在缩略图上。改成用应用自己的 HttpClient 直连下载再解码，链路可控，
        /// 失败也能明确记下来。
        /// </summary>
        private async Task UpgradeFullScreenImageAsync(string url, Wallpaper wallpaper)
        {
            var version = _previewVersion;

            byte[]? bytes = null;
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
                bytes = await Services.Network.HttpClientProvider.Images.GetByteArrayAsync(url, cts.Token);

                System.Diagnostics.Debug.WriteLine($"[全屏预览] 高清下载完成: {bytes.Length / 1024} KB / {url}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[全屏预览] 高清下载失败: {ex.Message} / {url}");
                return;
            }

            if (bytes.Length == 0
                || version != _previewVersion
                || !ReferenceEquals(_fullScreenWallpaper, wallpaper))
            {
                return;
            }

            try
            {
                var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                using (var writer = new Windows.Storage.Streams.DataWriter(stream))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    writer.DetachStream();
                }
                stream.Seek(0);

                var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage
                {
                    DecodePixelType = Microsoft.UI.Xaml.Media.Imaging.DecodePixelType.Physical,
                    DecodePixelWidth = 2048
                };

                await bitmap.SetSourceAsync(stream);

                // 解码期间用户可能已经关掉、或者点了另一张
                if (version != _previewVersion || !ReferenceEquals(_fullScreenWallpaper, wallpaper))
                {
                    return;
                }

                FullScreenImageHigh.Source = bitmap;

                // 从缩略图淡入到高清，避免硬切
                var highVisual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview
                    .GetElementVisual(FullScreenImageHigh);
                var compositor = highVisual.Compositor;

                var fade = compositor.CreateScalarKeyFrameAnimation();
                fade.InsertKeyFrame(0f, 0f);
                fade.InsertKeyFrame(1f, 1f);
                fade.Duration = TimeSpan.FromMilliseconds(240);
                highVisual.StartAnimation("Opacity", fade);

                System.Diagnostics.Debug.WriteLine("[全屏预览] ✅ 已切到高清图");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[全屏预览] 高清解码失败: {ex.Message}");
            }
        }

        private static Microsoft.UI.Xaml.Media.Imaging.BitmapImage? CreateBitmap(string url, int decodePixelWidth)
        {
            try
            {
                var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();

                if (decodePixelWidth > 0)
                {
                    bitmap.DecodePixelType = Microsoft.UI.Xaml.Media.Imaging.DecodePixelType.Physical;
                    bitmap.DecodePixelWidth = decodePixelWidth;
                }

                bitmap.UriSource = new Uri(url);
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 卡片悬停缩放。用合成线程的隐式动画：这里只改一次 Scale 值，
        /// 补间全部由合成器负责，鼠标来回扫也不占 UI 线程，所以是"跟手"而不是"追手"。
        /// 每张卡片的动画对象只装一次，重复装会反复重建。 
        /// </summary>
        private void AnimateCardScale(object sender, double target, int zIndex)
        {
            if (sender is not FrameworkElement element)
            {
                return;
            }

            Canvas.SetZIndex(element, zIndex);

            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(element);

            if (visual.ImplicitAnimations == null)
            {
                var compositor = visual.Compositor;

                // 缩放原点取卡片中心，否则会从左上角往外涨
                visual.CenterPoint = new System.Numerics.Vector3(
                    (float)element.ActualWidth / 2f,
                    (float)element.ActualHeight / 2f,
                    0f);

                var expand = compositor.CreateVector3KeyFrameAnimation();
                expand.Target = "Scale";
                expand.InsertExpressionKeyFrame(
                    1f,
                    "this.FinalValue",
                    compositor.CreateCubicBezierEasingFunction(
                        new System.Numerics.Vector2(0.16f, 1f),
                        new System.Numerics.Vector2(0.3f, 1f)));
                expand.Duration = TimeSpan.FromMilliseconds(180);
                expand.StopBehavior = Microsoft.UI.Composition.AnimationStopBehavior.SetToFinalValue;

                var implicitAnimations = compositor.CreateImplicitAnimationCollection();
                implicitAnimations["Scale"] = expand;
                visual.ImplicitAnimations = implicitAnimations;
            }

            visual.Scale = new System.Numerics.Vector3((float)target, (float)target, 1f);
        }

        /// <summary>
        /// 壁纸网格项鼠标进入事件
        /// </summary>
        private void WallpaperGridItem_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            AnimateCardScale(sender, 1.04, 10);
        }

        /// <summary>
        /// 壁纸网格项鼠标离开事件
        /// </summary>
        private void WallpaperGridItem_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            AnimateCardScale(sender, 1.0, 0);
        }

        #endregion
    }
}
