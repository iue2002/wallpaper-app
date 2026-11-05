using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using App1.Models;

namespace App1.Services.Api
{
    /// <summary>
    /// Bing每日壁纸API服务（支持多地区）
    /// </summary>
    public class BingApiService
    {
        private readonly HttpClient _httpClient;
        
        // Bing壁纸支持的地区市场代码
        private readonly string[] _markets = new[]
        {
            "zh-CN", // 中国
            "en-US", // 美国
            "ja-JP", // 日本
            "en-GB", // 英国
            "de-DE", // 德国
            "fr-FR", // 法国
            "en-AU", // 澳大利亚
            "en-CA", // 加拿大
            "es-ES", // 西班牙
            "it-IT", // 意大利
            "pt-BR", // 巴西
            "en-IN"  // 印度
        };
        
        public BingApiService()
        {
            // 配置 HttpClientHandler 禁用代理
            var handler = new HttpClientHandler
            {
                UseProxy = false,
                Proxy = null
            };
            
            _httpClient = new HttpClient(handler)
            {
                // 单个源最多等 8 秒，避免一个慢源把整页加载拖到 30 秒
                Timeout = TimeSpan.FromSeconds(8)
            };
        }
        
        /// <summary>
        /// 获取Bing随机历史壁纸（UHD超高清）
        /// </summary>
        public async Task<List<Wallpaper>> GetDailyWallpapersAsync(int count = 10)
        {
            var wallpapers = new List<Wallpaper>();
            
            try
            {
                System.Diagnostics.Debug.WriteLine($"[BingAPI] 开始获取Bing随机历史壁纸（UHD超高清）...");
                
                // 官方接口每段最多 8 天，取两段覆盖约 15 天（第三方 bing.img.run 已不可用，30 秒超时×N 是卡顿主因）
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var idx in new[] { 0, 7 })
                {
                    var batch = await GetWallpapersByMarketAsync("zh-CN", 8, idx);
                    foreach (var item in batch)
                    {
                        if (seen.Add(item.FullUrl))
                        {
                            wallpapers.Add(item);
                        }
                    }
                }

                if (wallpapers.Count > count)
                {
                    wallpapers = wallpapers.OrderBy(_ => Random.Shared.Next()).Take(count).ToList();
                }
                
                System.Diagnostics.Debug.WriteLine($"[BingAPI] ✅ 共获取 {wallpapers.Count} 张UHD超高清壁纸");
                return wallpapers;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BingAPI] ❌ 获取失败: {ex.Message}");
                return new List<Wallpaper>();
            }
        }
        
        /// <summary>
        /// 获取指定地区的Bing壁纸
        /// </summary>
        private async Task<List<Wallpaper>> GetWallpapersByMarketAsync(string market, int n = 8, int idx = 0)
        {
            try
            {
                // Bing壁纸API（走 cn.bing.com，国内可达性更稳）
                var url = $"https://cn.bing.com/HPImageArchive.aspx?format=js&idx={idx}&n={n}&mkt={market}";
                var response = await _httpClient.GetStringAsync(url);
                
                var json = JsonDocument.Parse(response);
                var images = json.RootElement.GetProperty("images");
                
                var wallpapers = new List<Wallpaper>();
                
                foreach (var image in images.EnumerateArray())
                {
                    try
                    {
                        var urlBase = image.GetProperty("urlbase").GetString();
                        var title = image.GetProperty("title").GetString();
                        var copyright = image.GetProperty("copyright").GetString();
                        
                        if (string.IsNullOrEmpty(urlBase)) continue;
                        
                        // 构建超高清图片URL（UHD = 4K）
                        var fullUrl = $"https://cn.bing.com{urlBase}_UHD.jpg";

                        // 列表缩略图：Bing 提供固定的尺寸档，_400x240 正好对上 280x200 的卡片，
                        // 实测 21 KB，而原来的 _1920x1080 档约 300 KB —— 同样一张图省 15 倍流量。
                        // 注意 480x270 这一档不存在（返回 404），只能用 400x240 或 800x480。
                        var thumbUrl = $"https://cn.bing.com{urlBase}_400x240.jpg";
                        
                        wallpapers.Add(new Wallpaper
                        {
                            Id = Guid.NewGuid().ToString(),
                            Title = title ?? "Bing每日壁纸",
                            Copyright = copyright ?? $"来源: Bing ({market})",
                            Source = WallpaperSource.Bing,
                            OriginalUrl = fullUrl,
                            FullUrl = fullUrl,
                            ThumbUrl = thumbUrl,
                            AddedDate = DateTime.Now
                        });
                    }
                    catch
                    {
                        continue;
                    }
                }
                
                return wallpapers;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BingAPI] 获取 {market} 壁纸失败: {ex.Message}");
                return new List<Wallpaper>();
            }
        }
    }
}
