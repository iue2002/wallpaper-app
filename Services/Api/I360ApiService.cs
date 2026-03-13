using System.Collections.Generic;
using System.Threading.Tasks;
using App1.Models;

namespace App1.Services.Api
{
    /// <summary>
    /// 360壁纸API服务接口
    /// </summary>
    public interface I360ApiService
    {
        /// <summary>
        /// 获取360壁纸分类列表
        /// </summary>
        Task<List<WallpaperCategory>> GetCategoriesAsync();
        
        /// <summary>
        /// 获取指定分类的壁纸
        /// </summary>
        Task<List<Wallpaper>> GetWallpapersByCategoryAsync(string categoryId, int start = 0, int count = 20);
        
        /// <summary>
        /// 获取多个分类的壁纸
        /// </summary>
        Task<List<Wallpaper>> GetWallpapersAsync(int countPerCategory = 10);
    }
    
    /// <summary>
    /// 壁纸分类
    /// </summary>
    public class WallpaperCategory
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }
}
