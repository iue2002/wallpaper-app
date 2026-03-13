using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using App1.Models;

namespace App1.Services.Data
{
    /// <summary>
    /// 壁纸数据仓储
    /// </summary>
    public class WallpaperRepository
    {
        private readonly DatabaseService _databaseService;
        
        public WallpaperRepository(DatabaseService databaseService)
        {
            _databaseService = databaseService;
        }
        
        /// <summary>
        /// 保存壁纸（自动去重）
        /// </summary>
        public async Task<bool> SaveWallpaperAsync(Wallpaper wallpaper)
        {
            try
            {
                var connection = _databaseService.GetConnection();
                using var command = connection.CreateCommand();
                
                command.CommandText = @"
                    INSERT OR IGNORE INTO Wallpapers 
                    (Id, Title, Copyright, Source, OriginalUrl, FullUrl, ThumbUrl, 
                     LocalPath, LocalThumbPath, IsDownloaded, IsFavorite, AddedDate, ViewCount)
                    VALUES 
                    (@id, @title, @copyright, @source, @originalUrl, @fullUrl, @thumbUrl,
                     @localPath, @localThumbPath, @isDownloaded, @isFavorite, @addedDate, @viewCount)
                ";
                
                command.Parameters.AddWithValue("@id", wallpaper.Id);
                command.Parameters.AddWithValue("@title", wallpaper.Title ?? string.Empty);
                command.Parameters.AddWithValue("@copyright", wallpaper.Copyright ?? string.Empty);
                command.Parameters.AddWithValue("@source", wallpaper.Source.ToString());
                command.Parameters.AddWithValue("@originalUrl", wallpaper.OriginalUrl ?? string.Empty);
                command.Parameters.AddWithValue("@fullUrl", wallpaper.FullUrl);
                command.Parameters.AddWithValue("@thumbUrl", wallpaper.ThumbUrl ?? string.Empty);
                command.Parameters.AddWithValue("@localPath", wallpaper.LocalPath ?? string.Empty);
                command.Parameters.AddWithValue("@localThumbPath", wallpaper.LocalThumbPath ?? string.Empty);
                command.Parameters.AddWithValue("@isDownloaded", wallpaper.IsDownloaded ? 1 : 0);
                command.Parameters.AddWithValue("@isFavorite", wallpaper.IsFavorite ? 1 : 0);
                command.Parameters.AddWithValue("@addedDate", wallpaper.AddedDate.ToString("O"));
                command.Parameters.AddWithValue("@viewCount", wallpaper.ViewCount);
                
                var result = await command.ExecuteNonQueryAsync();
                return result > 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存壁纸失败: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// 批量保存壁纸（自动去重）
        /// </summary>
        public async Task<int> SaveWallpapersAsync(List<Wallpaper> wallpapers)
        {
            int savedCount = 0;
            foreach (var wallpaper in wallpapers)
            {
                if (await SaveWallpaperAsync(wallpaper))
                {
                    savedCount++;
                }
            }
            return savedCount;
        }
        
        /// <summary>
        /// 获取已存在的URL列表（用于去重）
        /// </summary>
        public async Task<HashSet<string>> GetExistingUrlsAsync(WallpaperSource? source = null)
        {
            var urls = new HashSet<string>();
            
            try
            {
                var connection = _databaseService.GetConnection();
                using var command = connection.CreateCommand();
                
                if (source.HasValue)
                {
                    command.CommandText = "SELECT FullUrl FROM Wallpapers WHERE Source = @source";
                    command.Parameters.AddWithValue("@source", source.Value.ToString());
                }
                else
                {
                    command.CommandText = "SELECT FullUrl FROM Wallpapers";
                }
                
                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    urls.Add(reader.GetString(0));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"获取URL列表失败: {ex.Message}");
            }
            
            return urls;
        }
        
        /// <summary>
        /// 获取壁纸列表
        /// </summary>
        public async Task<List<Wallpaper>> GetWallpapersAsync(
            WallpaperSource? source = null,
            bool? isFavorite = null,
            int limit = 100,
            int offset = 0)
        {
            var wallpapers = new List<Wallpaper>();
            
            try
            {
                var connection = _databaseService.GetConnection();
                using var command = connection.CreateCommand();
                
                var whereClauses = new List<string>();
                
                if (source.HasValue)
                {
                    whereClauses.Add("Source = @source");
                    command.Parameters.AddWithValue("@source", source.Value.ToString());
                }
                
                if (isFavorite.HasValue)
                {
                    whereClauses.Add("IsFavorite = @isFavorite");
                    command.Parameters.AddWithValue("@isFavorite", isFavorite.Value ? 1 : 0);
                }
                
                var whereClause = whereClauses.Any() 
                    ? "WHERE " + string.Join(" AND ", whereClauses)
                    : "";
                
                command.CommandText = $@"
                    SELECT Id, Title, Copyright, Source, OriginalUrl, FullUrl, ThumbUrl,
                           LocalPath, LocalThumbPath, IsDownloaded, IsFavorite, AddedDate, ViewCount
                    FROM Wallpapers
                    {whereClause}
                    ORDER BY AddedDate DESC
                    LIMIT @limit OFFSET @offset
                ";
                
                command.Parameters.AddWithValue("@limit", limit);
                command.Parameters.AddWithValue("@offset", offset);
                
                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    wallpapers.Add(new Wallpaper
                    {
                        Id = reader.GetString(0),
                        Title = reader.GetString(1),
                        Copyright = reader.GetString(2),
                        Source = Enum.Parse<WallpaperSource>(reader.GetString(3)),
                        OriginalUrl = reader.GetString(4),
                        FullUrl = reader.GetString(5),
                        ThumbUrl = reader.GetString(6),
                        LocalPath = reader.IsDBNull(7) ? null : reader.GetString(7),
                        LocalThumbPath = reader.IsDBNull(8) ? null : reader.GetString(8),
                        IsDownloaded = reader.GetInt32(9) == 1,
                        IsFavorite = reader.GetInt32(10) == 1,
                        AddedDate = DateTime.Parse(reader.GetString(11)),
                        ViewCount = reader.GetInt32(12)
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"获取壁纸列表失败: {ex.Message}");
            }
            
            return wallpapers;
        }
        
        /// <summary>
        /// 获取壁纸总数
        /// </summary>
        public async Task<int> GetCountAsync(WallpaperSource? source = null)
        {
            try
            {
                var connection = _databaseService.GetConnection();
                using var command = connection.CreateCommand();
                
                if (source.HasValue)
                {
                    command.CommandText = "SELECT COUNT(*) FROM Wallpapers WHERE Source = @source";
                    command.Parameters.AddWithValue("@source", source.Value.ToString());
                }
                else
                {
                    command.CommandText = "SELECT COUNT(*) FROM Wallpapers";
                }
                
                var result = await command.ExecuteScalarAsync();
                return Convert.ToInt32(result);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"获取壁纸数量失败: {ex.Message}");
                return 0;
            }
        }
        
        /// <summary>
        /// 获取最后更新时间
        /// </summary>
        public async Task<DateTime> GetLastUpdateTimeAsync(WallpaperSource source)
        {
            try
            {
                var connection = _databaseService.GetConnection();
                using var command = connection.CreateCommand();
                
                command.CommandText = @"
                    SELECT MAX(AddedDate) 
                    FROM Wallpapers 
                    WHERE Source = @source
                ";
                command.Parameters.AddWithValue("@source", source.ToString());
                
                var result = await command.ExecuteScalarAsync();
                
                if (result != null && result != DBNull.Value)
                {
                    return DateTime.Parse(result.ToString()!);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"获取最后更新时间失败: {ex.Message}");
            }
            
            return DateTime.MinValue;
        }
        
        /// <summary>
        /// 切换收藏状态
        /// </summary>
        public async Task<bool> ToggleFavoriteAsync(string wallpaperId)
        {
            try
            {
                var connection = _databaseService.GetConnection();
                using var command = connection.CreateCommand();
                
                command.CommandText = @"
                    UPDATE Wallpapers 
                    SET IsFavorite = CASE WHEN IsFavorite = 1 THEN 0 ELSE 1 END
                    WHERE Id = @id
                ";
                command.Parameters.AddWithValue("@id", wallpaperId);
                
                var result = await command.ExecuteNonQueryAsync();
                return result > 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"切换收藏状态失败: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// 清理旧壁纸（保留最新的N张）
        /// </summary>
        public async Task<int> CleanOldWallpapersAsync(WallpaperSource source, int keepCount)
        {
            try
            {
                var connection = _databaseService.GetConnection();
                using var command = connection.CreateCommand();
                
                // 删除旧的壁纸，保留最新的N张和所有收藏的
                command.CommandText = $@"
                    DELETE FROM Wallpapers 
                    WHERE Id IN (
                        SELECT Id FROM Wallpapers
                        WHERE Source = @source AND IsFavorite = 0
                        ORDER BY AddedDate DESC
                        LIMIT -1 OFFSET @keepCount
                    )
                ";
                command.Parameters.AddWithValue("@source", source.ToString());
                command.Parameters.AddWithValue("@keepCount", keepCount);
                
                var deletedCount = await command.ExecuteNonQueryAsync();
                
                if (deletedCount > 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[清理] {source}来源清理了 {deletedCount} 张旧壁纸");
                }
                
                return deletedCount;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"清理旧壁纸失败: {ex.Message}");
                return 0;
            }
        }
        
        /// <summary>
        /// 清理超出总数限制的壁纸
        /// </summary>
        public async Task<int> CleanExcessWallpapersAsync(int maxTotal)
        {
            try
            {
                var connection = _databaseService.GetConnection();
                using var command = connection.CreateCommand();
                
                // 删除最旧的壁纸，保留收藏的
                command.CommandText = $@"
                    DELETE FROM Wallpapers 
                    WHERE Id IN (
                        SELECT Id FROM Wallpapers
                        WHERE IsFavorite = 0
                        ORDER BY AddedDate DESC
                        LIMIT -1 OFFSET @maxTotal
                    )
                ";
                command.Parameters.AddWithValue("@maxTotal", maxTotal);
                
                var deletedCount = await command.ExecuteNonQueryAsync();
                
                if (deletedCount > 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[清理] 清理了 {deletedCount} 张超出限制的壁纸");
                }
                
                return deletedCount;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"清理超出壁纸失败: {ex.Message}");
                return 0;
            }
        }
        
        /// <summary>
        /// 删除壁纸记录
        /// </summary>
        public async Task<bool> DeleteWallpaperAsync(string wallpaperId)
        {
            try
            {
                var connection = _databaseService.GetConnection();
                using var command = connection.CreateCommand();
                
                command.CommandText = "DELETE FROM Wallpapers WHERE Id = @id";
                command.Parameters.AddWithValue("@id", wallpaperId);
                
                var result = await command.ExecuteNonQueryAsync();
                return result > 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"删除壁纸失败: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// 清空指定来源的所有壁纸
        /// </summary>
        public async Task<int> ClearSourceAsync(WallpaperSource source)
        {
            try
            {
                var connection = _databaseService.GetConnection();
                using var command = connection.CreateCommand();
                
                command.CommandText = "DELETE FROM Wallpapers WHERE Source = @source";
                command.Parameters.AddWithValue("@source", source.ToString());
                
                var deletedCount = await command.ExecuteNonQueryAsync();
                System.Diagnostics.Debug.WriteLine($"[清空] 已删除 {source} 来源的 {deletedCount} 张壁纸");
                
                return deletedCount;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"清空来源失败: {ex.Message}");
                return 0;
            }
        }
    }
}
