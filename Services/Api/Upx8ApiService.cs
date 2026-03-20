using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using App1.Models;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace App1.Services.Api
{
    /// <summary>
    /// Upx8壁纸API服务
    /// </summary>
    public class Upx8ApiService
    {
        private readonly HttpClient _httpClient;
        private const string BASE_URL = "https://wp.upx8.com/api.php";
        private readonly string _cacheFolder;

        public Upx8ApiService()
        {
            var handler = new HttpClientHandler
            {
                UseProxy = false,
                Proxy = null,
                AllowAutoRedirect = true
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            // 创建缓存文件夹
            var picturesFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            _cacheFolder = Path.Combine(picturesFolder, "小K壁纸", "Upx8");
            if (!Directory.Exists(_cacheFolder))
            {
                Directory.CreateDirectory(_cacheFolder);
            }
        }

        /// <summary>
        /// 获取Upx8壁纸
        /// </summary>
        public async Task<List<Wallpaper>> GetWallpapersAsync(int count = 1)
        {
            try
            {
                var wallpapers = new List<Wallpaper>();
                System.Diagnostics.Debug.WriteLine($"[Upx8 API] 获取 {count} 张壁纸...");

                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        // 添加时间戳和随机参数防止缓存
                        var timestamp = DateTime.Now.Ticks;
                        var random = new Random().Next(1000000);
                        var url = $"{BASE_URL}?ts={timestamp}&r={random}";

                        // 发送请求获取JSON数据
                        var response = await _httpClient.GetAsync(url);
                        
                        // 处理302重定向
                        if (response.StatusCode == System.Net.HttpStatusCode.Found)
                        {
                            var redirectUrl = response.Headers.Location;
                            if (redirectUrl != null)
                            {
                                System.Diagnostics.Debug.WriteLine($"[Upx8 API] 处理302重定向到: {redirectUrl}");
                                response = await _httpClient.GetAsync(redirectUrl);
                                response.EnsureSuccessStatusCode();
                            }
                        }
                        else
                        {
                            response.EnsureSuccessStatusCode();
                        }

                        var contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? string.Empty;
                        if (contentType.StartsWith("image/"))
                        {
                            var directImageUrl = NormalizeImageUrl(response.RequestMessage?.RequestUri?.ToString());
                            if (!string.IsNullOrEmpty(directImageUrl))
                            {
                                System.Diagnostics.Debug.WriteLine($"[Upx8 API] 直接返回图片，使用URL: {directImageUrl}");
                                var resolution = await GetImageResolutionAsync(directImageUrl);
                                await TryAddWallpaperAsync(wallpapers, directImageUrl, resolution.Width, resolution.Height);
                            }
                            continue;
                        }

                        var json = await response.Content.ReadAsStringAsync();
                        var normalizedJson = json.TrimStart('\uFEFF', '\u200B', '\u0000', ' ', '\t', '\r', '\n');
                        JsonDocument jsonDoc;
                        try
                        {
                            jsonDoc = JsonDocument.Parse(normalizedJson);
                        }
                        catch (JsonException)
                        {
                            var directImageUrl = NormalizeImageUrl(response.RequestMessage?.RequestUri?.ToString());
                            if (!string.IsNullOrEmpty(directImageUrl))
                            {
                                System.Diagnostics.Debug.WriteLine($"[Upx8 API] JSON解析失败，降级为图片URL: {directImageUrl}");
                                var resolution = await GetImageResolutionAsync(directImageUrl);
                                await TryAddWallpaperAsync(wallpapers, directImageUrl, resolution.Width, resolution.Height);
                            }
                            else
                            {
                                throw;
                            }
                            continue;
                        }

                        // 解析JSON数据
                        var root = jsonDoc.RootElement;
                        var code = root.GetProperty("code").GetInt32();
                        
                        if (code == 200)
                        {
                            var data = root.GetProperty("data");
                            var imgUrl = NormalizeImageUrl(data.GetProperty("url").GetString()?.Trim());
                            var width = data.GetProperty("width").GetInt32();
                            var height = data.GetProperty("height").GetInt32();

                            if (!string.IsNullOrEmpty(imgUrl))
                            {
                                await TryAddWallpaperAsync(wallpapers, imgUrl, width, height);
                            }
                        }
                        else
                        {
                            var msg = root.GetProperty("msg").GetString();
                            System.Diagnostics.Debug.WriteLine($"[Upx8 API] API返回错误: {msg}");
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Upx8 API] 获取单张壁纸失败: {ex.Message}");
                        continue;
                    }
                }

                return wallpapers;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Upx8 API] 获取壁纸失败: {ex.Message}");
                return new List<Wallpaper>();
            }
        }

        /// <summary>
        /// 下载并缓存图片
        /// </summary>
        private async Task<string> DownloadAndCacheImageAsync(string imageUrl)
        {
            try
            {
                // 使用URL的哈希作为文件名
                var urlHash = Math.Abs(imageUrl.GetHashCode()).ToString();
                var fileName = $"upx8_{urlHash}.jpg";
                var localPath = Path.Combine(_cacheFolder, fileName);

                // 检查是否已经缓存
                if (File.Exists(localPath))
                {
                    System.Diagnostics.Debug.WriteLine($"[Upx8 API] 使用缓存图片: {localPath}");
                    return localPath;
                }

                // 下载图片
                var imageBytes = await _httpClient.GetByteArrayAsync(imageUrl);
                await File.WriteAllBytesAsync(localPath, imageBytes);

                System.Diagnostics.Debug.WriteLine($"[Upx8 API] 图片下载完成: {imageBytes.Length / 1024} KB");
                System.Diagnostics.Debug.WriteLine($"[Upx8 API] 保存路径: {localPath}");

                return localPath;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Upx8 API] 下载图片失败: {ex.Message}");
                return string.Empty;
            }
        }

        private string NormalizeImageUrl(string? imageUrl)
        {
            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                return string.Empty;
            }

            if (imageUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                return "https://" + imageUrl.Substring("http://".Length);
            }

            return imageUrl;
        }

        /// <summary>
        /// 检查分辨率并添加壁纸（保留2K过滤机制）
        /// </summary>
        private async Task TryAddWallpaperAsync(List<Wallpaper> wallpapers, string imageUrl, int width, int height)
        {
            if (width < 2560 && height < 1440)
            {
                System.Diagnostics.Debug.WriteLine($"[Upx8 API] 跳过低分辨率壁纸: {imageUrl} ({width}x{height})");
                return;
            }

            var localPath = await DownloadAndCacheImageAsync(imageUrl);

            var wallpaper = new Wallpaper
            {
                Id = Guid.NewGuid().ToString(),
                Title = $"Upx8 壁纸 ({width}x{height})",
                Copyright = "来源: Upx8",
                Source = WallpaperSource.Upx8,
                OriginalUrl = imageUrl,
                FullUrl = imageUrl,
                ThumbUrl = imageUrl,
                LocalPath = localPath,
                IsDownloaded = !string.IsNullOrEmpty(localPath),
                AddedDate = DateTime.Now
            };

            wallpapers.Add(wallpaper);
            System.Diagnostics.Debug.WriteLine($"[Upx8 API] 成功获取壁纸: {imageUrl} ({width}x{height})");
            if (!string.IsNullOrEmpty(localPath))
            {
                System.Diagnostics.Debug.WriteLine($"[Upx8 API] 图片已缓存到: {localPath}");
            }
        }

        /// <summary>
        /// 获取图片分辨率
        /// </summary>
        private async Task<Size> GetImageResolutionAsync(string imageUrl)
        {
            try
            {
                var imageBytes = await _httpClient.GetByteArrayAsync(imageUrl);
                using (var stream = new MemoryStream(imageBytes))
                {
                    using (var randomAccessStream = stream.AsRandomAccessStream())
                    {
                        var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
                        return new Size((int)decoder.PixelWidth, (int)decoder.PixelHeight);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Upx8 API] 获取图片分辨率失败: {ex.Message}");
                return new Size(0, 0);
            }
        }
    }
}
