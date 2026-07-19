using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using MiniPet.Models;

namespace MiniPet.Services
{
    /// <summary>
    /// 配置服务 - 设置持久化、读写、变更通知
    /// </summary>
    public class ConfigService
    {
        private readonly string _settingsPath;
        private readonly string _wzConfigPath;
        private PetSettings _settings;
        private WzConfig _wzConfig;
        private readonly object _lock = new();

        /// <summary>
        /// 设置变更事件
        /// </summary>
        public event Action<PetSettings>? OnSettingsChanged;

        public ConfigService()
        {
            var baseDir = GetConfigBaseDir();
            _settingsPath = Path.Combine(baseDir, "settings.json");
            _wzConfigPath = Path.Combine(baseDir, "wz-config.json");
            _settings = new PetSettings();
            _wzConfig = new WzConfig();
        }

        /// <summary>
        /// 加载设置
        /// </summary>
        public PetSettings LoadSettings()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(_settingsPath))
                    {
                        var json = File.ReadAllText(_settingsPath);
                        var settings = JsonSerializer.Deserialize<PetSettings>(json, GetJsonOptions());
                        if (settings != null)
                        {
                            _settings = settings;
                            return _settings;
                        }
                    }
                }
                catch (JsonException)
                {
                    // JSON格式错误，使用默认值
                }
                catch (IOException)
                {
                    // 文件读取错误，使用默认值
                }

                // 文件不存在或解析失败，创建默认值
                _settings = CreateDefaultSettings();
                SaveSettings(_settings);
                return _settings;
            }
        }

        /// <summary>
        /// 保存设置
        /// </summary>
        public void SaveSettings(PetSettings settings)
        {
            lock (_lock)
            {
                try
                {
                    var dir = Path.GetDirectoryName(_settingsPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    var json = JsonSerializer.Serialize(settings, GetJsonOptions());
                    File.WriteAllText(_settingsPath, json);
                    _settings = settings;
                    OnSettingsChanged?.Invoke(settings);
                }
                catch (IOException)
                {
                    // 忽略写入错误
                }
            }
        }

        /// <summary>
        /// 加载WZ配置
        /// </summary>
        public WzConfig LoadWzConfig()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(_wzConfigPath))
                    {
                        var json = File.ReadAllText(_wzConfigPath);
                        var config = JsonSerializer.Deserialize<WzConfig>(json, GetJsonOptions());
                        if (config != null)
                        {
                            _wzConfig = config;
                            return _wzConfig;
                        }
                    }
                }
                catch (JsonException)
                {
                    // JSON格式错误，使用默认值
                }
                catch (IOException)
                {
                    // 文件读取错误，使用默认值
                }

                // 文件不存在或解析失败，创建默认值
                _wzConfig = new WzConfig();
                SaveWzConfig(_wzConfig);
                return _wzConfig;
            }
        }

        /// <summary>
        /// 保存WZ配置
        /// </summary>
        public void SaveWzConfig(WzConfig config)
        {
            lock (_lock)
            {
                try
                {
                    var dir = Path.GetDirectoryName(_wzConfigPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    var json = JsonSerializer.Serialize(config, GetJsonOptions());
                    File.WriteAllText(_wzConfigPath, json);
                    _wzConfig = config;
                }
                catch (IOException)
                {
                    // 忽略写入错误
                }
            }
        }

        /// <summary>
        /// 重置为默认值
        /// </summary>
        public void ResetToDefaults()
        {
            lock (_lock)
            {
                _settings = CreateDefaultSettings();
                SaveSettings(_settings);
            }
        }

        private static string GetConfigBaseDir()
        {
            if (OperatingSystem.IsMacOS())
            {
                var home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(home, ".minipet");
            }
            else if (OperatingSystem.IsWindows())
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(appData, "MiniPet");
            }
            else
            {
                // Linux/其他平台
                var home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(home, ".minipet");
            }
        }

        private static PetSettings CreateDefaultSettings()
        {
            return new PetSettings
            {
                WzLibPath = string.Empty,
                BaseWzPath = string.Empty,
                CurrentMobId = 0,
                CurrentEntityType = "mob",
                MapId = 0,
                BalloonId = 0,
                DefaultAnim = "stand",
                Theme = "dark",
                Personality = string.Empty,
                Favorites = new System.Collections.Generic.List<FavoriteItem>(),
                RecentItems = new System.Collections.Generic.List<RecentItem>(),
                WindowX = 0,
                WindowY = 0,
                WindowScale = 1.0,
                HermesConfig = null
            };
        }

        private static JsonSerializerOptions GetJsonOptions()
        {
            return new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            };
        }
    }
}
