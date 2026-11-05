using System;
using System.Diagnostics;
using System.IO;

namespace App1.Services.Diagnostics
{
    /// <summary>
    /// 应用日志。后台模式（只有托盘、没有窗口）出问题时没有界面可以看，
    /// 只能靠这个文件复盘。
    ///
    /// 位置：%LOCALAPPDATA%\小K壁纸\app.log
    /// 超过 1MB 自动清空，写入失败一律吞掉，绝不影响功能。
    /// </summary>
    public static class AppLog
    {
        private const long MaxBytes = 1024 * 1024;

        private static readonly object Gate = new();
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly string LogPath = BuildPath();

        public static string FilePath => LogPath;

        private static string BuildPath()
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "小K壁纸");

                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "app.log");
            }
            catch
            {
                return Path.Combine(Path.GetTempPath(), "xiaok-app.log");
            }
        }

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    var info = new FileInfo(LogPath);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        File.WriteAllText(LogPath, string.Empty);
                    }

                    File.AppendAllText(
                        LogPath,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} (+{Clock.ElapsedMilliseconds,7}ms) {message}\r\n");
                }
            }
            catch
            {
                // 日志本身不能影响功能
            }
        }
    }
}
