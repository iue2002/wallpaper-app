namespace App1.Models
{
    /// <summary>
    /// 壁纸来源
    /// </summary>
    public enum WallpaperSource
    {
        /// <summary>
        /// Bing中国每日壁纸
        /// </summary>
        Bing,
        
        /// <summary>
        /// 360壁纸
        /// </summary>
        Qihu360,
        
        /// <summary>
        /// Pexels高质量摄影壁纸
        /// </summary>
        Pexels,
        
        /// <summary>
        /// Unsplash专业摄影壁纸
        /// </summary>
        Unsplash,
        
        /// <summary>
        /// Dmoe樱花随机二次元壁纸
        /// </summary>
        Dmoe,
        
        /// <summary>
        /// PAAA随机壁纸
        /// </summary>
        Paaa,
        
        /// <summary>
        /// Upx8壁纸
        /// </summary>
        Upx8,
        
        /// <summary>
        /// Seovx壁纸
        /// </summary>
        Seovx,
        
        /// <summary>
        /// Windows系统内置壁纸
        /// </summary>
        System,
        
        /// <summary>
        /// 用户收藏
        /// </summary>
        Favorite,

        // ===== 以下为新增源，编号从 10 起，保证已有数据库记录的 Source 值不变 =====

        /// <summary>
        /// Pixabay 免费图库（需 API Key，人工审核，支持服务端尺寸/方向筛选）
        /// </summary>
        Pixabay = 10,

        /// <summary>
        /// Lorem Picsum 随机摄影图（Unsplash 授权，免 Key）
        /// </summary>
        Picsum = 11,

        /// <summary>
        /// Wikimedia Commons 维基共享资源（CC/公有领域，免 Key，支持服务端尺寸筛选）
        /// </summary>
        Wikimedia = 12
    }
}
