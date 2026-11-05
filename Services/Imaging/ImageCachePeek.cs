using System;
using System.IO;

namespace App1.Services.Imaging
{
    /// <summary>
    /// 本地图片缓存查询工具。
    ///
    /// 背景：Dmoe / Paaa / Seovx / Upx8 这四个随机直链源，原来在「拉列表」阶段就把
    /// 每张候选图整张下载下来（一张 3~5 MB，一批 6~8 张）。但列表卡片只要 280×200 的
    /// 缩略图，用户不点设置就永远不会用到原图 —— 等于每次刷新都先白烧几十 MB 流量，
    /// 首屏自然慢。
    ///
    /// 现在改成：列表阶段只查本地有没有现成的缓存文件，有就复用，没有就留空，
    /// 等用户真正点「设为壁纸」时再下载（WallpaperService 自带缓存与进度提示）。
    /// </summary>
    public static class ImageCachePeek
    {
        /// <summary>
        /// 只查不下载。命中返回本地路径，未命中返回空字符串。
        /// </summary>
        public static string FindExisting(string cacheFolder, string filePrefix, string imageUrl)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(cacheFolder) || string.IsNullOrWhiteSpace(imageUrl))
                {
                    return string.Empty;
                }

                var path = Path.Combine(cacheFolder, BuildFileName(filePrefix, imageUrl));
                return File.Exists(path) ? path : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 与原下载逻辑保持同一套文件名规则，老缓存不会失效。
        /// </summary>
        public static string BuildFileName(string filePrefix, string imageUrl)
        {
            // 注意：string.GetHashCode 在 .NET Core 每个进程会加随机种子，
            // 所以「重启后算出来的文件名」和「上次缓存的文件名」对不上。
            // 这里改用稳定哈希（FNV-1a），保证跨进程一致，缓存才真的能复用。
            return $"{filePrefix}_{StableHash(imageUrl)}.jpg";
        }

        /// <summary>FNV-1a 32 位稳定哈希，纯本地计算，跨进程结果一致。</summary>
        private static uint StableHash(string text)
        {
            const uint offsetBasis = 2166136261;
            const uint prime = 16777619;

            var hash = offsetBasis;
            foreach (var ch in text)
            {
                hash ^= ch;
                hash *= prime;
            }

            return hash;
        }
    }
}
