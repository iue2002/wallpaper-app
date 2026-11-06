using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using App1.Services.Diagnostics;

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
            // 打包（MSIX）版不能直接用 %LOCALAPPDATA%：
            // 系统会把写进去的文件重定向到 Packages\<包名>\LocalCache\Local\ 这个
            // 包私有视图，读写容易落到两份不同的文件上，用户就感觉「设置没记住」。
            // ApplicationData.Current.LocalFolder 指向 …\LocalState\，是真实路径，
            // 写入不重定向，卸载/更新也不会莫名其妙换位置。
            // 非打包版这个 WinRT API 会抛异常，回落到原来的 %LOCALAPPDATA%\小K壁纸\。
            string? folder = null;
            try
            {
                folder = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
            }
            catch
            {
                // unpackaged：走下面的回落
            }

            var legacyFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppConfig.AppName);

            if (folder == null)
            {
                folder = legacyFolder;
            }
            else
            {
                // 一次性迁移：把桌面版/旧打包版留在真实路径上的配置带过来，
                // 免得用户切到打包版之后突然「全变默认值」。
                var legacy = Path.Combine(legacyFolder, "settings.json");
                var target = Path.Combine(folder, "settings.json");
                if (!File.Exists(target) && File.Exists(legacy))
                {
                    try
                    {
                        File.Copy(legacy, target);
                        AppLog.Write("[设置] 已从桌面版迁移配置: " + legacy);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write("[设置] 迁移配置失败: " + ex.Message);
                    }
                }
            }

            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                AppLog.Write("[设置] 创建配置目录失败: " + ex.Message);
            }

            SettingsFilePath = Path.Combine(folder, "settings.json");
            AppLog.Write("[设置] 配置文件: " + SettingsFilePath);
        }

        /// <summary>保存设置</summary>
        public static void SaveSettings(AppSettings settings)
        {
            try
            {
                // 用编译期生成的 JsonTypeInfo，不走反射重载：
                // 裁剪/AOT 下也不会踩 IL2026，更不会因为运行时开关变化而失效。
                var json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);

                File.WriteAllText(SettingsFilePath, json);

                // 写进 app.log，Release 版也看得到。以前只有 Debug.WriteLine，
                // 保存到底成功没有、写了哪份文件，全靠猜。
                AppLog.Write($"[设置] 已保存 档位={settings.TaskbarEffect} " +
                             $"透明浓度={settings.TaskbarClearAlpha} 亚克力浓度={settings.TaskbarAcrylicAlpha}");
            }
            catch (Exception ex)
            {
                AppLog.Write("[设置] 保存失败: " + ex.Message);
            }
        }

        /// <summary>加载设置</summary>
        public static AppSettings LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    var json = File.ReadAllText(SettingsFilePath);
                    var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);

                    if (settings != null)
                    {
                        // 老版本只有一个"任务栏透明"开关，读过老配置就迁成"透明"档
                        if (settings.TaskbarEffect == 0 && settings.IsTaskbarBlurEnabled)
                        {
                            settings.TaskbarEffect = 1;
                        }

                        AppLog.Write($"[设置] 已加载 档位={settings.TaskbarEffect} " +
                                     $"透明浓度={settings.TaskbarClearAlpha} 亚克力浓度={settings.TaskbarAcrylicAlpha}");
                        return settings;
                    }
                }
                else
                {
                    AppLog.Write("[设置] 配置文件不存在，使用默认值");
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("[设置] 加载失败: " + ex.Message);
            }

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

    /// <summary>
    /// 设置的 JSON 源生成上下文：编译期生成类型元数据，不依赖运行时反射。
    /// 这是设置这条关键路径的保险 —— 就算以后再动裁剪/AOT 配置，
    /// 也不会出现「读不了也存不下、每次重启回默认值」。
    /// </summary>
    [JsonSerializable(typeof(AppSettings))]
    internal partial class AppSettingsJsonContext : JsonSerializerContext
    {
    }
}
