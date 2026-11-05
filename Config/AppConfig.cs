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

        /// <summary>
        /// Pixabay 免费 API Key。留空则该源自动跳过，不影响其它源。
        /// 申请地址：https://pixabay.com/api/docs/ （注册即得，免费）
        /// </summary>
        public const string PixabayApiKey = "";

        /// <summary>
        /// Wikimedia Commons 要求带可识别的 User-Agent，否则会被限流。
        /// </summary>
        public const string WikimediaUserAgent = "XiaoKWallpaper/2.1 (https://github.com/iue2002/wallpaper-app)";

        // ===== 源开关 =====
        // 代码全部保留，只是默认不参与调度。某个源修好了，把对应开关改回 true 即可。

        /// <summary>
        /// Dmoe（www.dmoe.cc）。2026-11 实测：连续采样 3 张全部是 1920×1080，
        /// 永远达不到 2560×1440 的门槛，会被质量规则 100% 拦掉。
        /// 更糟的是它很慢（约 67 KB/s，单张 1 MB 要十几秒），拉 8 张就是白白拖住整个批次。
        /// 如果将来把质量门槛放宽到 1080p，这个源可以重新打开。
        /// </summary>
        public const bool EnableDmoe = false;

        /// <summary>
        /// PAAA（api.paaa.cn）。2026-11 实测：接口返回 200 但响应体 0 字节，
        /// 拿不到任何图片。而且因为不抛异常，熔断机制不会触发，每批都要白等 1.8~2.6 秒。
        /// </summary>
        public const bool EnablePaaa = false;

        /// <summary>
        /// Seovx。2026-11 实测双重故障：
        /// cdn.seovx.com 证书 CN 是 www.seovx.com（主机名不匹配，TLS 直接失败）；
        /// www.seovx.com 返回 500。两个地址都取不到图。
        /// </summary>
        public const bool EnableSeovx = false;

        // ===== 并发与资源控制 =====
        /// <summary>
        /// 同时在跑的「源请求」上限。源越多越要限，否则会打满连接池和带宽。
        /// 元数据请求很轻（几 KB JSON），真正的重活是后续图片下载与解码。
        /// </summary>
        public const int MaxConcurrentSourceRequests = 4;

        /// <summary>
        /// 某个源连续失败达到该次数后进入冷却，冷却期内不再请求。
        /// </summary>
        public const int SourceFailureThreshold = 3;

        /// <summary>
        /// 源熔断冷却时长（秒）。冷却结束后自动恢复一次试探。
        /// </summary>
        public const int SourceCooldownSeconds = 120;
        
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
