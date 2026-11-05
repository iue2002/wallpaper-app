namespace App1.Services.Quality
{
    /// <summary>
    /// 壁纸质量硬规则（全项目唯一标准，所有壁纸源共用）
    ///
    /// 要求：只保留横向高分辨率图片。
    ///   - 必须横向（宽 > 高），竖屏一律不要
    ///   - 宽 >= 2560 且 高 >= 1440，任一不达标即丢弃
    ///
    /// 注意：这里只做判断，不涉及任何下载。宽高两个整数足以决定去留。
    /// </summary>
    public static class WallpaperQualityRule
    {
        /// <summary>最低宽度（像素）</summary>
        public const int MinWidth = 2560;

        /// <summary>最低高度（像素）</summary>
        public const int MinHeight = 1440;

        /// <summary>
        /// 判断一张图是否达到壁纸标准。
        /// 宽高任一为 0 或负数（通常意味着探测失败）时判为不合格。
        /// </summary>
        public static bool IsAcceptable(int width, int height)
        {
            if (width < MinWidth) return false;
            if (height < MinHeight) return false;
            if (width <= height) return false;
            return true;
        }

        /// <summary>
        /// 生成用于日志/标题的尺寸描述
        /// </summary>
        public static string Describe(int width, int height)
        {
            return $"{width}x{height}";
        }

        /// <summary>
        /// 说明不合格的具体原因，用于日志排查
        /// </summary>
        public static string Explain(int width, int height)
        {
            if (width <= 0 || height <= 0) return "读不到尺寸";
            if (width <= height) return "竖图";
            if (width < MinWidth) return "宽度不足";
            if (height < MinHeight) return "高度不足";
            return "合格";
        }
    }
}
