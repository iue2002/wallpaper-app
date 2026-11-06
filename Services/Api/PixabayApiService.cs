using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using App1.Config;
using App1.Models;
using App1.Services.Quality;
using App1.Services.Network;

namespace App1.Services.Api
{
    /// <summary>
    /// Pixabay 免费图库（https://pixabay.com/api/docs/）
    ///
    /// 为什么值得优先用：
    ///   1. 内容为编辑审核入库，不会出现随机图接口那种失控内容；
    ///   2. 支持服务端直接筛「横向 + 最小宽高」，本地不需要探测尺寸、不需要下载原图；
    ///   3. 同时给出 640 宽缩略图与原图地址，列表用小图、点开用原图，天然分离。
    ///
    /// 未配置 API Key 时整体跳过（返回空列表），不影响其它源。
    /// </summary>
    public class PixabayApiService : IDisposable
    {
        private const string BASE_URL = "https://pixabay.com/api/";

        private readonly HttpClient _httpClient;
        private readonly Random _random = new();

        // 分类关键词：每次随机一个，避免每次都是同一批图
        private static readonly string[] Keywords =
        {
            "landscape", "nature", "mountain", "ocean", "sky",
            "forest", "sunset", "lake", "desert", "waterfall"
        };

        /// <summary>是否已配置 API Key</summary>
        public static bool IsEnabled => !string.IsNullOrWhiteSpace(AppConfig.PixabayApiKey);

        public PixabayApiService()
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
            _httpClient.DefaultRequestHeaders.Add("User-Agent", AppConfig.WikimediaUserAgent);
        }

        /// <summary>
        /// 获取横向高清壁纸。
        /// 服务端已按 min_width/min_height/orientation 过滤，返回的每一张都满足尺寸要求，
        /// 本地仍用统一规则复核一次，防止接口忽略参数时漏进来。
        /// </summary>
        public async Task<List<Wallpaper>> GetWallpapersAsync(int count = 6)
        {
            var wallpapers = new List<Wallpaper>();

            if (!IsEnabled)
            {
                System.Diagnostics.Debug.WriteLine("[Pixabay] 未配置 API Key，跳过（在 Config/AppConfig.cs 填入 PixabayApiKey 即可启用）");
                return wallpapers;
            }

            try
            {
                var keyword = Keywords[_random.Next(Keywords.Length)];
                var page = _random.Next(1, 4);
                var perPage = Math.Clamp(count * 8, 20, 100);

                var url = $"{BASE_URL}?key={AppConfig.PixabayApiKey}" +
                          $"&q={Uri.EscapeDataString(keyword)}" +
                          "&image_type=photo" +
                          "&orientation=horizontal" +
                          "&safesearch=true" +
                          $"&min_width={WallpaperQualityRule.MinWidth}" +
                          $"&min_height={WallpaperQualityRule.MinHeight}" +
                          $"&per_page={perPage}" +
                          $"&page={page}";

                System.Diagnostics.Debug.WriteLine($"[Pixabay] 请求 {count} 张（关键词 {keyword}，第 {page} 页，每页 {perPage}）...");

                var json = await _httpClient.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("hits", out var hits) || hits.GetArrayLength() == 0)
                {
                    System.Diagnostics.Debug.WriteLine("[Pixabay] 接口返回 0 条");
                    return wallpapers;
                }

                foreach (var hit in hits.EnumerateArray())
                {
                    if (wallpapers.Count >= count) break;

                    var width = GetInt(hit, "imageWidth");
                    var height = GetInt(hit, "imageHeight");

                    // 统一规则复核：横向 + 宽≥2560 + 高≥1440
                    if (!WallpaperQualityRule.IsAcceptable(width, height))
                    {
                        System.Diagnostics.Debug.WriteLine($"[Pixabay] 跳过（{WallpaperQualityRule.Explain(width, height)}）: {width}x{height}");
                        continue;
                    }

                    var fullUrl = GetString(hit, "imageURL");
                    if (string.IsNullOrEmpty(fullUrl))
                    {
                        // 部分返回不含原图字段，用 1280 档换成 2560 档（Pixabay CDN 命名规则）
                        fullUrl = (GetString(hit, "largeImageURL") ?? string.Empty).Replace("_1280.", "_2560.");
                    }

                    var thumbUrl = GetString(hit, "webformatURL");
                    if (string.IsNullOrEmpty(fullUrl)) continue;
                    if (string.IsNullOrEmpty(thumbUrl)) thumbUrl = fullUrl;

                    var tags = GetString(hit, "tags") ?? "Pixabay";
                    var user = GetString(hit, "user") ?? "unknown";

                    wallpapers.Add(new Wallpaper
                    {
                        Id = Guid.NewGuid().ToString(),
                        Title = tags.Length > 60 ? tags.Substring(0, 60) : tags,
                        Copyright = $"Pixabay / {user}（Pixabay 内容许可）",
                        Source = WallpaperSource.Pixabay,
                        OriginalUrl = fullUrl,
                        FullUrl = fullUrl,
                        ThumbUrl = thumbUrl,
                        AddedDate = DateTime.Now
                    });

                    System.Diagnostics.Debug.WriteLine($"[Pixabay] ✓ {tags} ({width}x{height})");
                }

                System.Diagnostics.Debug.WriteLine($"[Pixabay] ✅ 获取 {wallpapers.Count} 张");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Pixabay] ❌ 失败: {ex.GetType().Name} - {ex.Message}");
                SourceGate.ReportFailure("pixabay", ex);
            }

            return wallpapers;
        }

        private static int GetInt(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value)) return 0;
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : 0;
        }

        private static string? GetString(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value)) return null;
            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }
}
