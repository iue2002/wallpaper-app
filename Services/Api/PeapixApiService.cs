using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using App1.Config;
using App1.Models;

namespace App1.Services.Api
{
    /// <summary>
    /// Peapix API服务实现
    /// </summary>
    public class PeapixApiService : IPeapixApiService
    {
        private readonly HttpClient _httpClient;
        
        public PeapixApiService()
        {
            // 配置 HttpClientHandler 禁用代理
            var handler = new HttpClientHandler
            {
                UseProxy = false,
                Proxy = null
            };
            
            _httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri(AppConfig.PeapixBaseUrl),
                Timeout = TimeSpan.FromSeconds(AppConfig.HttpTimeoutSeconds)
            };
        }
        
        /// <summary>
        /// 获取Spotlight壁纸
        /// </summary>
        public async Task<List<Wallpaper>> GetSpotlightWallpapersAsync()
        {
            try
            {
                var url = $"spotlight/feed?n={AppConfig.SpotlightImageCount}";
                System.Diagnostics.Debug.WriteLine($"[API] 请求Spotlight: {url}");
                
                var response = await _httpClient.GetStringAsync(url);
                System.Diagnostics.Debug.WriteLine($"[API] 响应长度: {response.Length} 字符");
                
                var peapixWallpapers = JsonSerializer.Deserialize<List<PeapixWallpaper>>(response);
                System.Diagnostics.Debug.WriteLine($"[API] 解析结果: {peapixWallpapers?.Count ?? 0} 张壁纸");
                
                if (peapixWallpapers == null || !peapixWallpapers.Any())
                {
                    System.Diagnostics.Debug.WriteLine("[API] ⚠️ 警告：API返回空数据");
                    return new List<Wallpaper>();
                }
                
                var result = peapixWallpapers.Select(pw => new Wallpaper
                {
                    Id = Guid.NewGuid().ToString(),
                    Title = pw.Title,
                    Copyright = pw.Copyright,
                    Source = WallpaperSource.System,  // 改为 System 源
                    OriginalUrl = pw.ImageUrl,
                    FullUrl = pw.FullUrl,
                    ThumbUrl = pw.ThumbUrl,
                    AddedDate = DateTime.Now
                }).ToList();
                
                System.Diagnostics.Debug.WriteLine($"[API] ✅ Peapix成功获取: {result.Count} 张");
                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] ❌ 获取Peapix壁纸失败: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[API] 异常详情: {ex}");
                return new List<Wallpaper>();
            }
        }
        
        /// <summary>
        /// 获取Bing壁纸（多个国家）
        /// </summary>
        public async Task<List<Wallpaper>> GetBingWallpapersAsync()
        {
            var allWallpapers = new List<Wallpaper>();
            var countries = new[] { "cn", "us", "jp", "gb", "de", "fr", "au", "ca" }; // 8个主要国家
            
            try
            {
                System.Diagnostics.Debug.WriteLine($"[API] 开始获取多国Bing壁纸...");
                
                foreach (var country in countries)
                {
                    try
                    {
                        var url = $"bing/feed?country={country}&n={AppConfig.BingImageCount}";
                        var response = await _httpClient.GetStringAsync(url);
                        var peapixWallpapers = JsonSerializer.Deserialize<List<PeapixWallpaper>>(response);
                        
                        if (peapixWallpapers != null && peapixWallpapers.Any())
                        {
                            var wallpapers = peapixWallpapers.Select(pw => new Wallpaper
                            {
                                Id = Guid.NewGuid().ToString(),
                                Title = pw.Title,
                                Copyright = pw.Copyright,
                                Source = WallpaperSource.Bing,
                                OriginalUrl = pw.ImageUrl,
                                FullUrl = pw.FullUrl,
                                ThumbUrl = pw.ThumbUrl,
                                AddedDate = DateTime.Now
                            }).ToList();
                            
                            allWallpapers.AddRange(wallpapers);
                            System.Diagnostics.Debug.WriteLine($"[API] {country.ToUpper()}: {wallpapers.Count} 张");
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[API] 获取 {country} 壁纸失败: {ex.Message}");
                    }
                }
                
                // 根据 FullUrl 去重（不同国家可能有相同的图片）
                var uniqueWallpapers = allWallpapers
                    .GroupBy(w => w.FullUrl)
                    .Select(g => g.First())
                    .ToList();
                
                System.Diagnostics.Debug.WriteLine($"[API] ✅ Bing壁纸: 共获取 {allWallpapers.Count} 张, 去重后 {uniqueWallpapers.Count} 张");
                return uniqueWallpapers;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] ❌ 获取Bing壁纸失败: {ex.Message}");
                return allWallpapers;
            }
        }
        
        /// <summary>
        /// 下载图片到本地
        /// </summary>
        public async Task<string> DownloadImageAsync(string imageUrl, string localPath)
        {
            try
            {
                // 确保目录存在
                var directory = Path.GetDirectoryName(localPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                
                // 下载图片
                using var timeoutCts = new CancellationTokenSource(
                    TimeSpan.FromSeconds(AppConfig.DownloadTimeoutSeconds));
                    
                var imageBytes = await _httpClient.GetByteArrayAsync(imageUrl, timeoutCts.Token);
                await File.WriteAllBytesAsync(localPath, imageBytes, timeoutCts.Token);
                
                return localPath;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"下载图片失败: {ex.Message}");
                throw;
            }
        }
        
        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
