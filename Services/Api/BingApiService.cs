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
                Timeout = TimeSpan.FromSeconds(30)
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
                
                // 使用随机历史壁纸API，每次获取都不同
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        // 获取随机壁纸API（会重定向到Bing真实URL）
                        var randApiUrl = "https://bing.img.run/rand_uhd.php";
                        
                        // 获取302重定向后的真实URL
                        var response = await _httpClient.GetAsync(randApiUrl);
                        if (response.IsSuccessStatusCode)
                        {
                            var redirectUrl = response.RequestMessage?.RequestUri?.ToString() ?? randApiUrl;
                            
                            // 重要：重定向后的URL可能是1920x1080，需要替换为UHD
                            // 原始URL: https://cn.bing.com/th?id=OHR.xxx_1920x1080.jpg
                            // UHD URL: https://cn.bing.com/th?id=OHR.xxx_UHD.jpg
                            var fullUrl = redirectUrl.Replace("_1920x1080.jpg", "_UHD.jpg");
                            var thumbUrl = redirectUrl; // 缩略图使用1920x1080
                            
                            wallpapers.Add(new Wallpaper
                            {
                                Id = Guid.NewGuid().ToString(),
                                Title = $"Bing历史壁纸 #{i + 1}",
                                Copyright = "来源: Bing随机历史壁纸",
                                Source = WallpaperSource.Bing,
                                OriginalUrl = fullUrl,
                                FullUrl = fullUrl,
                                ThumbUrl = thumbUrl,
                                AddedDate = DateTime.Now
                            });
                            
                            System.Diagnostics.Debug.WriteLine($"[BingAPI] 获取第 {i + 1} 张 UHD: {fullUrl}");
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[BingAPI] 获取第 {i + 1} 张失败: {ex.Message}");
                    }
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
        private async Task<List<Wallpaper>> GetWallpapersByMarketAsync(string market, int n = 8)
        {
            try
            {
                // Bing壁纸API
                var url = $"https://www.bing.com/HPImageArchive.aspx?format=js&idx=0&n={n}&mkt={market}";
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
                        var fullUrl = $"https://www.bing.com{urlBase}_UHD.jpg";
                        var thumbUrl = $"https://www.bing.com{urlBase}_1920x1080.jpg";
                        
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
