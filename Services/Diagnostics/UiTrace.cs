using System;
using System.Diagnostics;
using System.IO;

namespace App1.Services.Diagnostics
{
    /// <summary>
    /// 轻量 UI 追踪：只往文件里追加带毫秒时间戳的标记行。
    ///
    /// 用来定位"点一下界面就不动了"这类问题 —— 卡住之后看日志最后一行停在
    /// 哪一步，就知道该查哪儿，不用靠猜。写入失败一律吞掉，绝不影响功能。
    /// 日志位置：%LOCALAPPDATA%\小K壁纸\ui-trace.log
    /// </summary>
    public static class UiTrace
    {
        private const long MaxBytes = 512 * 1024;

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
                return Path.Combine(dir, "ui-trace.log");
            }
            catch
            {
                return Path.Combine(Path.GetTempPath(), "xiaok-ui-trace.log");
            }
        }

        public static void Write(string stage)
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
                        $"{DateTime.Now:HH:mm:ss.fff} (+{Clock.ElapsedMilliseconds,6}ms) {stage}\r\n");
                }
            }
            catch
            {
                // 诊断日志本身不能影响任何功能
            }
        }
    }
}
