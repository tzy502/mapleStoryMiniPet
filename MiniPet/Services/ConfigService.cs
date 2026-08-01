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
                CurrentMobId = string.Empty,
                CurrentEntityType = "mob",
                MapId = string.Empty,
                BalloonId = string.Empty,
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
                PropertyNameCaseInsensitive = true,
                Converters = { new LenientStringConverter(), new MapHistoryEntryConverter() }
            };
        }
    }

    /// <summary>
    /// 容错字符串转换器：允许从 JSON 数字/布尔读为字符串（迁移旧 settings.json 里数值型 id 到 string 字段）。
    /// </summary>
    public class LenientStringConverter : System.Text.Json.Serialization.JsonConverter<string>
    {
        public override string? Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.TokenType switch
            {
                System.Text.Json.JsonTokenType.String => reader.GetString(),
                System.Text.Json.JsonTokenType.Null => null,
                _ => System.Text.Encoding.UTF8.GetString(reader.ValueSpan)
            };
        }

        public override void Write(System.Text.Json.Utf8JsonWriter writer, string value, JsonSerializerOptions options)
            => writer.WriteStringValue(value);
    }

    /// <summary>
    /// MapHistoryEntry 迁移兼容转换器（F2）：
    /// 旧配置 PetSettings.MapHistory 为 List&lt;string&gt;（纯 id），新为 List&lt;MapHistoryEntry&gt;。
    /// Read 同时接受 JSON string（→ new MapHistoryEntry{Id=...}，crop 全零经 CropFull 归一化为整图）
    /// 与 StartObject（正常对象）。否则旧配置反序列化抛 JsonException，触发 LoadSettings 的
    /// CreateDefaultSettings+SaveSettings 覆写，清空用户全部配置（ConfigService.cs:54-65）。
    /// Write 手写以避免再次命中本 converter 递归。
    /// </summary>
    public class MapHistoryEntryConverter : System.Text.Json.Serialization.JsonConverter<MapHistoryEntry>
    {
        public override MapHistoryEntry? Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == System.Text.Json.JsonTokenType.String)
            {
                return new MapHistoryEntry { Id = reader.GetString() ?? string.Empty };
            }
            if (reader.TokenType == System.Text.Json.JsonTokenType.StartObject)
            {
                using var doc = System.Text.Json.JsonDocument.ParseValue(ref reader);
                var root = doc.RootElement;
                return new MapHistoryEntry
                {
                    Id = TryString(root, "id"),
                    Name = TryString(root, "name"),
                    CropX = TryInt(root, "cropX"),
                    CropY = TryInt(root, "cropY"),
                    CropW = TryInt(root, "cropW"),
                    CropH = TryInt(root, "cropH")
                };
            }
            return null;
        }

        public override void Write(System.Text.Json.Utf8JsonWriter writer, MapHistoryEntry value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("id", value.Id);
            writer.WriteString("name", value.Name);
            writer.WriteNumber("cropX", value.CropX);
            writer.WriteNumber("cropY", value.CropY);
            writer.WriteNumber("cropW", value.CropW);
            writer.WriteNumber("cropH", value.CropH);
            writer.WriteEndObject();
        }

        private static string TryString(System.Text.Json.JsonElement el, string name)
        {
            if (el.TryGetProperty(name, out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String)
                return p.GetString() ?? string.Empty;
            return string.Empty;
        }

        private static int TryInt(System.Text.Json.JsonElement el, string name)
        {
            if (el.TryGetProperty(name, out var p) && p.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                if (p.TryGetInt32(out var v)) return v;
                return (int)p.GetDouble();
            }
            return 0;
        }
    }
}
