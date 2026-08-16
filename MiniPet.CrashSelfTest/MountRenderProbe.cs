using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Models;
using MiniPet.Services;
using SkiaSharp;
using WzComparerR2.WzLib;

namespace MiniPet.CrashSelfTest;

/// <summary>
/// 坐骑渲染位置 probe（R19 渲染对齐验证）：
/// ① 合成带坐骑/无坐骑的 stand1/0 帧，PNG 存 /tmp/mount-render/{id}.png；
/// ② 逐像素分析：坐骑像素 bbox、角色像素 bbox、两者相对位置（坐骑应在角色下方 = 角色骑在坐骑上）；
/// ③ 逐部件几何对照：每个坐骑部件在本管线（锚到身体 navel）与 MapleSalon2（-origin 相对坐骑 navel）
///    的世界落点差异 → 定位渲染差异根因（部件丢失 / 锚点错位 / z 序）。
/// 用法：dotnet run --project MiniPet.CrashSelfTest -p:UsedAvaloniaProducts= -p:StartupObject=MiniPet.CrashSelfTest.MountRenderProbe
/// </summary>
public static class MountRenderProbe
{
    public static int Main(string[] args)
    {
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        var exitCode = 0;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                Run();
                exitCode = 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"probe error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                exitCode = 1;
            }
            Environment.Exit(exitCode);
        });
        Dispatcher.UIThread.MainLoop(CancellationToken.None);
        return exitCode;
    }

    // 代表坐骑：普通 01902000 / 飞行 characterAction 01930001 / hideBody 全覆盖 01932294 / 随机 01902001 01902002 01992033
    private static readonly string[] MountIds = { "01902000", "01930001", "01932294", "01902001", "01902002", "01992033" };

    private static void Run()
    {
        var basePath = Environment.GetEnvironmentVariable("MINIPET_WZ_BASE");
        var config = new ConfigService();
        var wzConfig = config.LoadWzConfig();
        var wz = new WzService();
        var (ok, _) = wz.LoadWz(wzConfig.WzLibPath, basePath ?? wzConfig.BaseWzPath);
        Console.WriteLine($"WZ 加载: {ok} base={basePath ?? wzConfig.BaseWzPath}");
        if (!ok) return;

        var cache = new CacheManager();
        var paperdoll = new PaperdollService(wz, cache, new SpriteService(cache));
        string outDir = "/tmp/mount-render";
        Directory.CreateDirectory(outDir);

        // 基础外观（与 MountActionProbe 一致：30000 发型 / 20000 脸型 / body 2000）
        var hair = FindFirstItem(wz, new[] { "30000", "31000", "33000", "38000" }, "Hair");
        var face = FindFirstItem(wz, new[] { "20000", "21000", "23000", "28000" }, "Face");
        Console.WriteLine($"基础发型: {(hair != null ? hair.Id : "无")}  基础脸型: {(face != null ? face.Id : "无")}");

        // body 锚点基准（MapleSalon2 容器原点 = body/origin + body/map/navel；本管线画布原点 = body Anchor）
        var bodyMeta = GetPieceMeta(wz, "Character/00002000.img/stand1/0/body");
        Console.WriteLine($"\nbody(00002000) stand1/0: origin=({bodyMeta.Ox},{bodyMeta.Oy}) map=[{FormatMap(bodyMeta.Map)}]");

        // zmap 中坐骑相关层（判断坐骑落层/相对角色 z 序）
        Console.WriteLine("\n== zmap 关键层 index（index 0 = 最顶层，小=后画=在前）==");
        var zmap = GetZmapNames(wz);
        string[] keyLayers = { "tamingMobFront", "mobEquipMid", "saddleFront", "mobEquipUnderSaddle", "tamingMobMid",
            "characterStart", "face", "cap", "hairOverHead", "hairBelowHead", "head", "hair", "body", "backBody", "tamingMobRear", "backTamingMobFront", "backTamingMobMid" };
        foreach (var layer in keyLayers)
        {
            int idx = zmap.IndexOf(layer);
            Console.WriteLine($"  {layer,-24} zmap[{idx}]");
        }
        Console.WriteLine("  坐骑相关层全列:");
        for (int i = 0; i < zmap.Count; i++)
        {
            if (zmap[i].Contains("TamingMob", StringComparison.OrdinalIgnoreCase) ||
                zmap[i].Contains("taming", StringComparison.OrdinalIgnoreCase) ||
                zmap[i].Contains("saddle", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"    zmap[{i}] = {zmap[i]}");
            }
        }

        foreach (var id in MountIds)
        {
            string root = $"Character/TamingMob/{id}.img";
            if (!wz.WithWzLock(() => wz.FindNodeByPath(root) != null))
            {
                Console.WriteLine($"\n== [{id}] {root} 不存在，跳过 ==");
                continue;
            }
            AnalyzeMount(wz, paperdoll, id, root, hair, face, outDir);
        }
    }

    private static void AnalyzeMount(WzService wz, PaperdollService paperdoll, string id, string root,
        ItemInfo? hair, ItemInfo? face, string outDir)
    {
        var appearance = new CharacterAppearance
        {
            Gender = 0,
            BodyId = 2000,
            Ear = PaperdollService.EarHuman,
            Hair = hair ?? new ItemInfo(),
            Face = face ?? new ItemInfo(),
            Mount = new MountInfo { Id = id },
            EnableEffect = true,
        };
        Console.WriteLine($"\n========== [{id}] {root} ==========");

        // ① 坐骑动作解析（复制 ResolveMountAction 逻辑）：stand1 → 坐骑动作
        string mountAction = ResolveMountAction(wz, root, "stand1");
        Console.WriteLine($"  角色 stand1 → 坐骑动作: {mountAction}");
        int mountFrames = GetActionFrameCount(wz, $"{root}/{mountAction}");
        if (mountFrames <= 0)
        {
            Console.WriteLine($"  坐骑动作 {mountAction} 无帧，跳过");
            return;
        }
        int mountFrame = Math.Min(0, mountFrames - 1);
        string framePath = $"{root}/{mountAction}/{mountFrame}";
        var pieceNames = wz.WithWzLock(() => wz.GetFramePieceNames(framePath))
            .Where(n => n != "delay" && n != "move" && n != "a0" && n != "a1").ToList();
        Console.WriteLine($"  坐骑帧 {mountAction}/{mountFrame}: {pieceNames.Count} 个部件 [{string.Join(", ", pieceNames)}]");

        // ② 坐骑帧主 navel（MapleSalon2 getFrameNavel = 首个含 map.navel 的部件）
        (int x, int y)? mainNavel = null;
        var pieceMetas = new List<(string Name, PieceMeta Meta)>();
        foreach (var name in pieceNames)
        {
            var meta = GetPieceMeta(wz, $"{framePath}/{name}");
            pieceMetas.Add((name, meta));
            if (mainNavel == null && meta.Map.TryGetValue("navel", out var nv)) mainNavel = nv;
        }
        Console.WriteLine($"  坐骑帧主 navel (MapleSalon2 getFrameNavel): {(mainNavel == null ? "无" : $"({mainNavel.Value.x},{mainNavel.Value.y})")}");

        // ③ 部件几何对照：本管线 vs MapleSalon2（世界坐标 = 角色 body navel 为原点）
        //    本管线：mount Anchor = navelRoot(0,0) - map.navel；画布原点 bx = body Anchor = -bodyMap.navel。
        //    drawPos = Anchor - origin - bx（mv=0）。
        //    MapleSalon2：mount piece 画布左上 = -origin（坐骑容器坐标）；角色 navel 放坐骑主 navel。
        //    统一到本管线画布（角色 body navel 世界 = bodyNavel）：MapleSalon2 drawPos = -origin - mainNavel + bodyNavel。
        //    本管线（R19 修复后）：全部坐骑部件锚到帧主 navel → drawPos = -mainNavel - origin + bodyNavel（bx = -bodyNavel）。
        var bodyMap = GetPieceMeta(wz, "Character/00002000.img/stand1/0/body").Map;
        var bodyNavel = bodyMap.TryGetValue("navel", out (int x, int y) bn) ? bn : (x: 0, y: 0);
        Console.WriteLine($"  body map.navel = ({bodyNavel.x},{bodyNavel.y})");
        Console.WriteLine($"  {"部件",-18}{"origin",-14}{"map",-34}{"本管线落点",-16}{"MapleSalon2落点",-16}{"Δ(本-参考)",-16}{"z字段"}");
        bool anyDelta = false;
        foreach (var (name, meta) in pieceMetas)
        {
            // 本管线（R19 修复后）：全部坐骑部件统一锚到帧主 navel（无 navel 部件同样渲染）
            int ourX = mainNavel == null ? -meta.Ox + bodyNavel.x : -mainNavel.Value.x - meta.Ox + bodyNavel.x;
            int ourY = mainNavel == null ? -meta.Oy + bodyNavel.y : -mainNavel.Value.y - meta.Oy + bodyNavel.y;
            // MapleSalon2（统一到本画布：-origin - mainNavel + bodyNavel）
            int refX = mainNavel == null ? -meta.Ox + bodyNavel.x : -meta.Ox - mainNavel.Value.x + bodyNavel.x;
            int refY = mainNavel == null ? -meta.Oy + bodyNavel.y : -meta.Oy - mainNavel.Value.y + bodyNavel.y;
            string mapStr = FormatMap(meta.Map);
            string delta = "—";
            if (mainNavel != null)
            {
                int dx = ourX - refX, dy = ourY - refY;
                if (dx != 0 || dy != 0)
                {
                    anyDelta = true;
                    delta = $"({dx},{dy})";
                }
                else
                {
                    delta = "相同";
                }
            }
            Console.WriteLine($"  {name,-18}({meta.Ox},{meta.Oy})".PadRight(2 + 18 + 14) +
                $"{mapStr,-34}{$"({ourX},{ourY})",-16}{(mainNavel == null ? "—" : $"({refX},{refY})"),-16}{delta,-16}{meta.ZField}");
        }
        Console.WriteLine($"  几何对照结论: {(anyDelta ? "⚠ 存在部件落点与 MapleSalon2 不一致（Δ≠0）" : "✓ 本管线与 MapleSalon2 落点全部一致")}");

        // ③b 真实管线落层（ProbeLayerResolution：CollectPieces→ResolveAnchors→AssignLayers→ApplyLocks）
        var items = paperdoll.ProbeLayerResolution(appearance, "stand1", 0);
        var mountItems = items.Where(x => x.Category == "mount").ToList();
        var charItems = items.Where(x => x.Category != "mount").ToList();
        Console.WriteLine($"  管线落层: 角色部件 {charItems.Count} 件 / 坐骑部件 {mountItems.Count} 件");
        foreach (var mi in mountItems)
        {
            Console.WriteLine($"    坐骑[{mi.PieceName}] z字段={mi.ZField} → 层 {mi.ResolvedLayer}(ZIndex {mi.ZIndex}) locked={mi.Locked} hidden={mi.Hidden}");
        }

        // ④ 合成 + 像素分析（角色 navel 为世界原点；无坐骑合成 = 角色轮廓）
        var pngWith = paperdoll.CompositeFrame(appearance, "stand1", 0);
        if (pngWith == null || pngWith.Length == 0)
        {
            Console.WriteLine($"  合成失败（无输出）");
            return;
        }
        File.WriteAllBytes($"{outDir}/{id}.png", pngWith);
        var noMount = new CharacterAppearance
        {
            Gender = 0, BodyId = 2000, Ear = PaperdollService.EarHuman,
            Hair = hair ?? new ItemInfo(), Face = face ?? new ItemInfo(),
            Mount = null, EnableEffect = true,
        };
        var pngNo = paperdoll.CompositeFrame(noMount, "stand1", 0);
        if (pngNo == null || pngNo.Length == 0)
        {
            Console.WriteLine($"  无坐骑合成失败");
            return;
        }
        File.WriteAllBytes($"{outDir}/{id}_nomount.png", pngNo);
        paperdoll.SetAppearance(appearance);
        var (bw, bh, box, boy) = paperdoll.GetActionBounds("stand1");
        paperdoll.SetAppearance(noMount);
        var (nw, nh, nox, noy) = paperdoll.GetActionBounds("stand1");
        AnalyzePixels(wz, paperdoll, appearance, noMount, box, boy, nox, noy, id, outDir);
        Console.WriteLine($"  PNG: {outDir}/{id}.png ({pngWith.Length} 字节) / {outDir}/{id}_nomount.png");
    }

    /// <summary>
    /// 逐像素分析（世界坐标对齐，参考 CapeRenderProbe.ComputeDiff）：
    /// diff 像素 = 坐骑可见像素（带坐骑合成 且 无坐骑合成 无该像素 或 不同）；
    /// 角色轮廓 = 无坐骑合成不透明像素。
    /// 输出：两者 bbox / 中心 / 相对位置 / 骑乘合理性判定（坐骑应在角色下方：坐骑底 > 角色底，角色中心在坐骑中心上方）。
    /// </summary>
    private static void AnalyzePixels(WzService wz, PaperdollService paperdoll, CharacterAppearance withMount, CharacterAppearance noMount,
        int oxA, int oyA, int oxB, int oyB, string id, string outDir)
    {
        var pngA = paperdoll.CompositeFrame(withMount, "stand1", 0);
        var pngB = paperdoll.CompositeFrame(noMount, "stand1", 0);
        if (pngA == null || pngB == null) return;
        using var bmpA = SKBitmap.Decode(pngA);
        using var bmpB = SKBitmap.Decode(pngB);
        int aw = bmpA.Width, ah = bmpA.Height, bw2 = bmpB.Width, bh2 = bmpB.Height;
        int wMinX = Math.Min(-oxA, -oxB), wMinY = Math.Min(-oyA, -oyB);
        int wMaxX = Math.Max(-oxA + aw, -oxB + bw2), wMaxY = Math.Max(-oyA + ah, -oyB + bh2);
        int W = wMaxX - wMinX, H = wMaxY - wMinY;
        if (W <= 0 || H <= 0) return;

        using var pixA = bmpA.PeekPixels();
        using var pixB = bmpB.PeekPixels();
        // 角色轮廓 bbox（无坐骑合成）
        int cMinX = W, cMinY = H, cMaxX = -1, cMaxY = -1;
        // 坐骑 bbox（diff 且 与角色轮廓不同像素）
        int mMinX = W, mMinY = H, mMaxX = -1, mMaxY = -1;
        int charPixels = 0, mountPixels = 0, samePixels = 0;
        unsafe
        {
            byte* pa = (byte*)pixA.GetPixels();
            byte* pb = (byte*)pixB.GetPixels();
            for (int dy = 0; dy < H; dy++)
            {
                for (int dx = 0; dx < W; dx++)
                {
                    int wx = wMinX + dx, wy = wMinY + dy;
                    int ax = wx + oxA, ay = wy + oyA;
                    int bx = wx + oxB, by = wy + oyB;
                    bool aIn = ax >= 0 && ax < aw && ay >= 0 && ay < ah;
                    bool bIn = bx >= 0 && bx < bw2 && by >= 0 && by < bh2;
                    int ai = aIn ? (ay * aw + ax) * 4 : -1;
                    int bi = bIn ? (by * bw2 + bx) * 4 : -1;
                    bool aOpaque = ai >= 0 && pa[ai + 3] > 30;
                    bool bOpaque = bi >= 0 && pb[bi + 3] > 30;
                    if (bOpaque)
                    {
                        charPixels++;
                        if (dx < cMinX) cMinX = dx;
                        if (dy < cMinY) cMinY = dy;
                        if (dx > cMaxX) cMaxX = dx;
                        if (dy > cMaxY) cMaxY = dy;
                    }
                    if (!aOpaque) continue;
                    bool same = aIn && bIn && pa[ai] == pb[bi] && pa[ai + 1] == pb[bi + 1] && pa[ai + 2] == pb[bi + 2] && pa[ai + 3] == pb[bi + 3];
                    if (same) { samePixels++; continue; }
                    mountPixels++;
                    if (dx < mMinX) mMinX = dx;
                    if (dy < mMinY) mMinY = dy;
                    if (dx > mMaxX) mMaxX = dx;
                    if (dy > mMaxY) mMaxY = dy;
                }
            }
        }
        int cCx = cMaxX < 0 ? -1 : (cMinX + cMaxX) / 2;
        int cCy = cMaxY < 0 ? -1 : (cMinY + cMaxY) / 2;
        int mCx = mMaxX < 0 ? -1 : (mMinX + mMaxX) / 2;
        int mCy = mMaxY < 0 ? -1 : (mMinY + mMaxY) / 2;
        Console.WriteLine($"\n  [像素] 角色轮廓 {charPixels}px bbox=({cMinX},{cMinY})-({cMaxX},{cMaxY}) 中心=({cCx},{cCy})");
        Console.WriteLine($"  [像素] 坐骑像素 {mountPixels}px bbox=({mMinX},{mMinY})-({mMaxX},{mMaxY}) 中心=({mCx},{mCy}) (与角色相同像素 {samePixels})");
        if (mountPixels == 0)
        {
            Console.WriteLine($"  [像素] ✗ 坐骑完全不可见！");
            return;
        }
        bool mountBelow = mMaxY >= cMaxY;              // 坐骑底 ≥ 角色底（坐骑向下延伸）
        bool charAbove = cCy < mCy;                     // 角色中心在坐骑中心上方
        bool hCenterOk = Math.Abs(cCx - mCx) < Math.Max(40, (cMaxX - cMinX) / 2); // 水平大致居中
        int verticalOverlap = Math.Max(0, Math.Min(cMaxY, mMaxY) - Math.Max(cMinY, mMinY)); // 垂直重叠
        Console.WriteLine($"  [像素] 判定: 坐骑底{mMaxY} vs 角色底{cMaxY} → 坐骑向下延伸: {(mountBelow ? "✓" : "✗")}；" +
            $"角色中心({cCy}) 在坐骑中心({mCy}) 上方: {(charAbove ? "✓" : "✗")}；水平对齐: {(hCenterOk ? "✓" : "✗")}；垂直重叠 {verticalOverlap}px");
        bool pass = mountBelow && charAbove && hCenterOk;
        Console.WriteLine($"  [像素] 骑乘合理性: {(pass ? "PASS ✓ 坐骑在角色下方，角色骑在坐骑上" : "FAIL ✗ 相对位置异常（见上方指标）")}");

        // 存 diff 图（坐骑像素红染）
        using var diff = new SKBitmap(W, H, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(diff))
        {
            canvas.Clear(SKColors.Transparent);
        }
        using var diffPix = diff.PeekPixels();
        unsafe
        {
            byte* pa = (byte*)pixA.GetPixels();
            byte* pd = (byte*)diffPix.GetPixels();
            for (int dy = 0; dy < H; dy++)
            {
                for (int dx = 0; dx < W; dx++)
                {
                    int wx = wMinX + dx, wy = wMinY + dy;
                    int ax = wx + oxA, ay = wy + oyA;
                    int ai = (ax >= 0 && ax < aw && ay >= 0 && ay < ah) ? (ay * aw + ax) * 4 : -1;
                    if (ai < 0 || pa[ai + 3] <= 30) continue;
                    int di = (dy * W + dx) * 4;
                    bool inChar = ax >= -oxB && ax < -oxB + bw2 && ay >= -oyB && ay < -oyB + bh2;
                    // 大致区分：坐骑像素染红，与角色重叠区染橙
                    pd[di + 2] = 255; pd[di] = inChar ? (byte)200 : (byte)0; pd[di + 1] = inChar ? (byte)120 : (byte)0; pd[di + 3] = pa[ai + 3];
                }
            }
        }
        using var img = SKImage.FromBitmap(diff);
        File.WriteAllBytes($"{outDir}/{id}_diff.png", img.Encode(SKEncodedImageFormat.Png, 100).ToArray());

        // ASCII 可视化（无图像模型时人工复核）：C=仅角色像素，M=仅坐骑像素，X=两者重叠，.=透明
        PrintAscii(pixA, pixB, wMinX, wMinY, W, H, aw, ah, bw2, bh2, oxA, oyA, oxB, oyB, id);
    }

    /// <summary>ASCII 栅格：按 2x2 块降采样输出角色(C)/坐骑(M)/重叠(X)。</summary>
    private static void PrintAscii(SKPixmap pixA, SKPixmap pixB, int wMinX, int wMinY, int W, int H,
        int aw, int ah, int bw2, int bh2, int oxA, int oyA, int oxB, int oyB, string id)
    {
        const int Block = 2;
        int cols = (W + Block - 1) / Block, rows = (H + Block - 1) / Block;
        if (cols > 140 || rows > 80) return; // 过大跳过
        var grid = new char[rows, cols];
        for (int r = 0; r < rows; r++) { for (int c = 0; c < cols; c++) { grid[r, c] = '.'; } }
        unsafe
        {
            byte* pa = (byte*)pixA.GetPixels();
            byte* pb = (byte*)pixB.GetPixels();
            for (int dy = 0; dy < H; dy++)
            {
                for (int dx = 0; dx < W; dx++)
                {
                    int wx = wMinX + dx, wy = wMinY + dy;
                    int ax = wx + oxA, ay = wy + oyA;
                    int bx = wx + oxB, by = wy + oyB;
                    bool aIn = ax >= 0 && ax < aw && ay >= 0 && ay < ah;
                    bool bIn = bx >= 0 && bx < bw2 && by >= 0 && by < bh2;
                    bool aOp = aIn && pa[(ay * aw + ax) * 4 + 3] > 30;
                    bool bOp = bIn && pb[(by * bw2 + bx) * 4 + 3] > 30;
                    int r = dy / Block, c = dx / Block;
                    if (aOp && bOp) { grid[r, c] = 'X'; }
                    else if (bOp) { grid[r, c] = 'C'; }
                    else if (aOp && grid[r, c] != 'C') { grid[r, c] = 'M'; }
                }
            }
        }
        Console.WriteLine($"  [ASCII {id}] 角色=C 坐骑=M 重叠=X (每块 {Block}x{Block}px):");
        for (int r = 0; r < rows; r++)
        {
            var line = new char[cols];
            for (int c = 0; c < cols; c++) line[c] = grid[r, c];
            Console.WriteLine("    " + new string(line));
        }
    }

    // ═══════════════════════════════════
    // 工具（复制自 PaperdollService/Probe 的只读逻辑）
    // ═══════════════════════════════════

    private sealed class PieceMeta
    {
        public int Ox, Oy;
        public Dictionary<string, (int x, int y)> Map = new();
        public string ZField = "";
    }

    private static PieceMeta GetPieceMeta(WzService wz, string piecePath)
    {
        return wz.WithWzLock(() => new PieceMeta
        {
            Ox = wz.GetOrigin(piecePath).x,
            Oy = wz.GetOrigin(piecePath).y,
            Map = wz.GetPieceMap(piecePath),
            ZField = wz.GetZField(piecePath) ?? ""
        });
    }

    private static string FormatMap(Dictionary<string, (int x, int y)> map)
    {
        if (map.Count == 0) return "∅";
        return string.Join(" ", map.Select(kv => $"{kv.Key}({kv.Value.x},{kv.Value.y})"));
    }

    private static int GetActionFrameCount(WzService wz, string actionPath)
    {
        try
        {
            return wz.WithWzLock(() =>
            {
                var node = wz.FindNodeByPath(actionPath);
                if (node == null) return 0;
                int count = 0;
                foreach (Wz_Node child in node.Nodes)
                {
                    if (int.TryParse(child.Text, out _)) count++;
                }
                return count;
            });
        }
        catch { return 0; }
    }

    /// <summary>复制 PaperdollService.ResolveMountAction：characterAction 反向（含隐式同名）→ 同名 → stand1/stand2 → 枚举。</summary>
    private static string ResolveMountAction(WzService wz, string mountRoot, string requested)
    {
        var reverse = BuildCharActionReverse(wz, mountRoot);
        if (!string.IsNullOrEmpty(requested))
        {
            if (reverse.TryGetValue(requested, out var mapped) && GetActionFrameCount(wz, $"{mountRoot}/{mapped}") > 0)
            {
                return mapped;
            }
        }
        if (!string.IsNullOrEmpty(requested) && GetActionFrameCount(wz, $"{mountRoot}/{requested}") > 0) return requested;
        if (GetActionFrameCount(wz, $"{mountRoot}/stand1") > 0) return "stand1";
        if (GetActionFrameCount(wz, $"{mountRoot}/stand2") > 0) return "stand2";
        foreach (var act in new[] { "walk1", "walk2", "jump", "fly", "tired", "prone" })
        {
            if (GetActionFrameCount(wz, $"{mountRoot}/{act}") > 0) return act;
        }
        return "";
    }

    private static Dictionary<string, string> BuildCharActionReverse(WzService wz, string mountRoot)
    {
        var r = new Dictionary<string, string>(StringComparer.Ordinal);
        var inner = wz.WithWzLock(() =>
        {
            var imgNode = wz.FindNodeByPath(mountRoot);
            if (imgNode == null) return (Wz_Node?)null;
            var img = imgNode.GetValue<Wz_Image>();
            if (img != null && !img.TryExtract()) return null;
            return img != null ? img.Node : imgNode;
        });
        if (inner == null) return r;
        foreach (Wz_Node top in inner.Nodes)
        {
            if (top.Text == "info" || top.Text == "characterAction" || top.Text == "forcingItem") continue;
            if (!r.ContainsKey(top.Text)) r[top.Text] = top.Text;
        }
        var ca = inner.FindNodeByPath("characterAction");
        if (ca != null)
        {
            foreach (Wz_Node kv in ca.Nodes)
            {
                var x = kv.GetValueEx<string>(null);
                if (string.IsNullOrEmpty(x) || x.Equals("hideBody", StringComparison.OrdinalIgnoreCase)) continue;
                if (!r.TryGetValue(x, out var existing)) r[x] = kv.Text;
                else if (kv.Text == x && existing != x) r[x] = x;
            }
        }
        return r;
    }

    /// <summary>读 zmap（顶层 zmap.img / zmap_cn.img / Base/zmap.img 优先顺序），返回层名列表 index 0=最顶层。</summary>
    private static List<string> GetZmapNames(WzService wz)
    {
        var result = new List<string>();
        foreach (var top in new[] { "zmap.img", "zmap_cn.img", "Base/zmap.img" })
        {
            var imgNode = wz.WithWzLock(() => wz.FindNodeByPath(top));
            if (imgNode == null) continue;
            var img = imgNode.GetValue<Wz_Image>();
            if (img == null || !img.TryExtract()) continue;
            foreach (Wz_Node child in img.Node.Nodes)
            {
                if (!string.IsNullOrEmpty(child.Text)) result.Add(child.Text);
            }
            if (result.Count > 0) break;
        }
        return result;
    }

    private static ItemInfo? FindFirstItem(WzService wz, string[] ids, string folder)
    {
        foreach (var id in ids)
        {
            string path = $"Character/{folder}/{id.PadLeft(8, '0')}.img";
            if (wz.WithWzLock(() => wz.FindNodeByPath(path) != null))
            {
                return new ItemInfo { Id = id };
            }
        }
        return null;
    }
}
