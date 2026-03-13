using System;
using System.IO;
using System.Text.Json;

namespace App1.Config
{
    /// <summary>
    /// 设置管理器 - 负责读写配置文件
    /// </summary>
    public static class SettingsManager
    {
        private static readonly string SettingsFilePath;
        
        static SettingsManager()
        {
            // 初始化配置文件路径
            var appDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppConfig.AppName
            );
            
            if (!Directory.Exists(appDataFolder))
            {
                Directory.CreateDirectory(appDataFolder);
            }
            
            SettingsFilePath = Path.Combine(appDataFolder, "settings.json");
        }
        
        /// <summary>
        /// 保存设置
        /// </summary>
        public static void SaveSettings(AppSettings settings)
        {
            try
            {
                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                
                File.WriteAllText(SettingsFilePath, json);
                
                System.Diagnostics.Debug.WriteLine("[设置] 配置已保存");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[设置] 保存失败: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 加载设置
        /// </summary>
        public static AppSettings LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    var json = File.ReadAllText(SettingsFilePath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    
                    if (settings != null)
                    {
                        System.Diagnostics.Debug.WriteLine("[设置] 配置已加载");
                        return settings;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[设置] 加载失败: {ex.Message}");
            }
            
            // 返回默认设置
            return new AppSettings();
        }
    }
    
    /// <summary>
    /// 应用设置
    /// </summary>
    public class AppSettings
    {
        // 定时设置
        public bool IsDesktopTimerEnabled { get; set; } = false;
        public double DesktopTimerInterval { get; set; } = 60; // 默认1小时
        
        public bool IsLockScreenTimerEnabled { get; set; } = false;
        public double LockScreenTimerInterval { get; set; } = 60; // 默认1小时
        
        // 开机自启设置
        public bool IsStartupEnabled { get; set; } = false;
    }
}