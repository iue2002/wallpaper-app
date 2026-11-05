using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using App1.Models;
using App1.Services.Imaging;
using App1.Services.Quality;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace App1.Services.Api
{
    /// <summary>
    /// PAAA壁纸API服务
    /// </summary>
    public class PaaaApiService
    {
        private readonly HttpClient _httpClient;
        private const string BASE_API_URL = "https://api.paaa.cn/api/api.php";
        private const string BASE_GQ_API_URL = "https://api.paaa.cn/api/gqapi.php";
        private readonly string _cacheFolder;

        public PaaaApiService()
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
            _cacheFolder = Path.Combine(picturesFolder, "小K壁纸", "Paaa");
            if (!Directory.Exists(_cacheFolder))
            {
                Directory.CreateDirectory(_cacheFolder);
            }
        }

        /// <summary>
        /// 获取PAAA壁纸（二次元）
        /// </summary>
        public async Task<List<Wallpaper>> GetAnimeWallpapersAsync(int count = 1)
        {
            return await GetWallpapersAsync(BASE_API_URL, "PAAA 二次元壁纸", count);
        }

        /// <summary>
        /// 获取PAAA壁纸（高清）
        /// </summary>
        public async Task<List<Wallpaper>> GetHighQualityWallpapersAsync(int count = 1)
        {
            return await GetWallpapersAsync(BASE_GQ_API_URL, "PAAA 高清壁纸", count);
        }

        /// <summary>
        /// 统一获取PAAA壁纸的方法
        /// </summary>
        private async Task<List<Wallpaper>> GetWallpapersAsync(string baseUrl, string titlePrefix, int count)
        {
            try
            {
                var wallpapers = new List<Wallpaper>();
                System.Diagnostics.Debug.WriteLine($"[PAAA API] 获取 {count} 张{titlePrefix}...");

                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        // 添加时间戳防止缓存
                        var timestamp = DateTime.Now.Ticks;
                        var url = $"{baseUrl}?return=json&ts={timestamp}";

                        // 发送请求获取JSON数据
                        var response = await _httpClient.GetAsync(url);
                        response.EnsureSuccessStatusCode();

                        var json = await response.Content.ReadAsStringAsync();
                        var jsonDoc = JsonDocument.Parse(json);

                        // 解析JSON数据
                        var data = jsonDoc.RootElement;
                        var imgUrl = data.GetProperty("imgurl").GetString();
                        var title = data.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : titlePrefix;

                        if (!string.IsNullOrEmpty(imgUrl))
                        {
                            // 先只读文件头取尺寸（约 64KB），不合格立刻放弃，不再下载整图
                            var resolution = await GetImageResolutionAsync(imgUrl);
                            if (!WallpaperQualityRule.IsAcceptable(resolution.Width, resolution.Height))
                            {
                                System.Diagnostics.Debug.WriteLine($"[PAAA API] 跳过不合格壁纸: {imgUrl} ({resolution.Width}x{resolution.Height})");
                                continue;
                            }

                            // 尺寸达标后才下载并缓存
                            var localPath = await DownloadAndCacheImageAsync(imgUrl);

                            var wallpaper = new Wallpaper
                            {
                                Id = Guid.NewGuid().ToString(),
                                Title = $"{title} ({resolution.Width}x{resolution.Height})",
                                Copyright = "来源: PAAA",
                                Source = WallpaperSource.Paaa,
                                OriginalUrl = imgUrl,
                                FullUrl = imgUrl,
                                ThumbUrl = imgUrl, // 直接使用原图作为缩略图
                                LocalPath = localPath,
                                IsDownloaded = !string.IsNullOrEmpty(localPath),
                                AddedDate = DateTime.Now
                            };

                            wallpapers.Add(wallpaper);
                            System.Diagnostics.Debug.WriteLine($"[PAAA API] 成功获取壁纸: {imgUrl} ({resolution.Width}x{resolution.Height})");
                            if (!string.IsNullOrEmpty(localPath))
                            {
                                System.Diagnostics.Debug.WriteLine($"[PAAA API] 图片已缓存到: {localPath}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PAAA API] 获取单张壁纸失败: {ex.Message}");
                        continue;
                    }
                }

                return wallpapers;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PAAA API] 获取壁纸失败: {ex.Message}");
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
                var fileName = $"paaa_{urlHash}.jpg";
                var localPath = Path.Combine(_cacheFolder, fileName);

                // 检查是否已经缓存
                if (File.Exists(localPath))
                {
                    System.Diagnostics.Debug.WriteLine($"[PAAA API] 使用缓存图片: {localPath}");
                    return localPath;
                }

                // 下载图片
                var imageBytes = await _httpClient.GetByteArrayAsync(imageUrl);
                await File.WriteAllBytesAsync(localPath, imageBytes);

                System.Diagnostics.Debug.WriteLine($"[PAAA API] 图片下载完成: {imageBytes.Length / 1024} KB");
                System.Diagnostics.Debug.WriteLine($"[PAAA API] 保存路径: {localPath}");

                return localPath;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PAAA API] 下载图片失败: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// 获取PAAA壁纸（混合类型）
        /// </summary>
        public async Task<List<Wallpaper>> GetWallpapersAsync(int count = 1)
        {
            var wallpapers = new List<Wallpaper>();
            
            // 一半获取二次元壁纸，一半获取高清壁纸
            int animeCount = count / 2;
            int gqCount = count - animeCount;

            var animeWallpapers = await GetAnimeWallpapersAsync(animeCount);
            var gqWallpapers = await GetHighQualityWallpapersAsync(gqCount);

            wallpapers.AddRange(animeWallpapers);
            wallpapers.AddRange(gqWallpapers);

            return wallpapers;
        }

        /// <summary>
        /// 获取图片分辨率
        /// </summary>
        private async Task<Size> GetImageResolutionAsync(string imageUrl)
        {
            try
            {
                // 只读文件头（约 64KB）解析宽高，不再下载整张图片
                var probe = await ImageProbe.GetSizeAsync(_httpClient, imageUrl);
                return new Size(probe.Width, probe.Height);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PAAA API] 获取图片分辨率失败: {ex.Message}");
                // 如果获取失败，返回默认分辨率
                return new Size(0, 0);
            }
        }
    }
}