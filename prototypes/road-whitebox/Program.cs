using Avalonia;
using Avalonia.Headless;
using MiniPet;
using Avalonia.Threading;
using MiniPet.Models;
using MiniPet.Services;
using MiniPet.Services.Road;
using SkiaSharp;

namespace RoadWhitebox;

/// <summary>
/// F18 无限之路 白盒渲染验证（I4）：
/// force-map = 100000000（跳过解析器/回退链，RoadMaterialExtractor 直接提取指定图）→
/// InfiniteRoadGenerator v13 真实管线铺带（块池洗牌 + 无限平地）→ InfiniteRoadRenderer.RenderFrame
/// 出 3 个滚动位置的 PNG，目检三点：
/// ①草线水平（无错位台阶）②无断裂（柱间无白缝/黑洞）③obj 贴地（站地带件落草线上）。
/// 运行（工程根）：dotnet run --project prototypes/road-whitebox → 输出 out/wb_t{0,1,2}.png
/// </summary>
public static class Program
{
    private const string ForceMapId = "100000000"; // 射手村（v13 基准图：主街组稳定、obj 丰富）
    private const int WinW = 800;
    private const int WinH = 600;
    private const int InitSurf = 300; // 路世界表面 y（垂直映射锚 InitialSurfaceY）

    public static int Main(string[] args)
    {
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        var exitCode = 0;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                exitCode = Run();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[whitebox] error: {ex}");
                exitCode = 1;
            }
            Environment.Exit(exitCode);
        });
        Dispatcher.UIThread.MainLoop(CancellationToken.None);
        return exitCode;
    }

    private static int Run()
    {
        // 第一步：WZ 环境（照 RoadV5Probe.Init——ConfigService.LoadWzConfig 是唯一权威路径来源）
        var config = new ConfigService();
        var wzConfig = config.LoadWzConfig();
        var wz = new WzService();
        var (ok, _) = wz.LoadWz(wzConfig.WzLibPath, wzConfig.BaseWzPath);
        Console.WriteLine($"[whitebox] WZ 加载: {ok}");
        if (!ok) { Environment.Exit(2); }
        var mapSvc = new MapService(wz, new CacheManager());

        // 第二步：force-map 提取（跳过 RoadMapResolver/回退链——指定图直提取）
        RoadMaterialExtractor.Configure(mapSvc, wz);
        var state = new RoadState();
        var pack = RoadMaterialExtractor.Extract(ForceMapId, CancellationToken.None, state);
        if (pack == null)
        {
            Console.WriteLine($"[whitebox] 提取失败: {state.LastError}");
            return 3;
        }
        Console.WriteLine($"[whitebox] {ForceMapId} {wz.GetMapName(ForceMapId)}: 块池={pack.FlatColumns.Count} 坡={pack.SlantColumns.Count} mainSurf={pack.MainSurfaceH} 连续率={pack.ContinuityRatio:F2} usable={pack.Usable} anchor=({pack.AnchorX},{pack.AnchorY})");
        foreach (var b in pack.FlatColumns)
        {
            int minDx = b.Tiles.Count > 0 ? b.Tiles.Min(t => t.Dx) : 0;
            int maxDx = b.Tiles.Count > 0 ? b.Tiles.Max(t => t.Dx) : 0;
            Console.WriteLine($"[whitebox]   块 id={b.Id} 宽={b.WidthCols}柱 tiles={b.Tiles.Count} objs={b.Objs.Count} Dx=[{minDx}..{maxDx}]（块像素宽={b.WidthCols * RoadConstants.ColW}）");
        }

        // 第三步：v13 生成器真实管线铺带（块池洗牌 + 无限平地——SurfaceY 全带统一）
        var gen = new InfiniteRoadGenerator(pack, startWorldX: WinW, startSurfaceY: InitSurf, new Random(7));
        Console.WriteLine($"[whitebox] 生成器: 初始视图={gen.ActiveColumns.Count} 块 ScrollX={gen.ScrollX:F0} InitialSurfaceY={gen.InitialSurfaceY}");

        // 第四步：back 克隆拆分（ScreenMode 归一化在 ToRoadBack）→ RoadSky（冻结锚点）
        var backs = pack.Backs.Select(RoadMaterialExtractor.ToRoadBack).ToList();
        foreach (var b in backs)
        {
            // 路径口径照抄 InfiniteRoadRenderer.BackPath（ani=1 → ani/{no}/0，否则 back/{no}）
            string path = b.SpineAni == 1
                ? $"Map/Back/{b.BS}/ani/{b.No}/0"
                : $"Map/Back/{b.BS}/back/{b.No}";
            SKBitmap? probe = null;
            try
            {
                var png = wz.WithWzLock(() => wz.ExtractPng(path));
                if (png != null && png.Length > 0) { probe = SKBitmap.Decode(png); }
            }
            catch { /* MISS 记录即可 */ }
            int bottom = probe == null ? -1 : b.Y + probe.Height;
            Console.WriteLine($"[whitebox]   back type={b.Type} no={b.No} front={b.Front} sm={b.ScreenMode} x={b.X} y={b.Y} rx={b.Rx} ry={b.Ry} cx={b.Cx} cy={b.Cy} tm={b.TileMode} size={(probe == null ? "MISS" : probe.Width + "x" + probe.Height)} srcBottom={bottom} path={path}");
        }
        var sky = new RoadSky(
            backs.Where(b => !b.Front).ToList(),
            backs.Where(b => b.Front).ToList(),
            pack.AnchorX, pack.AnchorY, InitSurf, gen.ScrollX);

        // 第五步：3 个滚动位置出图（0 / 400 / 800 px——Advance 真实推进后取视图快照）
        var outDir = Path.Combine(Directory.GetCurrentDirectory(), "out");
        Directory.CreateDirectory(outDir);
        using var renderer = new InfiniteRoadRenderer(wz);
        double edge = gen.ScrollX + gen.WindowWidth;
        for (int i = 0; i < 3; i++)
        {
            if (i > 0)
            {
                edge += 400; // 每帧右移 400px（真实 Advance 推进——验证滚动过程中草线连续）
                gen.Advance(edge);
            }
            var road = new RoadRenderRoad(
                BuildGeneration: 1,
                MapId: pack.MapId,
                TileSetName: pack.TileSetName,
                Columns: gen.ActiveColumns,
                Entities: Array.Empty<RoadEntityState>(),
                FarObjs: Array.Empty<FarObjEntry>(),
                Decors: Array.Empty<RoadDecor>(),
                ScrollOriginX: gen.ScrollX,
                InitialSurfaceY: InitSurf,
                SourceAnchorX: pack.AnchorX,
                SourceAnchorY: pack.AnchorY);
            var frame = new RoadFrame(
                RoadPhase.Running, false, sky, road, null, 0, 0.0, i * 3000.0, 16.6);
            var bmp = renderer.RenderFrame(frame, WinW, WinH);
            if (bmp == null)
            {
                Console.WriteLine($"[whitebox] 帧 {i} 渲染 null");
                return 4;
            }
            string path = Path.GetFullPath(Path.Combine(outDir, $"wb_t{i}.png"));
            using (var img = SKImage.FromBitmap(bmp))
            using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
            using (var fs = File.OpenWrite(path))
            {
                data.SaveTo(fs);
            }
            Console.WriteLine($"[whitebox] 出图 scroll={gen.ScrollX:F0} cols={road.Columns.Count} → {path} ({bmp.Width}x{bmp.Height})");
        }
        return 0;
    }
}
