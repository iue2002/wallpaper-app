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
    /// Pexels API服务
    /// </summary>
    public class PexelsApiService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly Random _random;
        private const string API_KEY = "nYmpmW5gyUaijsRyzB1pOKuBUK7KUyUKjqGCBnklyXBl6AULvAFyvDTz";
        private const string BASE_URL = "https://api.pexels.com/v1";
        
        public PexelsApiService()
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
            _httpClient.DefaultRequestHeaders.Add("Authorization", API_KEY);
            _random = new Random();
        }
        
        /// <summary>
        /// 获取随机精选壁纸（随机页码）
        /// </summary>
        public async Task<List<Wallpaper>> GetRandomCuratedPhotosAsync(int perPage = 10)
        {
            // Pexels精选照片有数千张，随机选择1-100页之间的页面
            var randomPage = _random.Next(1, 101);
            System.Diagnostics.Debug.WriteLine($"[Pexels API] 随机选择第 {randomPage} 页");
            return await GetCuratedPhotosAsync(randomPage, perPage);
        }
        
        /// <summary>
        /// 获取精选壁纸
        /// </summary>
        public async Task<List<Wallpaper>> GetCuratedPhotosAsync(int page = 1, int perPage = 10)
        {
            var wallpapers = new List<Wallpaper>();
            
            try
            {
                System.Diagnostics.Debug.WriteLine($"[Pexels API] 获取精选壁纸（第 {page} 页，每页 {perPage} 张）...");
                
                // 不限制方向，获取所有高质量图片（包括8K竖屏）
                var url = $"{BASE_URL}/curated?page={page}&per_page={perPage}";
                
                var response = await _httpClient.GetStringAsync(url);
                var jsonDoc = JsonDocument.Parse(response);
                
                if (!jsonDoc.RootElement.TryGetProperty("photos", out var photos))
                {
                    return wallpapers;
                }
                
                foreach (var item in photos.EnumerateArray())
                {
                    try
                    {
                        var id = item.GetProperty("id").GetInt32();
                        var alt = item.TryGetProperty("alt", out var altProp) ? altProp.GetString() : "Pexels精选壁纸";
                        
                        // 获取摄影师信息
                        var photographer = item.GetProperty("photographer").GetString() ?? "Unknown";
                        
                        // 获取图片尺寸
                        var width = item.GetProperty("width").GetInt32();
                        var height = item.GetProperty("height").GetInt32();
                        
                        // 智能过滤：横向图片全部保留，竖向图片只保留4K以上
                        if (width <= height)
                        {
                            // 竖屏图片：检查是否为4K以上高分辨率（宽度>=2160 或 高度>=3840）
                            bool is4KOrHigher = width >= 2160 || height >= 3840;
                            
                            if (!is4KOrHigher)
                            {
                                System.Diagnostics.Debug.WriteLine($"[Pexels API] 跳过低分辨率竖屏: {width}x{height}");
                                continue;
                            }
                            
                            System.Diagnostics.Debug.WriteLine($"[Pexels API] ✓ 保留4K竖屏: {width}x{height}");
                        }
                        
                        // 获取图片URL（使用最高质量）
                        var src = item.GetProperty("src");
                        var original = src.GetProperty("original").GetString(); // 原始最高质量
                        var medium = src.GetProperty("medium").GetString(); // 缩略图
                        
                        if (string.IsNullOrEmpty(original) || string.IsNullOrEmpty(medium))
                            continue;
                        
                        wallpapers.Add(new Wallpaper
                        {
                            Id = Guid.NewGuid().ToString(),
                            Title = alt ?? "Pexels精选",
                            Copyright = $"摄影师: {photographer} | Pexels",
                            Source = WallpaperSource.Pexels,
                            OriginalUrl = original,
                            FullUrl = original, // 使用original（最高质量）
                            ThumbUrl = medium,
                            AddedDate = DateTime.Now
                        });
                        
                        System.Diagnostics.Debug.WriteLine($"[Pexels API] ✓ {alt} by {photographer}");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Pexels API] 解析照片失败: {ex.Message}");
                    }
                }
                
                System.Diagnostics.Debug.WriteLine($"[Pexels API] ✅ 成功获取 {wallpapers.Count} 张壁纸");
                return wallpapers;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Pexels API] ❌ 获取失败: {ex.Message}");
                return wallpapers;
            }
        }
        
        /// <summary>
        /// 获取精选壁纸（别名方法）
        /// </summary>
        public async Task<List<Wallpaper>> GetCuratedWallpapersAsync(int perPage = 10)
        {
            return await GetRandomCuratedPhotosAsync(perPage);
        }
        
        /// <summary>
        /// 搜索特定主题的壁纸
        /// </summary>
        public async Task<List<Wallpaper>> SearchPhotosAsync(string query, int page = 1, int perPage = 10)
        {
            var wallpapers = new List<Wallpaper>();
            
            try
            {
                System.Diagnostics.Debug.WriteLine($"[Pexels API] 搜索主题: {query}");
                
                // 不限制方向，获取所有高质量图片
                var url = $"{BASE_URL}/search?query={Uri.EscapeDataString(query)}&page={page}&per_page={perPage}";
                
                var response = await _httpClient.GetStringAsync(url);
                var jsonDoc = JsonDocument.Parse(response);
                
                if (!jsonDoc.RootElement.TryGetProperty("photos", out var photos))
                {
                    return wallpapers;
                }
                
                foreach (var item in photos.EnumerateArray())
                {
                    try
                    {
                        var alt = item.TryGetProperty("alt", out var altProp) ? altProp.GetString() : query;
                        var photographer = item.GetProperty("photographer").GetString() ?? "Unknown";
                        
                        // 获取图片尺寸并进行智能过滤
                        var width = item.GetProperty("width").GetInt32();
                        var height = item.GetProperty("height").GetInt32();
                        
                        // 智能过滤：横向图片全部保留，竖向图片只保留4K以上
                        if (width <= height)
                        {
                            // 竖屏图片：检查是否为4K以上高分辨率（宽度>=2160 或 高度>=3840）
                            bool is4KOrHigher = width >= 2160 || height >= 3840;
                            
                            if (!is4KOrHigher)
                            {
                                System.Diagnostics.Debug.WriteLine($"[Pexels 搜索] 跳过低分辨率竖屏: {width}x{height}");
                                continue;
                            }
                            
                            System.Diagnostics.Debug.WriteLine($"[Pexels 搜索] ✓ 保留4K竖屏: {width}x{height}");
                        }
                        
                        var src = item.GetProperty("src");
                        var original = src.GetProperty("original").GetString(); // 原始最高质量
                        var medium = src.GetProperty("medium").GetString(); // 缩略图
                        
                        if (string.IsNullOrEmpty(original) || string.IsNullOrEmpty(medium))
                            continue;
                        
                        wallpapers.Add(new Wallpaper
                        {
                            Id = Guid.NewGuid().ToString(),
                            Title = alt ?? query,
                            Copyright = $"摄影师: {photographer} | Pexels",
                            Source = WallpaperSource.Pexels,
                            OriginalUrl = original,
                            FullUrl = original, // 使用original（最高质量）
                            ThumbUrl = medium,
                            AddedDate = DateTime.Now
                        });
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Pexels API] 解析照片失败: {ex.Message}");
                    }
                }
                
                System.Diagnostics.Debug.WriteLine($"[Pexels API] ✅ 搜索到 {wallpapers.Count} 张壁纸");
                return wallpapers;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Pexels API] ❌ 搜索失败: {ex.Message}");
                return wallpapers;
            }
        }
        
        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
