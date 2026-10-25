using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Avalonia.Threading;
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
                            EnsurePets(settings);   // V0.1.3：旧单宠配置 → Pets[0]（内存内迁移，不落盘）
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
                EnsurePets(_settings);
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

        // ───────────────────────── V0.1.3 多桌宠 ─────────────────────────

        /// <summary>
        /// 保证 settings.Pets 至少有一只（旧配置迁移）：空列表时用旧单值字段生成 pet-1；
        /// 并保证有且仅有一只主宠。纯内存操作不写盘（首次 SaveSettings 时落盘）。
        /// </summary>
        public static List<PetInstanceConfig> EnsurePets(PetSettings settings)
        {
            if (settings.Pets == null) { settings.Pets = new List<PetInstanceConfig>(); }

            if (settings.Pets.Count == 0)
            {
                var isPaperdoll = settings.CurrentMobId == PaperdollService.SentinelMobId;
                // 旧 WindowX/WindowY 全仓无写入方 → (0,0) 视为「从未摆放过」，交给启动错开逻辑
                var legacyPos = !(settings.WindowX == 0 && settings.WindowY == 0);
                settings.Pets.Add(new PetInstanceConfig
                {
                    Id = "pet-1",
                    EntityType = isPaperdoll
                        ? "player"
                        : (string.IsNullOrEmpty(settings.CurrentEntityType) ? "Mob" : settings.CurrentEntityType),
                    MobId = (settings.CurrentMobId ?? string.Empty).Trim(),
                    Paperdoll = settings.CurrentPaperdoll,
                    WindowX = legacyPos ? settings.WindowX : int.MinValue,
                    WindowY = legacyPos ? settings.WindowY : int.MinValue,
                    WindowScale = settings.WindowScale > 0 ? settings.WindowScale : 1.0,
                    Primary = true
                });
            }

            // 手改配置可能塞进 null 元素 → 先剔除，避免下面 Where/FirstOrDefault 直接 NRE
            settings.Pets.RemoveAll(p => p == null);

            // 主宠唯一性自愈（配置被手改 / 旧版本写入 / 迁移异常时）
            var primaries = settings.Pets.Where(p => p.Primary).ToList();
            if (primaries.Count == 0)
            {
                settings.Pets[0].Primary = true;
            }
            else if (primaries.Count > 1)
            {
                foreach (var extra in primaries.Skip(1)) { extra.Primary = false; }
            }
            return settings.Pets;
        }

        /// <summary>生成当前配置里未被占用的宠物 id（pet-N）。</summary>
        public static string NewPetId(PetSettings settings)
        {
            EnsurePets(settings);
            for (int i = 1; i < 1000; i++)
            {
                var id = $"pet-{i}";
                if (!settings.Pets.Any(p => p.Id == id)) { return id; }
            }
            return $"pet-{Guid.NewGuid():N}";
        }

        /// <summary>
        /// 合并保存（多桌宠安全）：把 <paramref name="local"/> 的**非 Pets 字段**写回磁盘最新配置，
        /// Pets（宠物实例列表）保持磁盘最新值。仍持有旧快照的调用方（设置中心各 tab）用它写全局设置，
        /// 不会把别的宠物刚写的实例数据回滚掉。
        /// </summary>
        public void SaveSettingsKeepingPets(PetSettings local)
        {
            if (local == null) { return; }
            Update(latest =>
            {
                var pets = latest.Pets;
                foreach (var prop in typeof(PetSettings).GetProperties())
                {
                    if (prop.Name == nameof(PetSettings.Pets)) { continue; }
                    if (!prop.CanRead || !prop.CanWrite) { continue; }
                    try { prop.SetValue(latest, prop.GetValue(local)); }
                    catch (Exception ex) { Console.Error.WriteLine($"[ConfigService] MergeField {prop.Name}: {ex.Message}"); }
                }
                latest.Pets = pets;
            });
        }

        /// <summary>
        /// 原子更新（V0.1.3 多桌宠）：与 <see cref="Update"/> 等价的别名——合并 0.2.1 后统一到同一事务实现
        /// （锁内 读→改→写；UI 线程内联执行，非 UI 线程派发到 UI 线程）。
        /// </summary>
        public void UpdateSettings(Action<PetSettings> mutate) => Update(mutate);

        // ───────────────────── 表情开关持久化（M2，PLAN-表情功能.md §4.3）─────────────────────
        // PetSettings 是 class（非 record，无 with 语义）：读写都经 LoadSettings / UpdateSettings 整对象事务，
        // 不新增拷贝构造；SaveSettingsKeepingPets 按属性反射合并，新字段自动纳入合并（无需改动）。

        /// <summary>
        /// 读取「自动眨眼」开关（默认 true）。走 LoadSettings 的内存缓存，不额外读盘。
        /// </summary>
        public bool GetTopmost() => LoadSettings().Topmost;
        public void SetTopmost(bool enabled) => UpdateSettings(s => s.Topmost = enabled);

        /// <summary>读取「macOS Dock 图标可见」开关（默认 true，V0.1.0 对外发布口径）。</summary>
        public bool GetShowDockIcon() => LoadSettings().ShowDockIcon;

        /// <summary>写入「macOS Dock 图标可见」开关并落盘（语义同 SetTopmost）。</summary>
        public void SetShowDockIcon(bool enabled) => UpdateSettings(s => s.ShowDockIcon = enabled);
        public bool GetAutoBlink() => LoadSettings().AutoBlink;

        /// <summary>
        /// 写入「自动眨眼」开关并落盘（原子事务：锁内 读→改→写；非 UI 线程派发到 UI 线程，语义同
        /// <see cref="Update"/>）。
        /// </summary>
        public void SetAutoBlink(bool enabled) => UpdateSettings(s => s.AutoBlink = enabled);

        /// <summary>
        /// 读取「随机表情」开关（默认 false）。走 LoadSettings 的内存缓存，不额外读盘。
        /// </summary>
        public bool GetRandomExpression() => LoadSettings().RandomExpression;

        /// <summary>
        /// 写入「随机表情」开关并落盘（原子事务：锁内 读→改→写；非 UI 线程派发到 UI 线程，语义同
        /// <see cref="Update"/>）。
        /// </summary>
        public void SetRandomExpression(bool enabled) => UpdateSettings(s => s.RandomExpression = enabled);

        /// <summary>
        /// 原子更新指定宠物的实例配置；返回更新后的实例（找不到该 id 返回 null）。
        /// 主宠的实体 / 外观 / 缩放变化会同步写回旧单值字段（兼容旧读法）。
        /// </summary>
        public PetInstanceConfig? UpdatePet(string petId, Action<PetInstanceConfig> mutate)
        {
            PetInstanceConfig? result = null;
            UpdateSettings(s =>
            {
                var pet = EnsurePets(s).FirstOrDefault(p => p.Id == petId);
                if (pet == null) { return; }
                mutate(pet);
                SyncLegacyFields(s, pet);
                result = pet;
            });
            return result;
        }

        /// <summary>添加一只宠物（忽略传入的 Primary：主宠只能经 <see cref="SetPrimaryPet"/> 变更）。</summary>
        public PetInstanceConfig AddPet(PetInstanceConfig pet)
        {
            UpdateSettings(s =>
            {
                var list = EnsurePets(s);
                if (string.IsNullOrEmpty(pet.Id)) { pet.Id = NewPetId(s); }
                pet.Primary = false;
                list.Add(pet);
            });
            return pet;
        }

        /// <summary>移除一只宠物；主宠被移除时第一只剩余宠物继任。仅剩一只时拒绝移除（返回 false）。</summary>
        public bool RemovePet(string petId)
        {
            var ok = false;
            UpdateSettings(s =>
            {
                var list = EnsurePets(s);
                if (list.Count <= 1) { return; }
                var pet = list.FirstOrDefault(p => p.Id == petId);
                if (pet == null) { return; }
                var wasPrimary = pet.Primary;
                list.Remove(pet);
                if (wasPrimary && list.Count > 0) { list[0].Primary = true; }
                SyncLegacyFields(s, list.FirstOrDefault(p => p.Primary));
                ok = true;
            });
            return ok;
        }

        /// <summary>切换主宠（唯一性由 <see cref="EnsurePets"/> 兜底）。</summary>
        public bool SetPrimaryPet(string petId)
        {
            var ok = false;
            UpdateSettings(s =>
            {
                var list = EnsurePets(s);
                var pet = list.FirstOrDefault(p => p.Id == petId);
                if (pet == null) { return; }
                foreach (var p in list) { p.Primary = ReferenceEquals(p, pet); }
                SyncLegacyFields(s, pet);
                ok = true;
            });
            return ok;
        }

        /// <summary>把主宠的实体 / 外观 / 缩放同步到旧单值字段（旧版本回读 + 旧代码路径兼容）。</summary>
        private static void SyncLegacyFields(PetSettings s, PetInstanceConfig? primary)
        {
            if (primary == null || !primary.Primary) { return; }
            s.CurrentMobId = primary.MobId ?? string.Empty;
            s.CurrentEntityType = (primary.EntityType ?? string.Empty).ToLowerInvariant();
            s.CurrentPaperdoll = primary.Paperdoll;
            if (primary.WindowScale > 0) { s.WindowScale = primary.WindowScale; }
        }

        /// <summary>
        /// 原子更新设置（音乐模块 V0.2.0 高频持久化入口，如每次切曲写 LastTrackKey/PlayHistory）：
        /// 「读取最新配置 → mutate 修改（含 Music 子对象）→ 整体回存」整个事务在现有 _lock 序列化锁内
        /// 完成，消除读改写撕裂；且最终在 UI 线程（Avalonia Dispatcher.UIThread）上执行——与设置窗口
        /// 「缓存 _settings 引用后整对象回存」的既有写入点同线程串行化，闭合「缓存读↔整存」区间内的插入提交。
        /// 线程语义：已在 UI 线程则内联同步执行；否则派发到 UI 线程异步执行（尽力而为，不阻塞调用方）。
        /// mutate 内异常或 IO 异常记 Console.Error 不上抛（尽力而为持久化，不影响调用方流程）。
        /// </summary>
        /// <param name="mutate">对最新 PetSettings 的修改委托（在 UI 线程 + _lock 锁内执行）。</param>
        public void Update(Action<PetSettings> mutate)
        {
            // 事务体：锁内完成 读→改→写（Monitor 可重入，嵌套调用 LoadSettings/SaveSettings 无死锁）
            void RunTransaction()
            {
                lock (_lock)
                {
                    try
                    {
                        var settings = LoadSettings();
                        mutate(settings);
                        SaveSettings(settings);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[ConfigService] Update: {ex.Message}");
                    }
                }
            }

            var ui = Dispatcher.UIThread;
            if (ui.CheckAccess())
            {
                // 已在 UI 线程：内联同步执行
                RunTransaction();
            }
            else
            {
                // 非 UI 线程：派发到 UI 线程（与设置窗口整对象回存写入点串行化）；派发失败（如关闭中）仅记录
                try
                {
                    ui.InvokeAsync(RunTransaction);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ConfigService] Update 派发失败: {ex.Message}");
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
