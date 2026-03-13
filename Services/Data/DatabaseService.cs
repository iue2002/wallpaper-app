using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace App1.Services.Data
{
    /// <summary>
    /// 数据库服务
    /// </summary>
    public class DatabaseService
    {
        private readonly string _dbPath;
        private SqliteConnection? _connection;
        
        public DatabaseService()
        {
            // Unpackaged 模式：使用 LocalApplicationData 文件夹
            var localFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "小K壁纸");
            
            // 确保文件夹存在
            Directory.CreateDirectory(localFolder);
            
            _dbPath = Path.Combine(localFolder, "wallpapers.db");
        }
        
        /// <summary>
        /// 初始化数据库
        /// </summary>
        public async Task InitializeAsync()
        {
            _connection = new SqliteConnection($"Data Source={_dbPath}");
            await _connection.OpenAsync();
            
            await CreateTablesAsync();
        }
        
        /// <summary>
        /// 创建表结构
        /// </summary>
        private async Task CreateTablesAsync()
        {
            var createTableSql = @"
                -- 壁纸表
                CREATE TABLE IF NOT EXISTS Wallpapers (
                    Id TEXT PRIMARY KEY,
                    Title TEXT,
                    Copyright TEXT,
                    Source TEXT NOT NULL,
                    OriginalUrl TEXT,
                    FullUrl TEXT NOT NULL UNIQUE,
                    ThumbUrl TEXT,
                    LocalPath TEXT,
                    LocalThumbPath TEXT,
                    IsDownloaded INTEGER DEFAULT 0,
                    IsFavorite INTEGER DEFAULT 0,
                    AddedDate TEXT NOT NULL,
                    ViewCount INTEGER DEFAULT 0
                );
                
                -- 创建索引
                CREATE INDEX IF NOT EXISTS idx_source ON Wallpapers(Source);
                CREATE INDEX IF NOT EXISTS idx_favorite ON Wallpapers(IsFavorite);
                CREATE INDEX IF NOT EXISTS idx_added_date ON Wallpapers(AddedDate DESC);
                CREATE UNIQUE INDEX IF NOT EXISTS idx_full_url ON Wallpapers(FullUrl);
                
                -- 应用设置表
                CREATE TABLE IF NOT EXISTS AppSettings (
                    Key TEXT PRIMARY KEY,
                    Value TEXT,
                    UpdatedDate TEXT
                );
            ";
            
            using var command = _connection!.CreateCommand();
            command.CommandText = createTableSql;
            await command.ExecuteNonQueryAsync();
        }
        
        /// <summary>
        /// 获取数据库连接
        /// </summary>
        public SqliteConnection GetConnection()
        {
            if (_connection == null)
            {
                throw new InvalidOperationException("数据库未初始化，请先调用 InitializeAsync()");
            }
            return _connection;
        }
        
        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            _connection?.Dispose();
        }
    }
}
