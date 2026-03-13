using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using App1.Config;
using App1.Models;
using App1.Services.Api;
using App1.Services.Cache;
using App1.Services.Data;

namespace App1.Services
{
    /// <summary>
    /// 智能壁纸管理服务（整合API、数据库、缓存）
    /// </summary>
    public class WallpaperManager
    {
        private readonly IPeapixApiService _apiService;
        private readonly WallpaperRepository _repository;
        private readonly CacheService _cacheService;
        
        public WallpaperManager(
            IPeapixApiService apiService,
            WallpaperRepository repository,
            CacheService cacheService)
        {
            _apiService = apiService;
            _repository = repository;
            _cacheService = cacheService;
        }
        
        /// <summary>
        /// 智能获取壁纸（自动去重和刷新）
        /// </summary>
        public async Task<WallpaperResult> GetWallpapersSmartAsync(WallpaperSource source)
        {
            var result = new WallpaperResult();
            
            try
            {
                System.Diagnostics.Debug.WriteLine($"\n[管理器] ========== 开始获取 {source} 壁纸 ==========");
                
                // 1. 检查是否需要刷新
                var needsRefresh = await ShouldRefreshAsync(source);
                System.Diagnostics.Debug.WriteLine($"[管理器] 需要刷新: {needsRefresh}");
                
                if (needsRefresh)
                {
                    // 2. 从API获取新壁纸
                    System.Diagnostics.Debug.WriteLine($"[管理器] 正在从API获取壁纸...");
                    var newWallpapers = await FetchFromApiAsync(source);
                    result.FetchedCount = newWallpapers.Count;
                    System.Diagnostics.Debug.WriteLine($"[管理器] API返回: {result.FetchedCount} 张");
                    
                    // 3. 去重：过滤掉已存在的URL
                    System.Diagnostics.Debug.WriteLine($"[管理器] 开始去重...");
                    var uniqueWallpapers = await FilterDuplicatesAsync(newWallpapers, source);
                    result.NewCount = uniqueWallpapers.Count;
                    result.DuplicateCount = newWallpapers.Count - uniqueWallpapers.Count;
                    System.Diagnostics.Debug.WriteLine($"[管理器] 去重结果: 新增 {result.NewCount} 张, 重复 {result.DuplicateCount} 张");
                    
                    // 4. 保存到数据库
                    System.Diagnostics.Debug.WriteLine($"[管理器] 保存到数据库...");
                    var savedCount = await _repository.SaveWallpapersAsync(uniqueWallpapers);
                    result.SavedCount = savedCount;
                    System.Diagnostics.Debug.WriteLine($"[管理器] 成功保存: {savedCount} 张");
                    
                    // 5. 自动清理旧壁纸（如果启用）
                    if (AppConfig.AutoCleanEnabled)
                    {
                        await AutoCleanOldWallpapersAsync(source);
                    }
                    
                    result.IsRefreshed = true;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[管理器] 使用缓存数据，无需刷新");
                }
                
                // 5. 从数据库读取壁纸
                System.Diagnostics.Debug.WriteLine($"[管理器] 从数据库读取...");
                result.Wallpapers = await _repository.GetWallpapersAsync(source);
                result.TotalCount = result.Wallpapers.Count;
                System.Diagnostics.Debug.WriteLine($"[管理器] 数据库总数: {result.TotalCount} 张");
                
                result.Success = true;
                System.Diagnostics.Debug.WriteLine($"[管理器] ========== 完成 ==========\n");
                
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                System.Diagnostics.Debug.WriteLine($"[管理器] ❌ 错误: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[管理器] 异常详情: {ex}");
                return result;
            }
        }
        
        /// <summary>
        /// 判断是否需要刷新
        /// </summary>
        private async Task<bool> ShouldRefreshAsync(WallpaperSource source)
        {
            var lastUpdate = await _repository.GetLastUpdateTimeAsync(source);
            var elapsed = DateTime.Now - lastUpdate;
            
            // Bing每天更新一次，Spotlight每周更新一次
            var threshold = source == WallpaperSource.Bing 
                ? TimeSpan.FromHours(AppConfig.UpdateIntervalHours)   // Bing：24小时
                : TimeSpan.FromDays(7);                               // Spotlight：7天
            
            return elapsed > threshold;
        }
        
        /// <summary>
        /// 从API获取壁纸
        /// </summary>
        private async Task<List<Wallpaper>> FetchFromApiAsync(WallpaperSource source)
        {
            return source switch
            {
                WallpaperSource.Bing => await _apiService.GetBingWallpapersAsync(),
                _ => new List<Wallpaper>()
            };
        }
        
        /// <summary>
        /// 过滤重复的壁纸
        /// </summary>
        private async Task<List<Wallpaper>> FilterDuplicatesAsync(
            List<Wallpaper> wallpapers, 
            WallpaperSource source)
        {
            // 获取已存在的URL
            var existingUrls = await _repository.GetExistingUrlsAsync(source);
            
            // 过滤掉重复的
            return wallpapers
                .Where(w => !existingUrls.Contains(w.FullUrl))
                .ToList();
        }
        
        /// <summary>
        /// 强制刷新（手动刷新）
        /// </summary>
        public async Task<WallpaperResult> ForceRefreshAsync(WallpaperSource source)
        {
            var result = new WallpaperResult();
            
            try
            {
                // 直接从API获取
                var newWallpapers = await FetchFromApiAsync(source);
                result.FetchedCount = newWallpapers.Count;
                
                // 去重
                var uniqueWallpapers = await FilterDuplicatesAsync(newWallpapers, source);
                result.NewCount = uniqueWallpapers.Count;
                result.DuplicateCount = newWallpapers.Count - uniqueWallpapers.Count;
                
                // 保存
                var savedCount = await _repository.SaveWallpapersAsync(uniqueWallpapers);
                result.SavedCount = savedCount;
                
                // 读取全部
                result.Wallpapers = await _repository.GetWallpapersAsync(source);
                result.TotalCount = result.Wallpapers.Count;
                result.IsRefreshed = true;
                result.Success = true;
                
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                return result;
            }
        }
        
        /// <summary>
        /// 获取缓存统计信息
        /// </summary>
        public CacheStats GetCacheStats()
        {
            return new CacheStats
            {
                CacheSizeMB = _cacheService.GetCacheSizeMB(),
                CachedImageCount = _cacheService.GetCachedImageCount(),
                MaxCacheSizeMB = AppConfig.MaxCacheSizeMB,
                CachePath = _cacheService.GetCacheRoot()
            };
        }
        
        /// <summary>
        /// 清理缓存
        /// </summary>
        public async Task ClearCacheAsync()
        {
            await _cacheService.ClearAllCacheAsync();
        }
        
        /// <summary>
        /// 自动清理旧壁纸
        /// </summary>
        private async Task AutoCleanOldWallpapersAsync(WallpaperSource source)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"\n[清理] ========== 开始自动清理 {source} ==========");
                
                // 1. 清理该来源超出限制的壁纸
                var cleanedCount = await _repository.CleanOldWallpapersAsync(
                    source, 
                    AppConfig.MaxWallpapersPerSource
                );
                
                if (cleanedCount > 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[清理] {source}来源已清理 {cleanedCount} 张旧壁纸");
                }
                
                // 2. 检查总数是否超限
                var totalCount = await _repository.GetCountAsync();
                if (totalCount > AppConfig.MaxTotalWallpapers)
                {
                    var excessCount = await _repository.CleanExcessWallpapersAsync(
                        AppConfig.MaxTotalWallpapers
                    );
                    
                    if (excessCount > 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"[清理] 已清理 {excessCount} 张超出总数限制的壁纸");
                    }
                }
                
                System.Diagnostics.Debug.WriteLine($"[清理] ========== 清理完成 ==========\n");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[清理] 自动清理失败: {ex.Message}");
            }
        }
    }
    
    /// <summary>
    /// 壁纸获取结果
    /// </summary>
    public class WallpaperResult
    {
        public bool Success { get; set; }
        public bool IsRefreshed { get; set; }
        public int FetchedCount { get; set; }      // API返回的数量
        public int NewCount { get; set; }          // 新壁纸数量
        public int DuplicateCount { get; set; }    // 重复数量
        public int SavedCount { get; set; }        // 保存成功数量
        public int TotalCount { get; set; }        // 数据库总数
        public List<Wallpaper> Wallpapers { get; set; } = new();
        public string? ErrorMessage { get; set; }
    }
    
    /// <summary>
    /// 缓存统计信息
    /// </summary>
    public class CacheStats
    {
        public long CacheSizeMB { get; set; }
        public int CachedImageCount { get; set; }
        public long MaxCacheSizeMB { get; set; }
        public string CachePath { get; set; } = string.Empty;
    }
}
