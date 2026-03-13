using App1.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace App1.Services.Api
{
    /// <summary>
    /// Unsplash API 服务（限流：每小时12次请求）
    /// </summary>
    public class UnsplashApiService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private const string ACCESS_KEY = "qC5jdL0-IHbqBIXRFsgFSEReF5mhDv8Ks1cZioG_7WA";
        private const string BASE_URL = "https://api.unsplash.com";
        private static readonly Random _random = new Random();
        
        // 请求频率限制：每小时12次
        private static readonly List<DateTime> _requestHistory = new();
        private const int MAX_REQUESTS_PER_HOUR = 12;
        private static readonly object _lockObject = new();

        public UnsplashApiService()
        {
            // 配置 HttpClientHandler 禁用代理
            var handler = new HttpClientHandler
            {
                UseProxy = false,
                Proxy = null
            };
            
            _httpClient = new HttpClient(handler);
            _httpClient.Timeout = TimeSpan.FromSeconds(10); // 10秒超时
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Client-ID {ACCESS_KEY}");
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "WallpaperApp/1.0");
        }
        
        /// <summary>
        /// 检查是否可以发起请求（频率限制）
        /// </summary>
        private static bool CanMakeRequest()
        {
            lock (_lockObject)
            {
                var oneHourAgo = DateTime.Now.AddHours(-1);
                
                // 清理1小时前的记录
                _requestHistory.RemoveAll(time => time < oneHourAgo);
                
                // 检查是否达到限制
                if (_requestHistory.Count >= MAX_REQUESTS_PER_HOUR)
                {
                    var oldestRequest = _requestHistory.Min();
                    var waitMinutes = (int)Math.Ceiling((oldestRequest.AddHours(1) - DateTime.Now).TotalMinutes);
                    System.Diagnostics.Debug.WriteLine($"[Unsplash API] ⚠️ 已达到频率限制（{MAX_REQUESTS_PER_HOUR}次/小时），请等待 {waitMinutes} 分钟");
                    return false;
                }
                
                // 记录本次请求
                _requestHistory.Add(DateTime.Now);
                System.Diagnostics.Debug.WriteLine($"[Unsplash API] 本小时已请求 {_requestHistory.Count}/{MAX_REQUESTS_PER_HOUR} 次");
                return true;
            }
        }

        /// <summary>
        /// 获取随机壁纸（横向，适合桌面）
        /// </summary>
        public async Task<List<Wallpaper>> GetRandomWallpapersAsync(int count = 1)
        {
            try
            {
                // 检查频率限制
                if (!CanMakeRequest())
                {
                    System.Diagnostics.Debug.WriteLine($"[Unsplash API] 跳过请求（频率限制）");
                    return new List<Wallpaper>();
                }
                
                var wallpapers = new List<Wallpaper>();
                
                System.Diagnostics.Debug.WriteLine($"[Unsplash API] 获取 {count} 张随机壁纸（横向）...");

                // Unsplash API: 获取随机图片，指定横向
                var url = $"{BASE_URL}/photos/random?count={count}&orientation=landscape";
                
                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();
                
                var json = await response.Content.ReadAsStringAsync();
                var jsonArray = JsonDocument.Parse(json).RootElement;

                foreach (var item in jsonArray.EnumerateArray())
                {
                    try
                    {
                        var id = item.GetProperty("id").GetString();
                        var description = item.TryGetProperty("description", out var desc) && desc.ValueKind != JsonValueKind.Null
                            ? desc.GetString()
                            : item.TryGetProperty("alt_description", out var altDesc) && altDesc.ValueKind != JsonValueKind.Null
                                ? altDesc.GetString()
                                : "Unsplash Photo";

                        var urls = item.GetProperty("urls");
                        var thumbUrl = urls.GetProperty("small").GetString();  // 缩略图
                        var fullUrl = urls.GetProperty("regular").GetString(); // 高质量图

                        var width = item.GetProperty("width").GetInt32();
                        var height = item.GetProperty("height").GetInt32();

                        // 只保留横向图片
                        if (width < height)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Unsplash API] 跳过竖向图片: {width}x{height}");
                            continue;
                        }

                        var wallpaper = new Wallpaper
                        {
                            Title = description ?? "Unsplash Photo",
                            ThumbUrl = thumbUrl,
                            FullUrl = fullUrl,
                            Source = WallpaperSource.Unsplash,
                            Copyright = "Unsplash",
                            AddedDate = DateTime.Now
                        };

                        wallpapers.Add(wallpaper);
                        System.Diagnostics.Debug.WriteLine($"[Unsplash API] ✓ 添加: {wallpaper.Title} ({width}x{height})");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Unsplash API] 解析图片失败: {ex.Message}");
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[Unsplash API] ✅ 获取 {wallpapers.Count} 张横向壁纸");
                return wallpapers;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Unsplash API] ❌ 获取失败: {ex.Message}");
                return new List<Wallpaper>();
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
