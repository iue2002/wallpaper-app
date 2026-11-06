using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using App1.Config;
using App1.Models;
using App1.Services.Quality;
using App1.Services.Network;

namespace App1.Services.Api
{
    /// <summary>
    /// Wikimedia Commons 维基共享资源（https://commons.wikimedia.org/w/api.php）
    ///
    /// 关键约束（实测得出，别想当然）：
    ///   Wikimedia 只对外服务「预先生成好」的缩略图尺寸（常见 640/960/1280/1920/3840 等档），
    ///   自己把 URL 里的宽度段改成任意数字会直接 400。
    ///   所以不能靠拼 URL 拿小图，必须让接口告诉你地址：
    ///     - 列表缩略图：iiurlwidth=640  → 接口返回 960px 档，约 140KB
    ///     - 展示大图：  iiurlwidth=2560 → 接口返回 3840px 档，约 1.4MB
    ///   两次调用都只是 JSON，不下载任何图片，代价很小。
    ///
    /// 另一个好处：原始宽高由 iiprop=size 直接给出，尺寸筛选完全不碰图片本身
    /// （Commons 的原图可能是几十 MB 的 TIFF/PNG）。
    ///
    /// 授权：CC BY / CC BY-SA / 公有领域，版权写入 Copyright 字段。
    /// </summary>
    public class WikimediaCommonsApiService : IDisposable
    {
        private const string API_URL = "https://commons.wikimedia.org/w/api.php";
        private const int ThumbWidth = 640;
        private const int FullWidth = 2560;

        private readonly HttpClient _httpClient;
        private readonly Random _random = new();

        // 只用经过社区评选的高质量分类，避免混进图表、Logo、截图
        private static readonly string[] Categories =
        {
            "Category:Featured pictures on Wikimedia Commons",
            "Category:Quality images"
        };

        // 随机起点：分类按文件名排序，给个随机字母就能拿到不同片段
        private static readonly string[] SortKeyStarts =
        {
            "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M",
            "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z"
        };

        public WikimediaCommonsApiService()
        {
            var handler = new HttpClientHandler
            {
                UseProxy = false,
                Proxy = null
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(12)
            };
            // Wikimedia 要求请求方带可识别的 User-Agent，否则会被限流
            _httpClient.DefaultRequestHeaders.Add("User-Agent", AppConfig.WikimediaUserAgent);
        }

        /// <summary>
        /// 获取横向高清壁纸。全程只用元数据与小尺寸缩略图，不碰原图。
        /// </summary>
        public async Task<List<Wallpaper>> GetWallpapersAsync(int count = 6)
        {
            var result = new List<Wallpaper>();

            try
            {
                var candidates = await FetchCandidatesAsync(count);
                if (candidates.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine("[Commons] 本批没有合格的横向高清图");
                    return result;
                }

                await FillFullUrlsAsync(candidates);

                foreach (var candidate in candidates)
                {
                    if (result.Count >= count) break;
                    if (string.IsNullOrEmpty(candidate.FullUrl)) continue;

                    result.Add(new Wallpaper
                    {
                        Id = Guid.NewGuid().ToString(),
                        Title = CleanTitle(candidate.Title),
                        Copyright = "Wikimedia Commons（CC BY / CC BY-SA / 公有领域）",
                        Source = WallpaperSource.Wikimedia,
                        OriginalUrl = candidate.OriginalUrl ?? candidate.FullUrl,
                        FullUrl = candidate.FullUrl,
                        ThumbUrl = candidate.ThumbUrl,
                        AddedDate = DateTime.Now
                    });

                    System.Diagnostics.Debug.WriteLine($"[Commons] ✓ {candidate.Title} ({candidate.Width}x{candidate.Height})");
                }

                System.Diagnostics.Debug.WriteLine($"[Commons] ✅ 获取 {result.Count} 张");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Commons] ❌ 失败: {ex.GetType().Name} - {ex.Message}");
                SourceGate.ReportFailure("commons", ex);
            }

            return result;
        }

        /// <summary>
        /// 第一步：从随机分类片段取候选，用原始宽高做筛选，同时拿到 640 档缩略图。
        /// </summary>
        private async Task<List<Candidate>> FetchCandidatesAsync(int count)
        {
            var candidates = new List<Candidate>();

            var category = Categories[_random.Next(Categories.Length)];
            var sortStart = SortKeyStarts[_random.Next(SortKeyStarts.Length)];
            var limit = Math.Clamp(count * 6, 20, 50);

            var url = $"{API_URL}?action=query&format=json" +
                      "&generator=categorymembers" +
                      $"&gcmtitle={Uri.EscapeDataString(category)}" +
                      "&gcmtype=file" +
                      $"&gcmlimit={limit}" +
                      $"&gcmstartsortkey={sortStart}" +
                      "&prop=imageinfo" +
                      "&iiprop=url%7Csize" +
                      $"&iiurlwidth={ThumbWidth}";

            System.Diagnostics.Debug.WriteLine($"[Commons] 取样：{category}（起点 {sortStart}，{limit} 条）...");

            var json = await _httpClient.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("query", out var query) ||
                !query.TryGetProperty("pages", out var pages))
            {
                return candidates;
            }

            foreach (var page in pages.EnumerateObject())
            {
                if (!page.Value.TryGetProperty("imageinfo", out var infoArray) || infoArray.GetArrayLength() == 0)
                {
                    continue;
                }

                var info = infoArray[0];
                var width = GetInt(info, "width");
                var height = GetInt(info, "height");

                if (!WallpaperQualityRule.IsAcceptable(width, height))
                {
                    System.Diagnostics.Debug.WriteLine($"[Commons] 跳过（{WallpaperQualityRule.Explain(width, height)}）: {width}x{height}");
                    continue;
                }

                var title = page.Value.TryGetProperty("title", out var titleElement)
                    ? titleElement.GetString() ?? string.Empty
                    : string.Empty;

                var thumbUrl = GetString(info, "thumburl") ?? string.Empty;

                // 没有 /thumb/ 说明接口没给出缩略图（通常是被要求了比原图更大的宽度），换不出小图，跳过
                if (string.IsNullOrEmpty(title) || !thumbUrl.Contains("/thumb/"))
                {
                    System.Diagnostics.Debug.WriteLine($"[Commons] 跳过（无可用缩略图）: {title}");
                    continue;
                }

                candidates.Add(new Candidate
                {
                    Title = title,
                    Width = width,
                    Height = height,
                    ThumbUrl = thumbUrl,
                    OriginalUrl = GetString(info, "url")
                });
            }

            // 打乱，避免每次拿到同一批
            for (var i = candidates.Count - 1; i > 0; i--)
            {
                var j = _random.Next(i + 1);
                (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
            }

            return candidates.Take(Math.Max(count, 1) * 2).ToList();
        }

        /// <summary>
        /// 第二步：只为选中的这几张要 2560 档大图地址（一次请求批量取）。
        /// </summary>
        private async Task FillFullUrlsAsync(List<Candidate> candidates)
        {
            if (candidates.Count == 0) return;

            var titles = string.Join("|", candidates.Select(c => c.Title));
            var url = $"{API_URL}?action=query&format=json" +
                      "&prop=imageinfo" +
                      "&iiprop=url%7Csize" +
                      $"&iiurlwidth={FullWidth}" +
                      $"&titles={Uri.EscapeDataString(titles)}";

            var json = await _httpClient.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("query", out var query) ||
                !query.TryGetProperty("pages", out var pages))
            {
                return;
            }

            var byTitle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var page in pages.EnumerateObject())
            {
                if (!page.Value.TryGetProperty("title", out var titleElement)) continue;
                if (!page.Value.TryGetProperty("imageinfo", out var infoArray) || infoArray.GetArrayLength() == 0) continue;

                var info = infoArray[0];
                var title = titleElement.GetString() ?? string.Empty;
                var thumbUrl = GetString(info, "thumburl");

                if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(thumbUrl) && thumbUrl.Contains("/thumb/"))
                {
                    byTitle[title] = thumbUrl;
                }
            }

            foreach (var candidate in candidates)
            {
                if (byTitle.TryGetValue(candidate.Title, out var fullUrl))
                {
                    candidate.FullUrl = fullUrl;
                }
            }

            System.Diagnostics.Debug.WriteLine($"[Commons] 大图地址就绪 {byTitle.Count}/{candidates.Count}");
        }

        private static string CleanTitle(string title)
        {
            var name = title.StartsWith("File:", StringComparison.OrdinalIgnoreCase)
                ? title.Substring(5)
                : title;

            var dot = name.LastIndexOf('.');
            if (dot > 0) name = name.Substring(0, dot);

            name = name.Replace('_', ' ').Trim();
            return name.Length > 70 ? name.Substring(0, 70) : name;
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

        private sealed class Candidate
        {
            public string Title { get; set; } = string.Empty;
            public int Width { get; set; }
            public int Height { get; set; }
            public string ThumbUrl { get; set; } = string.Empty;
            public string? OriginalUrl { get; set; }
            public string FullUrl { get; set; } = string.Empty;
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }
}
