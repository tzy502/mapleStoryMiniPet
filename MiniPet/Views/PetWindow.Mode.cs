using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using MiniPet.Models;
using MiniPet.Services;

namespace MiniPet.Views;

/// <summary>
/// PetWindow 背景模式部分（partial 拆分）：静态图/摄像机模式切换、背景开关、
/// 地图选择应用、地面落地、重渲染。
/// F18 起（PLAN Rev 5 背景闸门）：五个背景变更命令全部经 RoadModeController.GateBackgroundAction
/// 串行化——路激活时先完整退出路（淡出→恢复→宠物复位→抑制代际收尾）await 完毕后才执行原语义；
/// 原 void 方法内部的 Task.Run/Dispatcher.Post 延迟段全部收编为 awaitable 内核 XxxCoreAsync
/// （命令真正完成 = 地图加载/窗口显隐/配置落库全部收尾——闸门释放前无残留飞行段）。
/// </summary>
public partial class PetWindow
{
    private void OnMapRendered()
    {
        if (_placePetAfterRender && _currentMap != null)
        {
            _placePetAfterRender = false;
            if (_backgroundWindow.IsVisible) PlacePetOnMapGround(_currentMap, 0, 0);
        }
    }

    /// <summary>
    /// 把宠物图片中心点的最底部对齐到地图地面（foothold）：
    /// 横向居中于背景窗口，纵向让精灵图底部落在地面线上（foothold 缺失时落在背景窗口底部）。
    /// #6：用 BackgroundWindow.EffectiveCrop*（裁紧后的世界坐标 crop + 实际尺寸）；
    ///     入参 cropX/cropY 不再可信（裁紧后窗口顶部对应世界 y = cropY + 裁掉的顶部透明行数）。
    /// </summary>

    private void PlacePetOnMapGround(MapInfo map, int cropX, int cropY)
    {
        try
        {
            var bg = _backgroundWindow;
            int ecX = bg.EffectiveCropX, ecY = bg.EffectiveCropY;
            int bgW = bg.EffectiveCropW > 0 ? bg.EffectiveCropW : (int)bg.Width;
            int bgH = bg.EffectiveCropH > 0 ? bg.EffectiveCropH : (int)bg.Height;
            int centerScreenX = bg.Position.X + bgW / 2;
            // crop 中心世界 x = ecX + bgW/2；窗口顶部对应世界 y = ecY
            int groundScreenY;
            int? gy = MapService.GetGroundY(map, ecX + bgW / 2);
            if (gy != null)
            {
                groundScreenY = bg.Position.Y + (gy.Value - ecY);
            }
            else
            {
                groundScreenY = bg.Position.Y + bgH - 24;
            }
            int fw = (int)Width;
            int fh = (int)Height;
            Position = new PixelPoint(centerScreenX - fw / 2, groundScreenY - fh);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] PlacePetOnMapGround: {ex.Message}"); }
    }

    private void OpenBackgroundTab()
    {
        try { ShowSettingsWindow(); _settingsWindow?.OpenBackgroundTab(); }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] OpenBackgroundTab: {ex.Message}"); }
    }

    // ── F18 背景快照辅助（RoadModeController.AttachPetWindow 三委托的实现） ──

    /// <summary>进入路前的 MapMode 快照（持久层只存 1/2——MapMode=3 纯运行时态永不落盘，读出 3 视为脏数据按 1 处理）。</summary>
    private int LoadMapModeSnapshot()
    {
        try
        {
            int mode = _configService.LoadSettings().MapMode;
            if (mode == 1 || mode == 2)
            {
                return mode;
            }
            return 1;
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>退出路后按快照恢复两背景窗口可见态（MapMode 持久化由控制器统一收口——本方法只管窗口）。</summary>
    private void RestoreBackgroundSnapshot(Services.Road.RoadBackgroundSnapshot snap)
    {
        try
        {
            var s = _configService.LoadSettings();
            // 摄像机窗口（模式2）
            if (snap.VpVisible && _currentMap != null && _wzService.IsLoaded)
            {
                _mapService.MapShowBack = s.MapShowBack;
                _mapService.MapShowLife = s.MapShowLife;
                _mapService.MapShowPortal = s.MapShowPortal;
                _mapService.MapShowObj = s.MapShowObj;
                _mapService.MapShowTile = s.MapShowTile;
                _mapService.MapShowFoothold = s.MapShowFoothold;
                _mapViewportWindow.SetMap(_currentMap, _mapService);
                _mapViewportWindow.Show();
            }
            else
            {
                _mapViewportWindow.Stop();
                _mapViewportWindow.Hide();
            }
            // 静态图窗口（模式1）
            if (snap.BgVisible && _currentMap != null && !string.IsNullOrEmpty(s.MapId) && _wzService.IsLoaded)
            {
                _mapService.MapShowBack = s.MapShowBack;
                _mapService.MapShowLife = s.MapShowLife;
                _mapService.MapShowPortal = s.MapShowPortal;
                _mapService.MapShowObj = s.MapShowObj;
                _mapService.MapShowTile = s.MapShowTile;
                _mapService.MapShowFoothold = s.MapShowFoothold;
                _backgroundWindow.SetMap(_currentMap, _mapService, s.CropX, s.CropY, s.CropW, s.CropH);
                _backgroundWindow.Show();
            }
            else
            {
                _backgroundWindow.HideMap();
                _backgroundWindow.Hide();
            }
            RaisePet();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] RestoreBackgroundSnapshot: {ex.Message}"); }
    }

    /// <summary>快速关闭背景：清图 + 隐藏全部背景窗口（静态图 + 摄像机）+ 桌宠置顶。
    /// F18：经背景闸门（路激活→先完整退出路再关）。</summary>
    private void QuickHideBackground()
    {
        _ = _roadController.GateBackgroundAction(QuickHideBackgroundCoreAsync);
    }

    private async Task QuickHideBackgroundCoreAsync()
    {
        try
        {
            _backgroundWindow.HideMap();
            _backgroundWindow.Hide();
            _mapViewportWindow.Stop();
            _mapViewportWindow.Hide();
            RaisePet();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] QuickHideBackground: {ex.Message}"); }
        await Task.CompletedTask;
    }

    /// <summary>开启/关闭背景总开关（背景=静态图+摄像机两个互斥分类的抽象）：任一可见→全关；全关→按 MapMode 开对应分类。
    /// F18：经背景闸门（路激活→先完整退出路再切）。</summary>
    private void ToggleBackground()
    {
        _ = _roadController.GateBackgroundAction(ToggleBackgroundCoreAsync);
    }

    private async Task ToggleBackgroundCoreAsync()
    {
        try
        {
            // 任一背景窗口可见 = 背景开着 → 全关（静态图 + 摄像机都关）
            if (_backgroundWindow.IsVisible || _mapViewportWindow.IsVisible)
            {
                _backgroundWindow.HideMap();
                _backgroundWindow.Hide();
                _mapViewportWindow.Stop();
                _mapViewportWindow.Hide();
                RaisePet();
                return;
            }
            var s = _configService.LoadSettings();
            if (string.IsNullOrEmpty(s.MapId))
            {
                OpenBackgroundTab();
                return;
            }
            // 无 WZ：尝试读地图 PNG 缓存（离线显示背景）；无缓存则进设置中心
            if (!_wzService.IsLoaded)
            {
                TryShowCachedMapBackground(s);
                return;
            }
            // F18 闸门收编：原 Task.Run + Dispatcher.UIThread.Post 延迟段改为直接 await（命令完成=地图加载+窗口显隐全部收尾）
            MapInfo? map = null;
            try
            {
                map = await Task.Run(() => _mapService.LoadMap(s.MapId));
            }
            catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] ToggleBg LoadMap: {ex.Message}"); }
            if (map == null)
            {
                OpenBackgroundTab();
                return;
            }
            _currentMap = map;
            // 按 MapMode 开对应分类（互斥：先关另一个）
            if (s.MapMode == 2)
            {
                _mapService.MapShowBack = s.MapShowBack;
                _mapService.MapShowLife = s.MapShowLife;
                _mapService.MapShowPortal = s.MapShowPortal;
                _mapService.MapShowObj = s.MapShowObj;
                _mapService.MapShowTile = s.MapShowTile;
                _mapService.MapShowFoothold = s.MapShowFoothold;
                _mapViewportWindow.SetMap(map, _mapService);
                _mapViewportWindow.Show();
            }
            else
            {
                _mapService.MapShowBack = s.MapShowBack;
                _mapService.MapShowLife = s.MapShowLife;
                _mapService.MapShowPortal = s.MapShowPortal;
                _mapService.MapShowObj = s.MapShowObj;
                _mapService.MapShowTile = s.MapShowTile;
                _mapService.MapShowFoothold = s.MapShowFoothold;
                _mapService.InvalidateFullRenderCache();
                _backgroundWindow.SetMap(map, _mapService, s.CropX, s.CropY, s.CropW, s.CropH);
                _backgroundWindow.Show();
            }
            RaisePet();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] ToggleBackground: {ex.Message}"); }
    }

    /// <summary>
    /// 无 WZ 时尝试从 PNG 缓存显示背景（静态图分类；摄像机模式无缓存则进设置中心）。
    /// 缓存 key = mapId + 请求 crop；命中则 BackgroundWindow 直接显示缓存图。
    /// M6：离线恢复用 sidecar 里的「裁紧后世界原点」作 EffectiveCrop——PNG 是裁紧图，用请求 crop 会错位落地。
    /// </summary>
    private void TryShowCachedMapBackground(PetSettings s)
    {
        try
        {
            var cached = _cacheManager.LoadMapPng(s.MapId, s.CropX, s.CropY, s.CropW, s.CropH);
            if (cached == null || cached.Length == 0)
            {
                OpenBackgroundTab();
                return;
            }
            // M6：读取裁紧后世界原点（sidecar 缺失时回退请求 crop——旧缓存兼容）
            int trimX = s.CropX, trimY = s.CropY;
            _cacheManager.TryLoadMapPngTrim(s.MapId, s.CropX, s.CropY, s.CropW, s.CropH, out trimX, out trimY);
            _mapViewportWindow.Stop();
            _mapViewportWindow.Hide();
            _backgroundWindow.ShowCachedMapPng(cached, trimX, trimY, s.CropW, s.CropH);
            _backgroundWindow.Show();
            RaisePet();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] TryShowCachedMapBackground: {ex.Message}"); OpenBackgroundTab(); }
    }

    /// <summary>
    /// 切换到摄像机模式（模式2）：后台 LoadMap（失败/无图→进设置中心背景 tab）→
    /// 关静态（BackgroundWindow.HideMap+Hide）→ MapViewportWindow.SetMap+Show + RaisePet；持久化 MapMode=2。
    /// 摄像机窗口的拖动/缩放由其内部处理，不触发 RaisePet 干扰；仅 Show/Hide 时 RaisePet。
    /// F18：经背景闸门（路激活→先完整退出路再切）。
    /// </summary>
    private void SwitchToCameraMode()
    {
        _ = _roadController.GateBackgroundAction(SwitchToCameraModeCoreAsync);
    }

    private async Task SwitchToCameraModeCoreAsync()
    {
        try
        {
            var s = _configService.LoadSettings();
            if (string.IsNullOrEmpty(s.MapId) || !_wzService.IsLoaded) { OpenBackgroundTab(); return; }
            // F18 闸门收编：原 Task.Run + Dispatcher.UIThread.Post 延迟段改为直接 await
            MapInfo? map = null;
            try
            {
                map = await Task.Run(() => _mapService.LoadMap(s.MapId));
            }
            catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] CameraMode LoadMap: {ex.Message}"); }
            try
            {
                if (map == null) { OpenBackgroundTab(); return; }
                _currentMap = map;
                _backgroundWindow.HideMap();
                _backgroundWindow.Hide();
                // 模式2：摄像机——分层 flag 读用户配置（不硬编码，尊重设置中心勾选）
                var s2 = _configService.LoadSettings();
                _mapService.MapShowBack = s2.MapShowBack;
                _mapService.MapShowLife = s2.MapShowLife;
                _mapService.MapShowPortal = s2.MapShowPortal;
                _mapService.MapShowObj = s2.MapShowObj;
                _mapService.MapShowTile = s2.MapShowTile;
                _mapService.MapShowFoothold = s2.MapShowFoothold;
                _mapViewportWindow.SetMap(map, _mapService); // SetMap 会 ClearSpriteCache，新 flag 生效
                _mapViewportWindow.Show();
                RaisePet(); // #1 摄像机窗口 Topmost=true 后紧跟把桌宠置顶
                // PLAN 步骤 8：async gap 前捕获的旧 settings 快照（方法开头的 s）不再整对象回存——
                // 改为 Update 事务（锁内读最新配置只改 MapMode，不覆盖期间其他字段的并发修改，
                // 如音乐 PlayHistory 高频写入；改动语义与原 s.MapMode = 2 完全一致）
                _configService.Update(cfg => { cfg.MapMode = 2; });
            }
            catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] CameraMode Show: {ex.Message}"); }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] SwitchToCameraMode: {ex.Message}"); }
    }

    /// <summary>
    /// 切换到静态图片模式（模式1）：Stop 摄像机（停 timer+清缓存+Hide）→
    /// 复用 _currentMap 恢复 BackgroundWindow（无图则仅隐藏）；持久化 MapMode=1。
    /// F18：经背景闸门（路激活→先完整退出路再切）。
    /// </summary>
    private void SwitchToStaticMode()
    {
        _ = _roadController.GateBackgroundAction(SwitchToStaticModeCoreAsync);
    }

    private async Task SwitchToStaticModeCoreAsync()
    {
        try
        {
            _mapViewportWindow.Stop();
            _mapViewportWindow.Hide();
            var s = _configService.LoadSettings();
            if (_currentMap != null && !string.IsNullOrEmpty(s.MapId) && _wzService.IsLoaded)
            {
                // 模式1：静态——分层 flag 读用户配置（不硬编码，尊重设置中心勾选）
                _mapService.MapShowBack = s.MapShowBack;
                _mapService.MapShowLife = s.MapShowLife;
                _mapService.MapShowPortal = s.MapShowPortal;
                _mapService.MapShowObj = s.MapShowObj;
                _mapService.MapShowTile = s.MapShowTile;
                _mapService.MapShowFoothold = s.MapShowFoothold;
                _mapService.InvalidateFullRenderCache(); // 强制按新 flag 重新渲染
                _backgroundWindow.SetMap(_currentMap, _mapService, s.CropX, s.CropY, s.CropW, s.CropH);
                _backgroundWindow.Show();
                RaisePet();
            }
            s.MapMode = 1;
            _configService.SaveSettings(s);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] SwitchToStaticMode: {ex.Message}"); }
        await Task.CompletedTask;
    }

    /// <summary>Opening 时重建「背景」子菜单（合并版）：开关 + 模式单选（静态/摄像机）+ 交互模式 + 最近使用×4（空→占位）+ 调整地图。</summary>

    private void ApplyMapSelection(MapSelection sel)
    {
        _ = _roadController.GateBackgroundAction(() => ApplyMapSelectionCoreAsync(sel));
    }

    private async Task ApplyMapSelectionCoreAsync(MapSelection sel)
    {
        try
        {
            var map = _mapService.LoadMap(sel.Id);
            if (map == null) return;
            _currentMap = map;
            // 选图应用 = 静态背景模式：先停/关摄像机窗口，保证两种模式互斥（问题：双模式同载）
            _mapViewportWindow.Stop();
            _mapViewportWindow.Hide();
            // 分层 flag 从保存的设置同步（与 ToggleBackground/SwitchToStaticMode/SwitchToCameraMode 一致）：
            // 否则 MapService 停留在类默认值（全开），用户保存的「不显示」层在应用后被渲染出来——
            // 跨会话复现：上一会话在设置里勾掉「背景」，本会话点最近使用/应用按钮 → 桌面静态图仍带出背景层。
            // 同步后必须失效整图缓存（RenderMapFull 缓存键含 flag），强制按新 flag 重新渲染。
            var sApply = _configService.LoadSettings();
            _mapService.MapShowBack = sApply.MapShowBack;
            _mapService.MapShowTile = sApply.MapShowTile;
            _mapService.MapShowObj = sApply.MapShowObj;
            _mapService.MapShowLife = sApply.MapShowLife;
            _mapService.MapShowPortal = sApply.MapShowPortal;
            _mapService.MapShowFoothold = sApply.MapShowFoothold;
            _mapService.InvalidateFullRenderCache();
            _backgroundWindow.SetMap(map, _mapService, sel.CropX, sel.CropY, sel.CropW, sel.CropH);
            _backgroundWindow.Show();
            RaisePet();
            _placePetAfterRender = true; // 落地推迟到渲染完成（OnMapRendered），裁紧值此时才就绪
            var s = _configService.LoadSettings();
            s.MapId = sel.Id;
            s.MapMode = 1; // 应用地图 = 静态模式
            s.CropX = sel.CropX; s.CropY = sel.CropY; s.CropW = sel.CropW; s.CropH = sel.CropH;
            s.MapHistory.RemoveAll(x => x.Id == sel.Id);
            s.MapHistory.Insert(0, new MapHistoryEntry { Id = sel.Id, Name = sel.Name, CropX = sel.CropX, CropY = sel.CropY, CropW = sel.CropW, CropH = sel.CropH });
            if (s.MapHistory.Count > 10) s.MapHistory = s.MapHistory.Take(10).ToList();
            _configService.SaveSettings(s);
            // 场景联动（F17，设计 §11.10）：地图切换成功后通知音乐服务（fire-and-forget 后台任务，
            // 内部自判 SceneLinkEnabled 开关——联动关/无 BGM/同曲跨图 均 no-op 保持当前播放不断曲）。
            // 挂载点在方法成功路径尾部：上方 LoadMap 失败早退（map == null return）与 try 内异常均不经过此处，
            // 图没切成不换歌。
            // F18 地图命令抑制代际：本次背景命令因路退出而让路（闸门先完整退出路）时，尾部的 F17
            // 换歌被抑制一轮——防 OnMapChanged 触发 CurrentChanged 自动重进模式3。
            if (!_roadController.ConsumeMapCommandSuppression())
            {
                _ = _musicService.OnMapChanged(sel.Id);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] ApplyMapSelection: {ex.Message}"); }
        await Task.CompletedTask;
    }

    /// <summary>设置中心分层 toggle 变化后：按当前可见的背景窗口模式重渲染（静态图重 SetMap / 摄像机重 SetMap）。</summary>

    private void ReRenderBackground()
    {
        try
        {
            var s = _configService.LoadSettings();
            if (_currentMap == null || string.IsNullOrEmpty(s.MapId)) return;
            if (_mapViewportWindow.IsVisible)
            {
                _mapViewportWindow.SetMap(_currentMap, _mapService);
            }
            else if (_backgroundWindow.IsVisible)
            {
                _backgroundWindow.SetMap(_currentMap, _mapService, s.CropX, s.CropY, s.CropW, s.CropH);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] ReRenderBackground: {ex.Message}"); }
    }

}
