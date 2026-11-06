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
                        // 老版本只有一个"任务栏透明"开关，读过老配置就迁成"透明"档
                        if (settings.TaskbarEffect == 0 && settings.IsTaskbarBlurEnabled)
                        {
                            settings.TaskbarEffect = 1;
                        }

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
        
        // 任务栏透明设置
        // IsTaskbarBlurEnabled 是旧字段，只用于把老的"开/关"迁移到 TaskbarEffect
        public bool IsTaskbarBlurEnabled { get; set; } = false;

        // 0=关闭 1=透明 2=亚克力，对应 App1.Services.Taskbar.TaskbarEffect
        public int TaskbarEffect { get; set; } = 0;

        // 任务栏着色的不透明度，0-255（滑块上是 0-100%）。
        // 透明默认 0（完全透明），亚克力默认 64（25% 黑），偏了随时用菜单里的滑块调。
        public int TaskbarClearAlpha { get; set; } = 0;
        public int TaskbarAcrylicAlpha { get; set; } = 64;
    }
}