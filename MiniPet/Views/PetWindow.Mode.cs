using System;
using System.Linq;
using Avalonia;
using Avalonia.Threading;
using MiniPet.Models;
using MiniPet.Services;

namespace MiniPet.Views;

/// <summary>
/// PetWindow 背景模式部分（partial 拆分）：静态图/摄像机模式切换、背景开关、
/// 地图选择应用、地面落地、重渲染。原实现从 PetWindow.axaml.cs 平移，行为完全一致。
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
        try { var settings = _petManager.ShowSettings(_petId); settings?.OpenBackgroundTab(); }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] OpenBackgroundTab: {ex.Message}"); }
    }

    /// <summary>快速关闭背景：清图 + 隐藏全部背景窗口（静态图 + 摄像机）+ 桌宠置顶。</summary>

    private void QuickHideBackground()
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
    }

    /// <summary>开启/关闭背景总开关（背景=静态图+摄像机两个互斥分类的抽象）：任一可见→全关；全关→按 MapMode 开对应分类。</summary>

    private void ToggleBackground()
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
            System.Threading.Tasks.Task.Run(() =>
            {
                MapInfo? map = null;
                try { map = _mapService.LoadMap(s.MapId); }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] ToggleBg LoadMap: {ex.Message}"); }
                Dispatcher.UIThread.Post(() =>
                {
                    if (map == null) { OpenBackgroundTab(); return; }
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
                });
            });
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
    /// </summary>

    private void SwitchToCameraMode()
    {
        try
        {
            var s = _configService.LoadSettings();
            if (string.IsNullOrEmpty(s.MapId) || !_wzService.IsLoaded) { OpenBackgroundTab(); return; }
            System.Threading.Tasks.Task.Run(() =>
            {
                MapInfo? map = null;
                try { map = _mapService.LoadMap(s.MapId); }
                catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] CameraMode LoadMap: {ex.Message}"); }
                Dispatcher.UIThread.Post(() =>
                {
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
                });
            });
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] SwitchToCameraMode: {ex.Message}"); }
    }

    /// <summary>
    /// 切换到静态图片模式（模式1）：Stop 摄像机（停 timer+清缓存+Hide）→
    /// 复用 _currentMap 恢复 BackgroundWindow（无图则仅隐藏）；持久化 MapMode=1。
    /// </summary>

    private void SwitchToStaticMode()
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
            _configService.UpdateSettings(st => st.MapMode = 1);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] SwitchToStaticMode: {ex.Message}"); }
    }

    /// <summary>Opening 时重建「背景」子菜单（合并版）：开关 + 模式单选（静态/摄像机）+ 交互模式 + 最近使用×4（空→占位）+ 调整地图。</summary>

    private void ApplyMapSelection(MapSelection sel)
    {
        try
        {
            // V0.1.3：地图背景只挂主宠——右键菜单已拦，但设置中心也能触发本方法（背景页「应用到主窗口」
            // / 点历史项），不拦的话非主宠也能挂上背景，与主宠的背景互相遮挡（背景/整图缓存是全局单份）。
            if (!_isPrimaryRole)
            {
                ShowInfoPopup("背景", "地图背景只挂在主宠上。\n请先在右键菜单「🐾 桌宠 → ⭐ 设为主宠」把这只设为主宠，或在主宠上应用。");
                return;
            }
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
            // V0.1.3 + V0.2.1：改走事务更新（锁内读最新配置只写地图字段），既不回滚其他宠物写入的实例数据，
            // 也不覆盖期间音乐 PlayHistory 等并发写入（原写法持旧快照整份 SaveSettings）。
_configService.Update(st =>
            {
                st.MapId = sel.Id;
                st.MapMode = 1; // 应用地图 = 静态模式
                st.CropX = sel.CropX; st.CropY = sel.CropY; st.CropW = sel.CropW; st.CropH = sel.CropH;
                st.MapHistory.RemoveAll(x => x.Id == sel.Id);
                // 写历史前重解析名字（存量坏数据 sel.Name==id 会被洗成 WZ 中文名，修「历史菜单中文缺失」）
                var resolvedName = _wzService.ResolveDisplayName(sel.Id, sel.Name);
                st.MapHistory.Insert(0, new MapHistoryEntry { Id = sel.Id, Name = resolvedName, CropX = sel.CropX, CropY = sel.CropY, CropW = sel.CropW, CropH = sel.CropH });
                if (st.MapHistory.Count > 10) { st.MapHistory = st.MapHistory.Take(10).ToList(); }
            });
            // 场景联动（F17，设计 §11.10）：地图切换成功后通知音乐服务（fire-and-forget 后台任务，
            // 内部自判 SceneLinkEnabled 开关——联动关/无 BGM/同曲跨图 均 no-op 保持当前播放不断曲）。
            _ = _musicService.OnMapChanged(sel.Id);

        }
        catch (Exception ex) { Console.Error.WriteLine($"[PetWindow] ApplyMapSelection: {ex.Message}"); }
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
