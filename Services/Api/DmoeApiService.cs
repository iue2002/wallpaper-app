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
    /// Dmoe壁纸API服务
    /// </summary>
    public class DmoeApiService
    {
        private readonly HttpClient _httpClient;
        private const string BASE_URL = "https://www.dmoe.cc/random.php";
        private readonly string _cacheFolder;

        public DmoeApiService()
        {
            var handler = new HttpClientHandler
            {
                UseProxy = false,
                Proxy = null
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            // 创建缓存文件夹
            var picturesFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            _cacheFolder = Path.Combine(picturesFolder, "小K壁纸", "Dmoe");
            if (!Directory.Exists(_cacheFolder))
            {
                Directory.CreateDirectory(_cacheFolder);
            }
        }

        /// <summary>
        /// 获取Dmoe壁纸
        /// </summary>
        public async Task<List<Wallpaper>> GetWallpapersAsync(int count = 1)
        {
            try
            {
                var wallpapers = new List<Wallpaper>();
                System.Diagnostics.Debug.WriteLine($"[Dmoe API] 获取 {count} 张壁纸...");

                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        // 添加时间戳防止缓存
                        var timestamp = DateTime.Now.Ticks;
                        var random = new Random().Next(1000000);
                        var url = $"{BASE_URL}?ts={timestamp}&r={random}";

                        // 发送请求获取响应
                        var response = await _httpClient.GetAsync(url);
                        response.EnsureSuccessStatusCode();

                        string imgUrl;
                        string title = "Dmoe 壁纸";

                        // 检查响应内容类型
                        var contentType = response.Content.Headers.ContentType?.MediaType;
                        System.Diagnostics.Debug.WriteLine($"[Dmoe API] 响应内容类型: {contentType}");

                        if (contentType != null && contentType.StartsWith("image/"))
                        {
                            // 直接返回图片
                            imgUrl = response.RequestMessage.RequestUri.ToString();
                            System.Diagnostics.Debug.WriteLine($"[Dmoe API] 直接获取图片URL: {imgUrl}");
                        }
                        else
                        {
                            // 尝试解析JSON
                            try
                            {
                                var json = await response.Content.ReadAsStringAsync();
                                var jsonDoc = JsonDocument.Parse(json);

                                // 解析JSON数据
                                var data = jsonDoc.RootElement;
                                imgUrl = data.GetProperty("imgurl").GetString();
                                title = data.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : "Dmoe 壁纸";
                                System.Diagnostics.Debug.WriteLine($"[Dmoe API] 从JSON获取图片URL: {imgUrl}");
                            }
                            catch
                            {
                                // JSON解析失败，尝试直接使用响应URL
                                imgUrl = response.RequestMessage.RequestUri.ToString();
                                System.Diagnostics.Debug.WriteLine($"[Dmoe API] JSON解析失败，使用响应URL: {imgUrl}");
                            }
                        }

                        if (!string.IsNullOrEmpty(imgUrl))
                        {
                            // 下载并缓存图片
                            var localPath = await DownloadAndCacheImageAsync(imgUrl);

                            // 检查图片分辨率，只保留2k以上的壁纸
                            var resolution = await GetImageResolutionAsync(imgUrl);
                            if (resolution.Width >= 2560 || resolution.Height >= 1440)
                            {
                                var wallpaper = new Wallpaper
                                {
                                    Id = Guid.NewGuid().ToString(),
                                    Title = $"{title} ({resolution.Width}x{resolution.Height})",
                                    Copyright = "来源: Dmoe",
                                    Source = WallpaperSource.Dmoe,
                                    OriginalUrl = imgUrl,
                                    FullUrl = imgUrl,
                                    ThumbUrl = imgUrl, // 直接使用原图作为缩略图
                                    LocalPath = localPath,
                                    IsDownloaded = !string.IsNullOrEmpty(localPath),
                                    AddedDate = DateTime.Now
                                };

                                wallpapers.Add(wallpaper);
                                System.Diagnostics.Debug.WriteLine($"[Dmoe API] 成功获取壁纸: {imgUrl} ({resolution.Width}x{resolution.Height})");
                                if (!string.IsNullOrEmpty(localPath))
                                {
                                    System.Diagnostics.Debug.WriteLine($"[Dmoe API] 图片已缓存到: {localPath}");
                                }
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine($"[Dmoe API] 跳过低分辨率壁纸: {imgUrl} ({resolution.Width}x{resolution.Height})");
                                // 如果已下载，删除低分辨率图片
                                if (!string.IsNullOrEmpty(localPath) && File.Exists(localPath))
                                {
                                    File.Delete(localPath);
                                    System.Diagnostics.Debug.WriteLine($"[Dmoe API] 删除低分辨率图片: {localPath}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Dmoe API] 获取单张壁纸失败: {ex.Message}");
                        continue;
                    }
                }

                return wallpapers;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Dmoe API] 获取壁纸失败: {ex.Message}");
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
                var fileName = $"dmoe_{urlHash}.jpg";
                var localPath = Path.Combine(_cacheFolder, fileName);

                // 检查是否已经缓存
                if (File.Exists(localPath))
                {
                    System.Diagnostics.Debug.WriteLine($"[Dmoe API] 使用缓存图片: {localPath}");
                    return localPath;
                }

                // 下载图片
                var imageBytes = await _httpClient.GetByteArrayAsync(imageUrl);
                await File.WriteAllBytesAsync(localPath, imageBytes);

                System.Diagnostics.Debug.WriteLine($"[Dmoe API] 图片下载完成: {imageBytes.Length / 1024} KB");
                System.Diagnostics.Debug.WriteLine($"[Dmoe API] 保存路径: {localPath}");

                return localPath;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Dmoe API] 下载图片失败: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// 获取图片分辨率
        /// </summary>
        private async Task<Size> GetImageResolutionAsync(string imageUrl)
        {
            try
            {
                // 下载图片数据
                var imageBytes = await _httpClient.GetByteArrayAsync(imageUrl);
                
                // 使用内存流读取图片
                using (var stream = new MemoryStream(imageBytes))
                {
                    using (var randomAccessStream = stream.AsRandomAccessStream())
                    {
                        // 解码图片
                        var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
                        return new Size((int)decoder.PixelWidth, (int)decoder.PixelHeight);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Dmoe API] 获取图片分辨率失败: {ex.Message}");
                // 如果获取失败，返回默认分辨率
                return new Size(0, 0);
            }
        }
    }
}