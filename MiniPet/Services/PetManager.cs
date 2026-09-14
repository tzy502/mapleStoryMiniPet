using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using MiniPet.Models;
using MiniPet.Views;

namespace MiniPet.Services;

/// <summary>
/// 多桌宠管理器（V0.1.3）：持有全部 <see cref="PetWindow"/>，按配置创建 / 移除宠物、维护主宠，
/// 并把「主宠」的全局绑定（地图背景 + Agent 特效驱动）派发下去。
/// <para>
/// 方向约定：PetManager → PetWindow（创建 / 生命周期）；PetWindow **不反向持有** PetManager，
/// 需要加 / 删 / 设主宠时经 <see cref="EventBus"/> 的 AddPet / RemovePet / SetPrimaryPet 命令事件。
/// </para>
/// </summary>
public class PetManager
{
    private readonly IServiceProvider _services;
    private readonly ConfigService _config;
    private readonly EventBus _eventBus;
    private readonly WzService _wzService;
    private readonly List<PetWindow> _pets = new();
    private bool _started;
    /// <summary>全局唯一的设置中心（V0.1.3：旧版每只桌宠各持一个实例，多只桌宠会开出多个设置窗口）。</summary>
    private SettingsWindow? _settingsWindow;

    /// <summary>全部宠物窗口（创建顺序）。</summary>
    public IReadOnlyList<PetWindow> Pets => _pets;

    public int Count => _pets.Count;

    /// <summary>主宠（挂地图背景 + 响应 Agent 特效）；无宠物时为 null。</summary>
    public PetWindow? Primary => _pets.FirstOrDefault(p => p.IsPrimaryRole) ?? _pets.FirstOrDefault();

    /// <summary>宠物集合变化（增 / 删 / 主宠切换）。</summary>
    public event Action? PetsChanged;

    /// <summary>主宠变化（参数为新主宠，可能为 null）。</summary>
    public event Action<PetWindow?>? PrimaryChanged;

    public PetManager(IServiceProvider services, ConfigService config, EventBus eventBus, WzService wzService)
    {
        _services = services;
        _config = config;
        _eventBus = eventBus;
        _wzService = wzService;

        _eventBus.Subscribe(EventType.AddPet, data =>
            RunOnUI(() => AddPetLike((data as PetCommandEvent)?.PetId)));
        _eventBus.Subscribe(EventType.RemovePet, data =>
            RunOnUI(() =>
            {
                var id = (data as PetCommandEvent)?.PetId;
                if (!string.IsNullOrEmpty(id)) { RemovePet(id); }
            }));
        _eventBus.Subscribe(EventType.SetPrimaryPet, data =>
            RunOnUI(() =>
            {
                var id = (data as PetCommandEvent)?.PetId;
                if (!string.IsNullOrEmpty(id)) { SetPrimary(id); }
            }));
    }

    private static void RunOnUI(Action action)
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess()) { action(); }
            else { Dispatcher.UIThread.Post(action); }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetManager] RunOnUI: {ex.Message}"); }
    }

    /// <summary>
    /// 启动：按配置创建全部宠物（配置已由 ConfigService 迁移出 Pets），返回主宠供 desktop.MainWindow。
    /// 位置：配置里有坐标用坐标，否则按序号在主屏中央错开摆放（多只不叠在一起）。
    /// </summary>
    public PetWindow? CreateAllFromConfig()
    {
        PetWindow? primary = null;
        try
        {
            var settings = _config.LoadSettings();
            var list = ConfigService.EnsurePets(settings);
            // 迁移结果落盘一次（旧单宠配置 → pets 列表）
            _config.UpdateSettings(s => ConfigService.EnsurePets(s));

            for (int i = 0; i < list.Count; i++)
            {
                var cfg = list[i];
                try
                {
                    var win = CreateWindow(cfg);
                    PlaceWindow(win, cfg, i);
                    win.Show();
                    if (cfg.Primary) { primary = win; }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[PetManager] 创建宠物 {cfg.Id} 失败: {ex.Message}");
                }
            }

            _started = true;
            primary ??= _pets.FirstOrDefault();
            ApplyPrimaryRole();
            Console.WriteLine($"[PetManager] 已创建 {_pets.Count} 只桌宠，主宠 = {primary?.PetId ?? "(无)"}");
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetManager] CreateAllFromConfig: {ex.Message}"); }
        return primary;
    }

    /// <summary>
    /// 打开（或复用）**全局唯一**的设置中心，并把编辑目标切到 targetPetId（空 = 保持当前 / 主宠）。
    /// 窗口只在首次创建时接线，之后一律复用（设置中心关闭 = Hide）。
    /// </summary>
    public SettingsWindow? ShowSettings(string? targetPetId)
    {
        try
        {
            if (_settingsWindow == null)
            {
                var config = _services.GetRequiredService<ConfigService>();
                var wz = _services.GetRequiredService<WzService>();
                var paperdoll = _services.GetRequiredService<PaperdollService>();
                var map = _services.GetRequiredService<MapService>();
                var balloon = _services.GetRequiredService<BalloonService>();
                var bus = _services.GetRequiredService<EventBus>();
                // V0.2.1 音乐页：设置中心构造末尾追加音乐目录 / 播放服务（合并 0.1.3 时两边参数都要透传）
                var musicCatalog = _services.GetRequiredService<MusicCatalogService>();
                var musicPlayer = _services.GetRequiredService<MusicPlayerService>();
                var initialTarget = string.IsNullOrEmpty(targetPetId) ? (Primary?.PetId ?? "pet-1") : targetPetId;
                _settingsWindow = new SettingsWindow(config, wz, paperdoll, map, balloon, musicCatalog, musicPlayer, this, bus, initialTarget);
                WireSettingsWindow(_settingsWindow);
            }
            else if (!string.IsNullOrEmpty(targetPetId))
            {
                _settingsWindow.SetTargetPet(targetPetId);
            }
            if (_settingsWindow.IsVisible)
            {
                _settingsWindow.Activate();
            }
            else
            {
                _settingsWindow.Show();
            }
            return _settingsWindow;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PetManager] ShowSettings: {ex.Message}");
            return _settingsWindow;
        }
    }

    /// <summary>当前设置中心实例（可能为 null：还没打开过）。</summary>
    public SettingsWindow? Settings => _settingsWindow;

    /// <summary>
    /// 设置中心的全局回调接线（只接一次）。背景 / 镜头尺寸 / 层开关 / WZ 刷新都落到**主宠**——
    /// 地图背景是全局单份语义（只有主宠挂背景），旧版是接在「创建该设置窗口的那只宠物」上。
    /// </summary>
    private void WireSettingsWindow(SettingsWindow w)
    {
        try
        {
            w.OnBackgroundMapApplied += sel => RunOnUI(() => Primary?.ApplyMapSelectionFromSettings(sel));
            w.OnViewportSizeApplied += (vw, vh) => RunOnUI(() => Primary?.ApplyViewportSizeFromSettings(vw, vh));
            w.OnLayerTogglesChanged += () => RunOnUI(() => Primary?.ReRenderBackgroundFromSettings());
            w.OnWzRefreshed += () => RunOnUI(() =>
            {
                try
                {
                    var map = _services.GetService<MapService>();
                    map?.ClearSpriteCache();
                    map?.InvalidateFullRenderCache();
                    LoadAllPets(_wzService);   // 热更新后所有桌宠各自重载实体
                }
                catch (Exception ex) { Console.Error.WriteLine($"[PetManager] OnWzRefreshed: {ex.Message}"); }
            });
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetManager] WireSettingsWindow: {ex.Message}"); }
    }

    /// <summary>WZ 加载完成后，让每只宠物各自恢复自己的实体（纸娃娃 / Mob / NPC）。</summary>
    public void LoadAllPets(WzService? wzService)
    {
        foreach (var pet in _pets.ToList())
        {
            try { pet.LoadDefaultPet(wzService); }
            catch (Exception ex) { Console.Error.WriteLine($"[PetManager] LoadAllPets({pet.PetId}): {ex.Message}"); }
        }
    }

    /// <summary>按 id 找宠物窗口。</summary>
    public PetWindow? Find(string petId) => _pets.FirstOrDefault(p => p.PetId == petId);

    /// <summary>
    /// 「添加桌宠」：复制 sourcePetId（空 = 主宠）的实体与外观新建一只，位置自动错开。
    /// 返回新窗口（WZ 未就绪 / 配置异常时返回 null）。
    /// </summary>
    public PetWindow? AddPetLike(string? sourcePetId)
    {
        try
        {
            var settings = _config.LoadSettings();
            var list = ConfigService.EnsurePets(settings);
            var src = list.FirstOrDefault(p => p.Id == sourcePetId)
                      ?? list.FirstOrDefault(p => p.Primary)
                      ?? list.FirstOrDefault();
            if (src == null) { return null; }

            var cfg = new PetInstanceConfig
            {
                Id = ConfigService.NewPetId(settings),
                EntityType = src.EntityType,
                MobId = src.MobId,
                Name = UniquePetName(list, src.Name, src.MobId),
                Paperdoll = CloneAppearance(src.Paperdoll),
                WindowX = int.MinValue,
                WindowY = int.MinValue,
                WindowScale = src.WindowScale > 0 ? src.WindowScale : 1.0,
                Primary = false
            };
            _config.AddPet(cfg);

            var win = CreateWindow(cfg);
            PlaceWindow(win, cfg, _pets.Count - 1);
            win.Show();
            // 新宠物立即加载自己的实体（WZ 已加载时即时可见；未加载由 App 的 LoadAllPets 兜底）
            win.LoadDefaultPet(_wzService.IsLoaded ? _wzService : null);

            PetsChanged?.Invoke();
            Console.WriteLine($"[PetManager] 添加桌宠 {cfg.Id}（复制自 {src.Id}，实体={cfg.MobId}）");
            return win;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PetManager] AddPetLike: {ex.Message}");
            return null;
        }
    }

    /// <summary>移除一只宠物（最后一只拒绝移除）；窗口真正关闭，不走「隐藏到托盘」。</summary>
    public bool RemovePet(string petId)
    {
        try
        {
            if (_pets.Count <= 1)
            {
                Console.WriteLine("[PetManager] 仅剩一只桌宠，拒绝移除");
                return false;
            }
            var win = _pets.FirstOrDefault(p => p.PetId == petId);
            if (win == null) { return false; }
            if (!_config.RemovePet(petId)) { return false; }

            _pets.Remove(win);
            try { win.CloseForRemoval(); }
            catch (Exception ex) { Console.Error.WriteLine($"[PetManager] 关闭 {petId}: {ex.Message}"); }

            PetsChanged?.Invoke();
            ApplyPrimaryRole();
            Console.WriteLine($"[PetManager] 移除桌宠 {petId}，剩余 {_pets.Count} 只");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PetManager] RemovePet: {ex.Message}");
            return false;
        }
    }

    /// <summary>设为主宠（挂背景 + 响应 Agent 特效）。</summary>
    public bool SetPrimary(string petId)
    {
        try
        {
            if (_pets.All(p => p.PetId != petId)) { return false; }
            if (!_config.SetPrimaryPet(petId)) { return false; }
            ApplyPrimaryRole();
            PetsChanged?.Invoke();
            Console.WriteLine($"[PetManager] 主宠切换为 {petId}");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PetManager] SetPrimary: {ex.Message}");
            return false;
        }
    }

    /// <summary>把「谁是主宠」派发到各窗口与订阅方（背景归属 + Agent 特效驱动跟随）。</summary>
    private void ApplyPrimaryRole()
    {
        try
        {
            var primary = Primary;
            foreach (var pet in _pets.ToList())
            {
                try { pet.SetPrimaryRole(ReferenceEquals(pet, primary)); }
                catch (Exception ex) { Console.Error.WriteLine($"[PetManager] SetPrimaryRole({pet.PetId}): {ex.Message}"); }
            }
            PrimaryChanged?.Invoke(primary);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetManager] ApplyPrimaryRole: {ex.Message}"); }
    }

    private PetWindow CreateWindow(PetInstanceConfig cfg)
    {
        var win = ActivatorUtilities.CreateInstance<PetWindow>(_services, cfg);
        win.Closed += (_, _) =>
        {
            try
            {
                if (_pets.Remove(win) && _started)
                {
                    PetsChanged?.Invoke();
                    ApplyPrimaryRole();
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[PetManager] Closed({cfg.Id}): {ex.Message}"); }
        };
        _pets.Add(win);
        return win;
    }

    /// <summary>摆放窗口：配置坐标优先，否则主屏中央按序号错开（多只不重叠）。</summary>
    private static void PlaceWindow(PetWindow win, PetInstanceConfig cfg, int index)
    {
        try
        {
            if (cfg.WindowX != int.MinValue && cfg.WindowY != int.MinValue)
            {
                // S12：坐标可能来自已拔掉的副屏 → 不在任何屏幕内时回退默认摆放，否则桌宠会「消失」
                if (IsPointOnAnyScreen(win, cfg.WindowX, cfg.WindowY))
                {
                    win.SetStartupPosition(new PixelPoint(cfg.WindowX, cfg.WindowY));
                    return;
                }
                Console.WriteLine($"[PetManager] {cfg.Id} 配置坐标 ({cfg.WindowX},{cfg.WindowY}) 不在任何屏幕内，回退默认摆放");
            }
            var screens = win.Screens;
            if (screens == null || screens.ScreenCount == 0) { return; }
            var screen = screens.Primary ?? screens.All[0];
            var b = screen.WorkingArea;
            int offsetX = index * 150;
            int offsetY = index * 110;
            int x = b.X + (b.Width - 220) / 2 + offsetX;
            int y = b.Y + (b.Height - 260) / 2 + offsetY;
            // 越界回收（多只时最后一两只有可能推出屏幕）
            if (x + 120 > b.X + b.Width) { x = b.X + b.Width - 220; }
            if (y + 160 > b.Y + b.Height) { y = b.Y + b.Height - 260; }
            if (x < b.X) { x = b.X; }
            if (y < b.Y) { y = b.Y; }
            win.SetStartupPosition(new PixelPoint(x, y));
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetManager] PlaceWindow: {ex.Message}"); }
    }

    /// <summary>复制宠物时的显示名：源名 + 序号（重名时递增），源名为空则留空（列表回退 WZ 名 / id）。</summary>
    private static string UniquePetName(List<PetInstanceConfig> existing, string? srcName, string? mobId)
    {
        try
        {
            var baseName = (srcName ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(baseName)) { return string.Empty; }
            if (existing.All(x => !string.Equals(x.Name, baseName, StringComparison.Ordinal))) { return baseName; }
            for (int i = 2; i < 100; i++)
            {
                var candidate = $"{baseName} {i}";
                if (existing.All(x => !string.Equals(x.Name, candidate, StringComparison.Ordinal))) { return candidate; }
            }
            return baseName;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PetManager] UniquePetName: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>给定坐标是否落在任一屏幕范围内（含少量容差；取不到屏幕信息时按「在屏内」处理）。</summary>
    private static bool IsPointOnAnyScreen(PetWindow win, int x, int y)
    {
        try
        {
            var screens = win.Screens;
            if (screens == null) { return true; }
            foreach (var screen in screens.All)
            {
                var b = screen.Bounds;
                if (x >= b.X - 50 && x < b.X + b.Width && y >= b.Y - 50 && y < b.Y + b.Height)
                {
                    return true;
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PetManager] IsPointOnAnyScreen: {ex.Message}");
            return true;
        }
    }

    /// <summary>纸娃娃外观深拷贝（新宠物独立外观；JSON 往返避免共享引用被改串）。</summary>
    private static CharacterAppearance? CloneAppearance(CharacterAppearance? src)
    {
        if (src == null) { return null; }
        try
        {
            return JsonSerializer.Deserialize<CharacterAppearance>(JsonSerializer.Serialize(src));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PetManager] CloneAppearance: {ex.Message}");
            return null;
        }
    }
}
