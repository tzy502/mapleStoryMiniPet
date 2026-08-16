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
        // P6-3：磁盘 JSON 内存缓存——高频 LoadSettings（如 EffectLayerService 每次 Agent 状态变化 2 次）
        // 不再锁内反复 ReadAllText；SaveSettings/SaveWzConfig 成功后刷新。
        // 语义保持：每次调用仍反序列化出独立对象（调用方可安全持有/修改，与旧逐次读盘行为一致）。
        private string? _settingsJsonCache;
        private string? _wzConfigJsonCache;

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
                    // P6-3：磁盘只读一次，后续走内存缓存（SaveSettings 后刷新）；
                    // 每次仍反序列化出独立对象，调用方持有/修改互不影响（与旧逐次读盘语义一致）
                    if (_settingsJsonCache == null && File.Exists(_settingsPath))
                    {
                        _settingsJsonCache = File.ReadAllText(_settingsPath);
                    }
                    if (_settingsJsonCache != null)
                    {
                        var settings = JsonSerializer.Deserialize<PetSettings>(_settingsJsonCache, GetJsonOptions());
                        if (settings != null)
                        {
                            _settings = settings;
                            return _settings;
                        }
                    }
                }
                catch (JsonException)
                {
                    // JSON格式错误：缓存作废（下次重读磁盘），使用默认值
                    _settingsJsonCache = null;
                }
                catch (IOException)
                {
                    // 文件读取错误：缓存作废，使用默认值
                    _settingsJsonCache = null;
                }

                // 缓存未命中或解析失败，创建默认值
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
                    // 原子写（B3）：先写同目录临时文件，再原子替换目标文件，避免崩溃/断电写坏 settings.json
                    var tmpPath = _settingsPath + ".tmp";
                    try
                    {
                        File.WriteAllText(tmpPath, json);
                        if (File.Exists(_settingsPath))
                        {
                            // 目标存在：File.Replace 原子替换（同卷 rename）；备份参数传 null 不留 .bak 避免堆积
                            try
                            {
                                File.Replace(tmpPath, _settingsPath, null);
                            }
                            catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
                            {
                                // File.Replace 在个别平台/文件系统不可用（含目标刚消失）时，回退 File.Move 覆盖（同卷 move 原子）
                                File.Move(tmpPath, _settingsPath, true);
                            }
                        }
                        else
                        {
                            // 目标不存在：直接 File.Move（同卷 move 原子）
                            File.Move(tmpPath, _settingsPath, true);
                        }
                    }
                    finally
                    {
                        // 清理残留临时文件（写入失败或替换异常时）
                        if (File.Exists(tmpPath))
                        {
                            try
                            {
                                File.Delete(tmpPath);
                            }
                            catch (IOException)
                            {
                                // 忽略清理失败
                            }
                        }
                    }

                    _settings = settings;
                    _settingsJsonCache = json; // P6-3：内存缓存刷新（与落盘同源，保证下次 LoadSettings 返回本次保存值）
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
                    // P6-3：同 LoadSettings，磁盘只读一次，后续走内存缓存（SaveWzConfig 后刷新）
                    if (_wzConfigJsonCache == null && File.Exists(_wzConfigPath))
                    {
                        _wzConfigJsonCache = File.ReadAllText(_wzConfigPath);
                    }
                    if (_wzConfigJsonCache != null)
                    {
                        var config = JsonSerializer.Deserialize<WzConfig>(_wzConfigJsonCache, GetJsonOptions());
                        if (config != null)
                        {
                            _wzConfig = config;
                            return _wzConfig;
                        }
                    }
                }
                catch (JsonException)
                {
                    _wzConfigJsonCache = null;
                }
                catch (IOException)
                {
                    _wzConfigJsonCache = null;
                }

                // 缓存未命中或解析失败，创建默认值
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
                    _wzConfigJsonCache = json; // P6-3：内存缓存刷新
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
                // 2026-08-17 用户要求「和 exe 同目录」：Windows 绿色便携版——settings.json 跟随 exe
                // （拷走整个文件夹即带走配置）；exe 目录不可写（如 Program Files）时回退 %APPDATA%\MiniPet。
                try
                {
                    var exeDir = AppContext.BaseDirectory;
                    var probe = Path.Combine(exeDir, ".minipet-write-test");
                    File.WriteAllText(probe, "ok");
                    File.Delete(probe);
                    return exeDir;
                }
                catch
                {
                    var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    return Path.Combine(appData, "MiniPet");
                }
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
