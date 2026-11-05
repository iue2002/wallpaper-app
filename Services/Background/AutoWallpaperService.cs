using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using App1.Config;
using App1.Models;
using App1.Services.Api;
using App1.Services.Diagnostics;
using App1.Services.WallpaperSetter;

namespace App1.Services.Background
{
    /// <summary>
    /// 后台自动换壁纸。
    ///
    /// 这个类刻意不碰任何 UI：用 System.Threading.Timer 调度，回调跑在线程池线程上，
    /// 换壁纸本身又下沉到 WallpaperService 的独立 STA 线程。所以主窗口关掉、
    /// 整个 XAML 树被销毁之后，它照样按点干活 —— 这是"托盘常驻还能保持超低内存"的前提。
    ///
    /// 原来的定时器是 DispatcherTimer，挂在 UI 线程上，窗口一旦销毁就跟着没了，
    /// 所以以前只能把窗口藏起来、内存一直不降。
    /// </summary>
    public sealed class AutoWallpaperService : IDisposable
    {
        private readonly WallpaperService _wallpaperService = new();
        private readonly BingApiService _bingApi = new();
        private readonly Upx8ApiService _upx8Api = new();
        private readonly PexelsApiService _pexelsApi = new();

        /// <summary>同一时刻只允许一次换壁纸流程，避免定时器连发时几个下载撞在一起。</summary>
        private readonly SemaphoreSlim _changeGate = new(1, 1);

        private Timer? _desktopTimer;
        private Timer? _lockScreenTimer;
        private bool _disposed;

        /// <summary>换壁纸成功后通知一次（主窗口开着时用来刷新状态栏）。</summary>
        public event Action<bool, string>? Changed;

        /// <summary>
        /// 按设置重建两个定时器。设置一改就调这里，不用重启进程。
        /// </summary>
        /// <param name="initialDelaySeconds">
        /// 第一次触发的延迟（秒）。-1 表示等满一个周期再换第一张，跟原来一样。
        /// 开机自启时传一个较小的值：否则开机后要等一小时才看到变化，
        /// 用户会以为自动换壁纸没生效。
        /// </param>
        public void ApplySettings(AppSettings settings, int initialDelaySeconds = -1)
        {
            if (_disposed)
            {
                return;
            }

            Rebuild(ref _desktopTimer, settings.IsDesktopTimerEnabled, settings.DesktopTimerInterval,
                () => ChangeAsync(desktop: true, reason: "定时"), initialDelaySeconds);

            Rebuild(ref _lockScreenTimer, settings.IsLockScreenTimerEnabled, settings.LockScreenTimerInterval,
                () => ChangeAsync(desktop: false, reason: "定时"), initialDelaySeconds);

            AppLog.Write(
                $"[自动换壁纸] 调度已更新 | 桌面={settings.IsDesktopTimerEnabled}/{settings.DesktopTimerInterval}分" +
                $" | 锁屏={settings.IsLockScreenTimerEnabled}/{settings.LockScreenTimerInterval}分");
        }

        private void Rebuild(ref Timer? slot, bool enabled, double minutes, Func<Task> action, int initialDelaySeconds)
        {
            slot?.Dispose();
            slot = null;

            if (!enabled || minutes <= 0)
            {
                return;
            }

            var interval = TimeSpan.FromMinutes(Math.Max(1, minutes));

            // 默认首次也在一个周期之后（和原来 DispatcherTimer 一致）；
            // 开机自启会传一个小延迟，让第一张早些换，不然用户开机后一小时看不到动静。
            var dueTime = initialDelaySeconds > 0
                ? TimeSpan.FromSeconds(initialDelaySeconds)
                : interval;

            slot = new Timer(_ => _ = RunSafelyAsync(action), null, dueTime, interval);
        }

        private static async Task RunSafelyAsync(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                AppLog.Write($"[自动换壁纸] 未处理异常: {ex}");
            }
        }

        /// <summary>
        /// 立即换一张。托盘菜单"立即换一张"和主窗口里的按钮都走这里。
        /// </summary>
        public async Task<bool> ChangeAsync(bool desktop, string reason)
        {
            if (_disposed)
            {
                return false;
            }

            // 上一次还在下载就跳过这次，硬等只会把线程堆起来
            if (!await _changeGate.WaitAsync(TimeSpan.FromSeconds(10)))
            {
                AppLog.Write($"[自动换壁纸] 上一次还没结束，跳过本次（{reason}）");
                return false;
            }

            try
            {
                var wallpaper = await PickWallpaperAsync();
                if (wallpaper == null)
                {
                    AppLog.Write($"[自动换壁纸] 所有源都没拿到可用壁纸（{reason}）");
                    return false;
                }

                var url = !string.IsNullOrEmpty(wallpaper.FullUrl) ? wallpaper.FullUrl : wallpaper.ThumbUrl;
                if (string.IsNullOrEmpty(url))
                {
                    AppLog.Write("[自动换壁纸] 选中的壁纸没有可用地址");
                    return false;
                }

                var ok = desktop
                    ? await _wallpaperService.SetWallpaperAsync(url, wallpaper.Title, wallpaper.LocalPath)
                    : await _wallpaperService.SetLockScreenAsync(url, wallpaper.Title);

                AppLog.Write($"[自动换壁纸] {reason} | 目标={(desktop ? "桌面" : "锁屏")} | 成功={ok} | {wallpaper.Title}");

                if (ok)
                {
                    Changed?.Invoke(desktop, wallpaper.Title);
                }

                return ok;
            }
            catch (Exception ex)
            {
                AppLog.Write($"[自动换壁纸] 失败: {ex.Message}");
                return false;
            }
            finally
            {
                _changeGate.Release();
            }
        }

        /// <summary>
        /// 挑一张。每次随机一个起点，失败就换下一个源，全挂就返回 null。
        /// 只保留这三个源：Bing 最快最稳、Upx8 稳定 4K、Pexels 量最大。
        /// </summary>
        private async Task<Wallpaper?> PickWallpaperAsync()
        {
            var sources = new (string Name, Func<Task<List<Wallpaper>>> Fetch)[]
            {
                ("Bing", () => _bingApi.GetDailyWallpapersAsync(8)),
                ("Upx8", () => _upx8Api.GetWallpapersAsync(3)),
                ("Pexels", () => _pexelsApi.GetCuratedWallpapersAsync(40))
            };

            var start = Random.Shared.Next(sources.Length);

            for (var i = 0; i < sources.Length; i++)
            {
                var source = sources[(start + i) % sources.Length];

                try
                {
                    var list = await source.Fetch();

                    // 拿到列表还不够，必须有一张能真正设成壁纸的地址
                    var usable = list
                        .Where(w => !string.IsNullOrEmpty(w.FullUrl) || !string.IsNullOrEmpty(w.ThumbUrl))
                        .ToList();

                    if (usable.Count == 0)
                    {
                        AppLog.Write($"[自动换壁纸] {source.Name} 返回 {list.Count} 条，但没有可用地址");
                        continue;
                    }

                    var picked = usable[Random.Shared.Next(usable.Count)];
                    AppLog.Write($"[自动换壁纸] 源={source.Name} 候选={usable.Count}");
                    return picked;
                }
                catch (Exception ex)
                {
                    AppLog.Write($"[自动换壁纸] {source.Name} 取图失败: {ex.Message}");
                }
            }

            return null;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _desktopTimer?.Dispose();
            _lockScreenTimer?.Dispose();
            _desktopTimer = null;
            _lockScreenTimer = null;

            try { _pexelsApi.Dispose(); } catch { }
            try { _changeGate.Dispose(); } catch { }
        }
    }
}
