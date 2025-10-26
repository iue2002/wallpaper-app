namespace App1.Config
{
    /// <summary>
    /// 应用配置 - 针对中国用户优化
    /// </summary>
    public static class AppConfig
    {
        // ===== 应用信息 =====
        public const string AppName = "小K壁纸";
        public const string AppNameEn = "Wallpaper Genie";
        public const string Version = "1.0.0";
        
        // ===== 固定配置（针对中国用户） =====
        public const string Country = "cn";              // 固定：中国
        public const string Language = "zh-CN";          // 固定：简体中文
        
        // ===== API配置 =====
        public const string PeapixBaseUrl = "https://peapix.com/";
        public const int BingImageCount = 30;            // Bing中国壁纸数量
        public const int SpotlightImageCount = 20;       // Spotlight壁纸数量
        
        // ===== 缓存配置 =====
        public const long MaxCacheSizeMB = 500;          // 最大缓存500MB
        public const int MaxCachedImages = 100;          // 最多缓存100张
        public const int ThumbnailWidth = 400;           // 缩略图宽度400px
        public const int ThumbnailHeight = 225;          // 缩略图高度225px (16:9)
        
        // ===== 数据库限制配置 =====
        public const int MaxWallpapersPerSource = 100;   // 每个来源最多保留100张
        public const int MaxTotalWallpapers = 200;       // 数据库总共最多200张
        public const bool AutoCleanEnabled = true;       // 启用自动清理
        
        // ===== 自动更新配置 =====
        public const int UpdateIntervalHours = 24;       // 24小时更新一次
        
        // ===== 超时配置 =====
        public const int HttpTimeoutSeconds = 30;        // HTTP请求超时30秒
        public const int DownloadTimeoutSeconds = 60;    // 下载图片超时60秒
    }
}
