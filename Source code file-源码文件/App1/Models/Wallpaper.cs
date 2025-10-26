using System;

namespace App1.Models
{
    /// <summary>
    /// 壁纸数据模型
    /// </summary>
    public class Wallpaper
    {
        /// <summary>
        /// 唯一标识符
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();
        
        /// <summary>
        /// 标题
        /// </summary>
        public string Title { get; set; } = string.Empty;
        
        /// <summary>
        /// 版权信息
        /// </summary>
        public string Copyright { get; set; } = string.Empty;
        
        /// <summary>
        /// 来源
        /// </summary>
        public WallpaperSource Source { get; set; }
        
        /// <summary>
        /// 原始图片URL
        /// </summary>
        public string OriginalUrl { get; set; } = string.Empty;
        
        /// <summary>
        /// 高清图片URL
        /// </summary>
        public string FullUrl { get; set; } = string.Empty;
        
        /// <summary>
        /// 缩略图URL
        /// </summary>
        public string ThumbUrl { get; set; } = string.Empty;
        
        /// <summary>
        /// 本地高清图片路径
        /// </summary>
        public string? LocalPath { get; set; }
        
        /// <summary>
        /// 本地缩略图路径
        /// </summary>
        public string? LocalThumbPath { get; set; }
        
        /// <summary>
        /// 是否已下载
        /// </summary>
        public bool IsDownloaded { get; set; }
        
        /// <summary>
        /// 是否收藏
        /// </summary>
        public bool IsFavorite { get; set; }
        
        /// <summary>
        /// 添加日期
        /// </summary>
        public DateTime AddedDate { get; set; } = DateTime.Now;
        
        /// <summary>
        /// 查看次数
        /// </summary>
        public int ViewCount { get; set; }
    }
}
