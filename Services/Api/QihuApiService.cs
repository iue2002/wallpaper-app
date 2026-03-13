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
    /// 360壁纸API服务实现
    /// </summary>
    public class QihuApiService
    {
        private readonly HttpClient _httpClient;
        
        // 精选的壁纸分类（排除美女、性感类）
        private readonly string[] _categoryIds = new[] 
        { 
            "26",  // 动漫卡通
            "11",  // 游戏壁纸
            "12",  // 明星影视
            "15",  // 汽车壁纸
            "9",   // 体育运动
            "36",  // 军事壁纸
            "30",  // 炫彩视觉
            "5",   // 风景壁纸
            "10",  // 萌宠动物
            "38"   // 城市建筑
        };
        
        private readonly Random _random = new Random();
        
        public QihuApiService()
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
        /// 获取指定分类的壁纸
        /// </summary>
        public async Task<List<Wallpaper>> GetWallpapersByCategoryAsync(string categoryId, int start = 0, int count = 20)
        {
            try
            {
                var url = $"http://wallpaper.apc.360.cn/index.php?c=WallPaper&a=getAppsByCategory&cid={categoryId}&start={start}&count={count}&from=360chrome";
                var response = await _httpClient.GetStringAsync(url);
                
                var json = JsonDocument.Parse(response);
                if (!json.RootElement.TryGetProperty("data", out var dataArray))
                {
                    return new List<Wallpaper>();
                }
                
                var wallpapers = new List<Wallpaper>();
                foreach (var item in dataArray.EnumerateArray())
                {
                    try
                    {
                        // 获取2K分辨率（2560x1440）的图片
                        var url_2k = item.GetProperty("img_1600_900").GetString()?.Replace("1600_900_85", "2560_1440_100");
                        var thumbUrl = item.GetProperty("url_thumb").GetString();
                        var utag = item.TryGetProperty("utag", out var utagProp) ? utagProp.GetString() : "";
                        
                        if (string.IsNullOrEmpty(url_2k) || string.IsNullOrEmpty(thumbUrl))
                            continue;
                        
                        wallpapers.Add(new Wallpaper
                        {
                            Id = Guid.NewGuid().ToString(),
                            Title = utag ?? "360壁纸",
                            Copyright = $"来源: 360壁纸",
                            Source = WallpaperSource.Qihu360,
                            OriginalUrl = url_2k,
                            FullUrl = url_2k,
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
                System.Diagnostics.Debug.WriteLine($"[360API] 获取壁纸失败: {ex.Message}");
                return new List<Wallpaper>();
            }
        }
        
        /// <summary>
        /// 获取随机类别的壁纸（每次随机选择类别）
        /// </summary>
        public async Task<List<Wallpaper>> GetWallpapersAsync(int countPerCategory = 10)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[360API] 开始获取360壁纸...");
                
                // 随机选择一个类别（确保每次不同）
                var randomCategoryIndex = _random.Next(0, _categoryIds.Length);
                var selectedCategory = _categoryIds[randomCategoryIndex];
                
                // 随机起始位置，范围 0-500
                var randomStart = _random.Next(0, 500);
                
                var categoryName = GetCategoryName(selectedCategory);
                System.Diagnostics.Debug.WriteLine($"[360API] 随机选择类别: {categoryName} (ID:{selectedCategory}, 起始:{randomStart})");
                
                var wallpapers = await GetWallpapersByCategoryAsync(selectedCategory, randomStart, countPerCategory);
                
                System.Diagnostics.Debug.WriteLine($"[360API] ✅ 获取 {wallpapers.Count} 张 {categoryName} 壁纸");
                return wallpapers;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[360API] ❌ 获取失败: {ex.Message}");
                return new List<Wallpaper>();
            }
        }
        
        /// <summary>
        /// 获取类别名称（用于调试显示）
        /// </summary>
        private string GetCategoryName(string categoryId)
        {
            return categoryId switch
            {
                "26" => "动漫卡通",
                "11" => "游戏壁纸",
                "12" => "明星影视",
                "15" => "汽车壁纸",
                "9" => "体育运动",
                "36" => "军事壁纸",
                "30" => "炫彩视觉",
                "5" => "风景壁纸",
                "10" => "萌宠动物",
                "38" => "城市建筑",
                _ => "未知类别"
            };
        }
        
        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
