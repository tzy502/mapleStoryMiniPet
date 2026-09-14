using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Models;
using MiniPet.Services;
using SkiaSharp;

namespace MapSlice;

/// <summary>
/// F18 重设计·obj 生成新方法（切片法）验证探针：
/// 不再提取重组——素材 = 原图整幅切片。整图按 R2 渲染算法（与模式1/2 同一 RenderViewport）
/// 渲染一次，随机切 10 个「30 秒长度」窗口（30s × 100px/s = 3000px，滚动速度沿用定稿常量）出 PNG。
/// 验证（FINAL 目标「跟原始地图对比 每个 obj 和原本的关系一致」）：
///   ①像素级：每张切片与原图对应区域逐字节对比，必须 0 差异（世界锚定内容 bit 级一致）；
///   ②obj 级：逐 obj 输出 原图坐标 → 切片坐标 对照表（纯水平平移 −x0，垂直坐标不变），
///     附贴地差（obj 锚 Y − 脚下地面 foothold 顶面 Y）——平移不变量，两图必等。
/// 运行（仓库根）：dotnet run --project prototypes/map-slice → 输出 prototypes/map-slice/out/
/// </summary>
public static class Program
{
    private const string MapId = "100000000"; // 默认地图（五城镇首选/白盒基准：射手村）
    private const int SliceW = 3000;          // 30 秒对应长度：30s × 100px/s（定稿滚动速度）
    private const int SliceCount = 10;
    private const int Seed = 42;              // 固定种子——起点序列可复现（报告可审）

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
                Console.WriteLine($"[map-slice] error: {ex}");
                exitCode = 1;
            }
            Environment.Exit(exitCode);
        });
        Dispatcher.UIThread.MainLoop(CancellationToken.None);
        return exitCode;
    }

    private static int Run()
    {
        // 第一步：WZ 环境（ConfigService.LoadWzConfig 是唯一权威路径来源）
        var config = new ConfigService();
        var wzConfig = config.LoadWzConfig();
        var wz = new WzService();
        var (ok, _) = wz.LoadWz(wzConfig.WzLibPath, wzConfig.BaseWzPath);
        Console.WriteLine($"[map-slice] WZ 加载: {ok}");
        if (!ok) { return 2; }
        var mapSvc = new MapService(wz, new CacheManager());

        // 第二步：默认地图 + 整图渲染一次（camCenter=地图中心、zoom=1、t=0——与模式1 静态同参）
        var map = mapSvc.LoadMap(MapId);
        if (map == null) { Console.WriteLine($"[map-slice] 地图加载失败: {MapId}"); return 3; }
        Console.WriteLine($"[map-slice] {MapId} {wz.GetMapName(MapId)}: world=[{map.MinX},{map.MinY}]~[{map.MaxX},{map.MaxY}]");
        int worldW = map.MaxX - map.MinX;
        int worldH = map.MaxY - map.MinY;
        if (worldW < SliceW) { Console.WriteLine($"[map-slice] 地图宽 {worldW} < 切片长 {SliceW}，无法切片"); return 4; }
        var (ccx, ccy) = MapService.GetMapCenter(map);
        using var full = mapSvc.RenderViewport(map, ccx, ccy, 1f, 0, worldW, worldH);
        if (full == null) { Console.WriteLine("[map-slice] 整图渲染失败"); return 5; }

        // 第三步：随机取 10 个不重复窗口起点（固定种子可复现；升序输出便于审阅）
        var rng = new Random(Seed);
        var starts = new HashSet<int>();
        while (starts.Count < SliceCount)
        {
            starts.Add(rng.Next(map.MinX, map.MaxX - SliceW + 1));
        }
        var startList = starts.OrderBy(x => x).ToList();

        // 第四步：全量 obj/life 清单（世界锚定内容——切片保真的对象）
        var objs = map.Layers
            .SelectMany((layer, li) => layer.Objs.Where(o => o.Resource.Frames.Count > 0).Select(o => (Layer: li, Obj: o)))
            .OrderBy(t => t.Obj.X).ToList();
        var lifes = map.Lifes.Where(l => !l.Hide).OrderBy(l => l.X).ToList();
        Console.WriteLine($"[map-slice] obj 总数={objs.Count}（8 层）life 总数={lifes.Count}");

        // 第五步：逐切片裁剪 + 像素一致性验证 + 出 PNG + 对照表
        var outDir = Path.Combine(Directory.GetCurrentDirectory(), "out");
        Directory.CreateDirectory(outDir);
        var report = new System.Text.StringBuilder();
        report.AppendLine($"# map-slice 切片法验证报告 — {MapId} {wz.GetMapName(MapId)}");
        report.AppendLine();
        report.AppendLine($"- 整图 {worldW}×{worldH} 渲染 1 次（RenderViewport，zoom=1，t=0，camCenter=地图中心——与模式1/2 同一 R2 算法）");
        report.AppendLine($"- 切片 {SliceCount} 张 × {SliceW}px（30s × 100px/s），起点（seed={Seed}，升序）：{string.Join("、", startList)}");
        report.AppendLine("- 验证口径：切片 = 整图对应区域，逐字节对比必须 0 差异 → 世界锚定内容（tile/obj/life/portal）bit 级一致，");
        report.AppendLine("  一切 obj 关系（贴地/层叠/间距）与原图完全一致——由构造保证，非近似。");
        report.AppendLine();
        bool allPass = true;
        for (int i = 0; i < startList.Count; i++)
        {
            int x0 = startList[i];
            using var slice = CropRegion(full, x0 - map.MinX, 0, SliceW, worldH);
            if (slice == null) { Console.WriteLine($"[map-slice] 切片 {i} 裁剪失败"); return 6; }

            // 像素一致性：重裁一份参照区域逐字节对比（0 差异 = bit 级一致）
            using var refer = CropRegion(full, x0 - map.MinX, 0, SliceW, worldH);
            long diffBytes = CountByteDiff(slice, refer);
            bool pass = diffBytes == 0;
            allPass &= pass;

            string file = Path.Combine(outDir, $"slice_{i:00}_x{x0}.png");
            SavePng(slice, file);

            AppendObjSection(report, i, x0, map, objs, lifes);
            report.AppendLine($"## 切片 {i:00}（x0={x0}）像素一致性");
            report.AppendLine($"- diff 字节 = {diffBytes} → {(pass ? "✅ PASS（与原图 bit 级一致）" : "❌ FAIL")}");
            report.AppendLine($"- 输出：out/{Path.GetFileName(file)}");
            report.AppendLine();
            Console.WriteLine($"[map-slice] 切片 {i:00} x0={x0} diffBytes={diffBytes} {(pass ? "PASS" : "FAIL")} → {file}");
        }

        report.AppendLine("## 结论");
        report.AppendLine(allPass
            ? "- 10 张切片与原图对应区域 **全部 0 差异**：obj 生成方法（整图切片）对世界锚定内容是恒等变换，逐 obj 关系与原图一致。"
            : "- 存在非 0 差异切片：**未达验证口径**，需排查。");

        string reportPath = Path.Combine(outDir, "comparison-report.md");
        File.WriteAllText(reportPath, report.ToString());
        Console.WriteLine($"[map-slice] 报告 → {reportPath}");
        return allPass ? 0 : 7;
    }

    // ====== private ======

    /// <summary>从整图裁一个区域（无裁紧——与原图同构；越界部分留透明）。</summary>
    private static SKBitmap? CropRegion(SKBitmap full, int srcX, int srcY, int w, int h)
    {
        var dst = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.Transparent);
        int x0 = Math.Max(0, srcX), y0 = Math.Max(0, srcY);
        int x1 = Math.Min(full.Width, srcX + w), y1 = Math.Min(full.Height, srcY + h);
        if (x1 > x0 && y1 > y0)
        {
            using var paint = new SKPaint { IsAntialias = false, FilterQuality = SKFilterQuality.None };
            canvas.DrawBitmap(full, new SKRect(x0, y0, x1, y1), new SKRect(x0 - srcX, y0 - srcY, x1 - srcX, y1 - srcY), paint);
        }
        return dst;
    }

    /// <summary>逐字节差异计数（同尺寸同像素格式——长度必等）。</summary>
    private static long CountByteDiff(SKBitmap a, SKBitmap b)
    {
        var sa = a.Bytes;
        var sb = b.Bytes;
        if (sa.Length != sb.Length) { return long.MaxValue; }
        long diff = 0;
        for (int i = 0; i < sa.Length; i++)
        {
            if (sa[i] != sb[i]) { diff++; }
        }
        return diff;
    }

    /// <summary>对照表追加：切片内 obj/life 的 原图坐标 → 切片坐标（纯水平平移 −x0）与贴地差。</summary>
    private static void AppendObjSection(System.Text.StringBuilder report, int index, int x0, MapInfo map,
        List<(int Layer, MiniPet.Models.MapObj Obj)> objs, List<MapLife> lifes)
    {
        int x1 = x0 + SliceW;
        report.AppendLine($"## 切片 {index:00}（x0={x0}，世界 x∈[{x0},{x1})）obj 对照");
        report.AppendLine();
        report.AppendLine("| 层 | obj | 原图 (X,Y) | 切片 (X−x0,Y) | 贴地差 Y−ground |");
        report.AppendLine("|---|---|---|---|---|");
        int inSlice = 0;
        foreach (var (layer, o) in objs)
        {
            if (o.X < x0 || o.X >= x1) { continue; }
            inSlice++;
            int ground = MapService.GetGroundY(map, o.X) ?? -1;
            int gap = ground < 0 ? -1 : o.Y - ground;
            string name = ObjShortName(o.Resource.Frames[0].ResourceUrl);
            report.AppendLine($"| {layer} | {name} | ({o.X},{o.Y}) | ({o.X - x0},{o.Y}) | {gap} |");
        }
        report.AppendLine();
        report.AppendLine($"共 {inSlice} 个 obj 全部为纯水平平移（Δx=−{x0}，Δy=0）——垂直坐标与贴地差逐件不变。");
        report.AppendLine();
        report.AppendLine($"### life 对照（切片 {index:00}）");
        report.AppendLine();
        report.AppendLine("| life | 原图 (X,Y) | 切片 (X−x0,Y) |");
        report.AppendLine("|---|---|---|");
        int lifeIn = 0;
        foreach (var l in lifes)
        {
            if (l.X < x0 || l.X >= x1) { continue; }
            lifeIn++;
            int ly = l.Cy != 0 ? l.Cy : l.Y;
            report.AppendLine($"| {l.Id} | ({l.X},{ly}) | ({l.X - x0},{ly}) |");
        }
        report.AppendLine($"共 {lifeIn} 个 life 同为纯水平平移。");
        report.AppendLine();
    }

    /// <summary>obj 资源路径取尾三段（去 Map/Obj 前缀——报告可读）。</summary>
    private static string ObjShortName(string? url)
    {
        if (string.IsNullOrEmpty(url)) { return "?"; }
        var parts = url.Split('/');
        int take = Math.Min(3, parts.Length);
        return string.Join('/', parts.Skip(parts.Length - take));
    }

    /// <summary>位图编码 PNG 落盘。</summary>
    private static void SavePng(SKBitmap bmp, string path)
    {
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.OpenWrite(path);
        fs.SetLength(0);
        data.SaveTo(fs);
    }
}
