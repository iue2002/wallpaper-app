using System;
using System.IO;
using System.Threading.Tasks;
using App1.Config;

namespace App1.Services.Cache
{
    /// <summary>
    /// 缓存服务
    /// </summary>
    public class CacheService
    {
        private readonly string _cacheRoot;
        private readonly string _imagesFolder;
        private readonly string _thumbnailsFolder;
        
        public CacheService()
        {
            // Unpackaged 模式：使用 LocalApplicationData/小K壁纸/Cache 文件夹
            _cacheRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "小K壁纸", "Cache");
            _imagesFolder = Path.Combine(_cacheRoot, "Images");
            _thumbnailsFolder = Path.Combine(_cacheRoot, "Thumbnails");
            
            EnsureDirectoriesExist();
        }
        
        /// <summary>
        /// 确保缓存目录存在
        /// </summary>
        private void EnsureDirectoriesExist()
        {
            Directory.CreateDirectory(_imagesFolder);
            Directory.CreateDirectory(_thumbnailsFolder);
        }
        
        /// <summary>
        /// 获取图片缓存路径
        /// </summary>
        public string GetImagePath(string imageId)
        {
            return Path.Combine(_imagesFolder, $"{imageId}.jpg");
        }
        
        /// <summary>
        /// 获取缩略图缓存路径
        /// </summary>
        public string GetThumbnailPath(string imageId)
        {
            return Path.Combine(_thumbnailsFolder, $"{imageId}_thumb.jpg");
        }
        
        /// <summary>
        /// 检查图片是否已缓存
        /// </summary>
        public bool IsImageCached(string imageId)
        {
            return File.Exists(GetImagePath(imageId));
        }
        
        /// <summary>
        /// 检查缩略图是否已缓存
        /// </summary>
        public bool IsThumbnailCached(string imageId)
        {
            return File.Exists(GetThumbnailPath(imageId));
        }
        
        /// <summary>
        /// 获取缓存大小（MB）
        /// </summary>
        public long GetCacheSizeMB()
        {
            long totalSize = 0;
            
            if (Directory.Exists(_imagesFolder))
            {
                var imageFiles = Directory.GetFiles(_imagesFolder);
                foreach (var file in imageFiles)
                {
                    totalSize += new FileInfo(file).Length;
                }
            }
            
            if (Directory.Exists(_thumbnailsFolder))
            {
                var thumbFiles = Directory.GetFiles(_thumbnailsFolder);
                foreach (var file in thumbFiles)
                {
                    totalSize += new FileInfo(file).Length;
                }
            }
            
            return totalSize / 1024 / 1024; // 转换为MB
        }
        
        /// <summary>
        /// 获取缓存文件数量
        /// </summary>
        public int GetCachedImageCount()
        {
            int count = 0;
            
            if (Directory.Exists(_imagesFolder))
            {
                count += Directory.GetFiles(_imagesFolder).Length;
            }
            
            return count;
        }
        
        /// <summary>
        /// 清理所有缓存
        /// </summary>
        public Task ClearAllCacheAsync()
        {
            return Task.Run(() =>
            {
                if (Directory.Exists(_imagesFolder))
                {
                    Directory.Delete(_imagesFolder, true);
                }
                
                if (Directory.Exists(_thumbnailsFolder))
                {
                    Directory.Delete(_thumbnailsFolder, true);
                }
                
                EnsureDirectoriesExist();
            });
        }
        
        /// <summary>
        /// 清理指定图片缓存
        /// </summary>
        public Task ClearImageCacheAsync(string imageId)
        {
            return Task.Run(() =>
            {
                var imagePath = GetImagePath(imageId);
                if (File.Exists(imagePath))
                {
                    File.Delete(imagePath);
                }
                
                var thumbPath = GetThumbnailPath(imageId);
                if (File.Exists(thumbPath))
                {
                    File.Delete(thumbPath);
                }
            });
        }
        
        /// <summary>
        /// 检查缓存是否超过限制
        /// </summary>
        public bool IsCacheFull()
        {
            var currentSize = GetCacheSizeMB();
            return currentSize >= AppConfig.MaxCacheSizeMB;
        }
        
        /// <summary>
        /// 获取缓存根目录
        /// </summary>
        public string GetCacheRoot()
        {
            return _cacheRoot;
        }
    }
}
