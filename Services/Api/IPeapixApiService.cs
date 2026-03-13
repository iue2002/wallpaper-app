using System.Collections.Generic;
using System.Threading.Tasks;
using App1.Models;

namespace App1.Services.Api
{
    /// <summary>
    /// Peapix API服务接口
    /// </summary>
    public interface IPeapixApiService
    {
        /// <summary>
        /// 获取Spotlight壁纸
        /// </summary>
        Task<List<Wallpaper>> GetSpotlightWallpapersAsync();
        
        /// <summary>
        /// 获取Bing中国壁纸
        /// </summary>
        Task<List<Wallpaper>> GetBingWallpapersAsync();
        
        /// <summary>
        /// 下载图片到本地
        /// </summary>
        /// <param name="imageUrl">图片URL</param>
        /// <param name="localPath">本地保存路径</param>
        /// <returns>本地文件路径</returns>
        Task<string> DownloadImageAsync(string imageUrl, string localPath);
    }
}
