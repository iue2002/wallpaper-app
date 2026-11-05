using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using App1.Config;
using App1.Models;
using App1.Services.Quality;

namespace App1.Services.Api
{
    /// <summary>
    /// Lorem Picsum（https://picsum.photos）—— Unsplash 授权图片的随机分发服务，免 Key。
    ///
    /// /v2/list 接口直接返回每张图的 width / height，所以可以只凭元数据做尺寸筛选，
    /// 不需要下载图片再探测。
    ///
    /// 注意：图床走 Fastly CDN，大陆实测偶发偏慢（2560x1440 约 620KB / 10 秒），
    /// 因此本源定位为「兜底补位」，不作为首屏优先源。
    /// </summary>
    public class PicsumApiService : IDisposable
    {
        private const string LIST_URL = "https://picsum.photos/v2/list";
        private const string IMAGE_BASE = "https://picsum.photos/id";

        private readonly HttpClient _httpClient;
        private readonly Random _random = new();

        public PicsumApiService()
        {
            var handler = new HttpClientHandler
            {
                UseProxy = false,
                Proxy = null
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            // Picsum 对无 UA 的请求会返回空内容
            _httpClient.DefaultRequestHeaders.Add("User-Agent", AppConfig.WikimediaUserAgent);
        }

        /// <summary>
        /// 获取横向高清壁纸。元数据阶段就把不合格的筛掉，不产生任何图片下载。
        /// </summary>
        public async Task<List<Wallpaper>> GetWallpapersAsync(int count = 6)
        {
            var wallpapers = new List<Wallpaper>();

            try
            {
                // Picsum 总量约 1000 张，按页随机取，避免每次都是同一批
                var page = _random.Next(1, 60);
                var limit = Math.Clamp(count * 6, 20, 100);

                var url = $"{LIST_URL}?page={page}&limit={limit}";
                System.Diagnostics.Debug.WriteLine($"[Picsum] 请求 {count} 张（第 {page} 页，{limit} 条）...");

                var json = await _httpClient.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                {
                    System.Diagnostics.Debug.WriteLine("[Picsum] 接口返回空");
                    return wallpapers;
                }

                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (wallpapers.Count >= count) break;

                    var width = GetInt(item, "width");
                    var height = GetInt(item, "height");

                    if (!WallpaperQualityRule.IsAcceptable(width, height))
                    {
                        System.Diagnostics.Debug.WriteLine($"[Picsum] 跳过（{WallpaperQualityRule.Explain(width, height)}）: {width}x{height}");
                        continue;
                    }

                    var id = item.TryGetProperty("id", out var idElement) ? idElement.ToString() : null;
                    var author = item.TryGetProperty("author", out var authorElement) ? authorElement.GetString() : "unknown";
                    var fullUrl = item.TryGetProperty("download_url", out var dlElement) ? dlElement.GetString() : null;

                    // download_url 就是原尺寸地址；拿不到时按 id 拼一个不低于标准的地址
                    if (string.IsNullOrEmpty(fullUrl))
                    {
                        if (string.IsNullOrEmpty(id)) continue;
                        fullUrl = $"{IMAGE_BASE}/{id}/{Math.Max(width, WallpaperQualityRule.MinWidth)}/{Math.Max(height, WallpaperQualityRule.MinHeight)}";
                    }

                    // 列表缩略图固定 640 宽，避免列表去拉原图
                    var thumbUrl = string.IsNullOrEmpty(id)
                        ? fullUrl
                        : $"{IMAGE_BASE}/{id}/640/360";

                    wallpapers.Add(new Wallpaper
                    {
                        Id = Guid.NewGuid().ToString(),
                        Title = $"Picsum #{id} by {author}",
                        Copyright = $"Picsum / {author}（Unsplash 许可）",
                        Source = WallpaperSource.Picsum,
                        OriginalUrl = fullUrl,
                        FullUrl = fullUrl,
                        ThumbUrl = thumbUrl,
                        AddedDate = DateTime.Now
                    });

                    System.Diagnostics.Debug.WriteLine($"[Picsum] ✓ #{id} by {author} ({width}x{height})");
                }

                System.Diagnostics.Debug.WriteLine($"[Picsum] ✅ 获取 {wallpapers.Count} 张");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Picsum] ❌ 失败: {ex.GetType().Name} - {ex.Message}");
            }

            return wallpapers;
        }

        private static int GetInt(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value)) return 0;
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : 0;
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }
}
