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
/// 骑乘动作映射 probe（R20 修复验证）：
/// ① 实测 01930001（飞行坐骑）+ 另 2 个 characterAction 坐骑（01992031 / 01992033）的
///    characterAction 节点内容（坐骑动作 → 角色动作，如 walk1→fly）；
/// ② 角色 body（00002000）是否含映射目标动作（fly/sit/StabT2）及帧数——映射目标不存在时
///    角色切过去会空帧（数据以实测为准，不编造）；
/// ③ 骑乘链确认：角色 stand1 → ResolveMountAction → 坐骑动作 → characterAction[坐骑动作] → 角色动作
///    （如 stand1 → 坐骑 stand1 → fly），即角色动作本身应切换为映射的角色动作（fly）；
/// ④ 像素对比：外观带坐骑时，合成 stand1 帧 vs 合成 fly 帧——坐骑 bbox 相对角色 bbox 的位置
///    （浮头顶 = 坐骑在角色上方；贴合 = 坐骑在角色下方、角色骑在坐骑上）。
/// 用法：dotnet run --project MiniPet.CrashSelfTest -p:UsedAvaloniaProducts= -p:StartupObject=MiniPet.CrashSelfTest.MountRideProbe
/// </summary>
public static class MountRideProbe
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

    // 目标坐骑：01930001 飞行映射 / 01992031（walk1→StabT2 等）/ 01992033（stand1→sit 等）/ 01932294 hideBody 参照
    private static readonly string[] MountIds = { "01930001", "01992031", "01992033", "01932294" };

    private static void Run()
    {
        var basePath = Environment.GetEnvironmentVariable("MINIPET_WZ_BASE");
        var config = new ConfigService();
        var wzConfig = config.LoadWzConfig();
        var wz = new WzService();
        var (ok, _) = wz.LoadWz(wzConfig.WzLibPath, basePath ?? wzConfig.BaseWzPath);
        Console.WriteLine($"WZ 加载: {ok} base={basePath ?? wzConfig.BaseWzPath}");
        if (!ok) return;

        // ① characterAction 内容 + ② 角色 body 目标动作存在性
        Console.WriteLine("=== 坐骑 characterAction 实测 ===");
        foreach (var id in MountIds)
        {
            ScanCharacterAction(wz, id);
        }

        Console.WriteLine("\n=== 角色 body(00002000) 动作帧数（映射目标是否可播）===");
        foreach (var act in new[] { "stand1", "walk1", "fly", "jump", "sit", "StabT2", "prone" })
        {
            int fc = GetActionFrameCount(wz, $"Character/00002000.img/{act}");
            Console.WriteLine($"  {act,-7}: {fc} 帧");
        }

        // ③ 骑乘链确认（复制 PaperdollService 逻辑）
        Console.WriteLine("\n=== 骑乘链：角色动作 → 坐骑动作 → characterAction → 角色动作 ===");
        foreach (var id in MountIds)
        {
            string root = $"Character/TamingMob/{id}.img";
            if (!wz.WithWzLock(() => wz.FindNodeByPath(root) != null))
            {
                Console.WriteLine($"  [{id}] 不存在，跳过");
                continue;
            }
            var map = GetCharActionMap(wz, root);
            foreach (var req in new[] { "stand1", "walk1", "fly", "jump" })
            {
                string mountAct = ResolveMountAction(wz, root, req);
                string charAct = map.TryGetValue(mountAct, out var v) ? v : "";
                string mapped = charAct.Length > 0 && !charAct.Equals("hideBody", StringComparison.OrdinalIgnoreCase) ? charAct : req;
                Console.WriteLine($"  [{id}] 角色 {req,-6} → 坐骑 {mountAct,-6} → characterAction[{mountAct}]={charAct,-8} → 角色应播 {mapped}");
            }
        }

        // ③b 几何数据：body fly vs stand1、坐骑 stand1 vs fly 的 navel/origin（定位贴合依据）
        Console.WriteLine("\n=== 几何数据（角色 body + 坐骑帧 navel/origin）===");
        foreach (var id in MountIds)
        {
            string root = $"Character/TamingMob/{id}.img";
            if (!wz.WithWzLock(() => wz.FindNodeByPath(root) != null)) continue;
            Console.WriteLine($"\n--- {id} ---");
            foreach (var ba in new[] { "stand1", "fly" })
            {
                var bodyMeta = GetPieceMeta(wz, $"Character/00002000.img/{ba}/0/body");
                Console.WriteLine($"  角色 body {ba}/0: origin=({bodyMeta.Ox},{bodyMeta.Oy}) map=[{FormatMap(bodyMeta.Map)}] png={GetPngDims(wz, $"Character/00002000.img/{ba}/0/body")}");
            }
            foreach (var ma in new[] { "stand1", "fly" })
            {
                string mp = $"{root}/{ma}/0";
                if (!wz.WithWzLock(() => wz.FindNodeByPath(mp) != null)) { Console.WriteLine($"  坐骑 {ma}/0 不存在"); continue; }
                var pieceNames = wz.WithWzLock(() => wz.GetFramePieceNames(mp))
                    .Where(n => n != "delay" && n != "move" && n != "a0" && n != "a1").ToList();
                (int x, int y)? mainNavel = null;
                foreach (var name in pieceNames)
                {
                    var meta = GetPieceMeta(wz, $"{mp}/{name}");
                    if (mainNavel == null && meta.Map.TryGetValue("navel", out var nv)) mainNavel = nv;
                }
                Console.WriteLine($"  坐骑 {ma}/0: {pieceNames.Count} 部件 主navel={(mainNavel == null ? "无" : $"({mainNavel.Value.x},{mainNavel.Value.y})")}");
                foreach (var name in pieceNames)
                {
                    var meta = GetPieceMeta(wz, $"{mp}/{name}");
                    var dims = GetPngDims(wz, $"{mp}/{name}");
                    Console.WriteLine($"    {name}: origin=({meta.Ox},{meta.Oy}) map=[{FormatMap(meta.Map)}] png={dims}");
                }
            }
        }

        // ④ 像素对比：stand1 合成 vs fly 合成（坐骑贴合判定）
        Console.WriteLine("\n=== 像素对比：stand1 帧 vs fly 帧（坐骑是否贴合角色）===");
        var cache = new CacheManager();
        var paperdoll = new PaperdollService(wz, cache, new SpriteService(cache));
        string outDir = "/tmp/mount-ride";
        Directory.CreateDirectory(outDir);
        foreach (var id in MountIds)
        {
            string root = $"Character/TamingMob/{id}.img";
            if (!wz.WithWzLock(() => wz.FindNodeByPath(root) != null))
            {
                Console.WriteLine($"\n== [{id}] 不存在，跳过 ==");
                continue;
            }
            AnalyzePixelsForAction(wz, paperdoll, id, root, outDir, "stand1");
            AnalyzePixelsForAction(wz, paperdoll, id, root, outDir, "fly");
        }
        Console.WriteLine($"\nPNG 输出: {outDir}/{{id}}_{{action}}.png / {{id}}_{{action}}_nomount.png");
    }

    private static void ScanCharacterAction(WzService wz, string id)
    {
        Console.WriteLine($"\n--- {id} ---");
        var inner = GetMountInner(wz, id);
        if (inner == null)
        {
            Console.WriteLine($"  {id}.img 不存在或提取失败（跳过）");
            return;
        }
        var ca = inner.FindNodeByPath("characterAction");
        if (ca == null)
        {
            Console.WriteLine("  characterAction: 无");
            return;
        }
        Console.WriteLine("  characterAction（坐骑动作 → 角色动作）:");
        foreach (Wz_Node kv in ca.Nodes)
        {
            Console.WriteLine($"    {kv.Text} → {kv.GetValueEx<string>(null)}");
        }
    }

    private static void AnalyzePixelsForAction(WzService wz, PaperdollService paperdoll, string id, string root,
        string outDir, string action)
    {
        var hair = FindFirstItem(wz, new[] { "30000", "31000", "33000", "38000" }, "Hair");
        var face = FindFirstItem(wz, new[] { "20000", "21000", "23000", "28000" }, "Face");
        var withMount = new CharacterAppearance
        {
            Gender = 0, BodyId = 2000, Ear = PaperdollService.EarHuman,
            Hair = hair ?? new ItemInfo(), Face = face ?? new ItemInfo(),
            Mount = new MountInfo { Id = id }, EnableEffect = true,
        };
        var noMount = new CharacterAppearance
        {
            Gender = 0, BodyId = 2000, Ear = PaperdollService.EarHuman,
            Hair = hair ?? new ItemInfo(), Face = face ?? new ItemInfo(),
            Mount = null, EnableEffect = true,
        };

        // R20：带坐骑合成时角色动作内部会切换为 characterAction 映射动作（如 stand1→fly），
        // 无坐骑参照须用同一解析动作合成（隔离坐骑像素，避免角色动作差异混入坐骑 diff）。
        string resolvedAction = ResolveRideAction(wz, root, action);
        var pngA = paperdoll.CompositeFrame(withMount, action, 0);
        var pngB = paperdoll.CompositeFrame(noMount, resolvedAction, 0);
        if (pngA == null || pngB == null)
        {
            Console.WriteLine($"  [{id}] {action}/0 合成失败（pngA={pngA != null} pngB={pngB != null}）");
            return;
        }
        File.WriteAllBytes($"{outDir}/{id}_{action}.png", pngA);
        File.WriteAllBytes($"{outDir}/{id}_{action}_nomount.png", pngB);

        // 角色 body 该动作是否有帧（无帧 = 角色空帧，坐骑也悬空 → 贴合判定无意义）
        int bodyFrames = GetActionFrameCount(wz, "Character/00002000.img/" + action);
        string mountAct = ResolveMountAction(wz, root, action);
        string charAct = GetCharActionMap(wz, root).TryGetValue(mountAct, out var cv) ? cv : "";

        using var bmpA = SKBitmap.Decode(pngA);
        using var bmpB = SKBitmap.Decode(pngB);
        paperdoll.SetAppearance(withMount);
        var (aw, ah, oxA, oyA) = paperdoll.GetActionBounds(action);
        paperdoll.SetAppearance(noMount);
        var (bw, bh, oxB, oyB) = paperdoll.GetActionBounds(resolvedAction);
        if (aw <= 0 || bw <= 0)
        {
            Console.WriteLine($"  [{id}] {action}/0 bounds 异常（{aw}x{ah} / {bw}x{bh}）");
            return;
        }
        (int cMinX, int cMinY, int cMaxX, int cMaxY, int mMinX, int mMinY, int mMaxX, int mMaxY, int charPixels, int mountPixels) =
            ComputeBoxes(bmpA, bmpB, oxA, oyA, oxB, oyB);

        Console.WriteLine($"\n  [{id}] {action}/0: body 角色帧={bodyFrames} 坐骑动作={mountAct} 角色应播={charAct} 参照动作={resolvedAction}");
        Console.WriteLine($"    角色轮廓 {charPixels}px bbox=({cMinX},{cMinY})-({cMaxX},{cMaxY})");
        Console.WriteLine($"    坐骑像素 {mountPixels}px bbox=({mMinX},{mMinY})-({mMaxX},{mMaxY})");
        if (mountPixels == 0)
        {
            Console.WriteLine($"    坐骑不可见（mountPixels=0）");
            return;
        }
        int cCy = (cMinY + cMaxY) / 2, mCy = (mMinY + mMaxY) / 2;
        bool mountBelow = mMaxY >= cMaxY;                 // 坐骑底 ≥ 角色底（坐骑向下延伸）
        bool charAbove = cCy < mCy;                       // 角色中心在坐骑中心上方
        bool hCenterOk = Math.Abs((cMinX + cMaxX) / 2 - (mMinX + mMaxX) / 2) < Math.Max(40, (cMaxX - cMinX) / 2);
        int overlap = Math.Max(0, Math.Min(cMaxY, mMaxY) - Math.Max(cMinY, mMinY));
        int vGap = mMaxY < cMinY ? cMinY - mMaxY : (cMaxY < mMinY ? mMinY - cMaxY : 0); // 垂直间距（0=贴合/重叠）
        Console.WriteLine($"    判定: 坐骑向下延伸:{(mountBelow ? "✓" : "✗")} 角色中心在坐骑上方:{(charAbove ? "✓" : "✗")} 水平对齐:{(hCenterOk ? "✓" : "✗")} 垂直重叠 {overlap}px 垂直间距 {vGap}px");
        // 贴合判定：普通坐骑（坐骑在角色下方）或飞行坐骑（坐骑贴角色头顶/头部相接）均算贴合；
        // 核心指标 = 角色与坐骑无悬浮间距（vGap==0）且水平对齐。
        bool attached = vGap == 0 && hCenterOk;
        bool pass = (mountBelow && charAbove && hCenterOk) || (attached && overlap >= 0);
        Console.WriteLine($"    骑乘合理性: {(pass ? "PASS ✓ 坐骑贴合角色（无悬浮/重叠/骑乘位）" : "FAIL ✗ 坐骑相对角色位置异常（浮头顶/错位）")}");
        PrintAscii(bmpA, bmpB, oxA, oyA, oxB, oyB, id, action);
    }

    /// <summary>ASCII 栅格：C=仅角色像素，M=仅坐骑像素，X=重叠，.=透明（按 2x2 块降采样）。</summary>
    private static void PrintAscii(SKBitmap bmpA, SKBitmap bmpB, int oxA, int oyA, int oxB, int oyB, string id, string action)
    {
        int aw = bmpA.Width, ah = bmpA.Height, bw2 = bmpB.Width, bh2 = bmpB.Height;
        int wMinX = Math.Min(-oxA, -oxB), wMinY = Math.Min(-oyA, -oyB);
        int wMaxX = Math.Max(-oxA + aw, -oxB + bw2), wMaxY = Math.Max(-oyA + ah, -oyB + bh2);
        int W = wMaxX - wMinX, H = wMaxY - wMinY;
        const int Block = 2;
        int cols = (W + Block - 1) / Block, rows = (H + Block - 1) / Block;
        if (cols > 130 || rows > 90) return;
        var grid = new char[rows, cols];
        for (int r = 0; r < rows; r++) { for (int c = 0; c < cols; c++) { grid[r, c] = '.'; } }
        using var pixA = bmpA.PeekPixels();
        using var pixB = bmpB.PeekPixels();
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
        Console.WriteLine($"    [ASCII {id} {action}] 角色=C 坐骑=M 重叠=X:");
        for (int r = 0; r < rows; r++)
        {
            var line = new char[cols];
            for (int c = 0; c < cols; c++) line[c] = grid[r, c];
            Console.WriteLine("      " + new string(line));
        }
    }

    /// <summary>带坐骑合成 vs 无坐骑合成：角色轮廓 = 无坐骑不透明像素；坐骑像素 = 带坐骑有且与角色不同的像素。世界坐标对齐。</summary>
    private static (int cMinX, int cMinY, int cMaxX, int cMaxY, int mMinX, int mMinY, int mMaxX, int mMaxY, int charPixels, int mountPixels)
        ComputeBoxes(SKBitmap bmpA, SKBitmap bmpB, int oxA, int oyA, int oxB, int oyB)
    {
        int aw = bmpA.Width, ah = bmpA.Height, bw2 = bmpB.Width, bh2 = bmpB.Height;
        int wMinX = Math.Min(-oxA, -oxB), wMinY = Math.Min(-oyA, -oyB);
        int wMaxX = Math.Max(-oxA + aw, -oxB + bw2), wMaxY = Math.Max(-oyA + ah, -oyB + bh2);
        int W = wMaxX - wMinX, H = wMaxY - wMinY;
        int cMinX = W, cMinY = H, cMaxX = -1, cMaxY = -1;
        int mMinX = W, mMinY = H, mMaxX = -1, mMaxY = -1;
        int charPixels = 0, mountPixels = 0;
        if (W <= 0 || H <= 0) return (0, 0, -1, -1, 0, 0, -1, -1, 0, 0);
        using var pixA = bmpA.PeekPixels();
        using var pixB = bmpB.PeekPixels();
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
                    if (same) continue;
                    mountPixels++;
                    if (dx < mMinX) mMinX = dx;
                    if (dy < mMinY) mMinY = dy;
                    if (dx > mMaxX) mMaxX = dx;
                    if (dy > mMaxY) mMaxY = dy;
                }
            }
        }
        if (cMaxX < 0) { cMinX = 0; cMinY = 0; cMaxX = 0; cMaxY = 0; }
        if (mMaxX < 0) { mMinX = 0; mMinY = 0; mMaxX = 0; mMaxY = 0; }
        return (cMinX, cMinY, cMaxX, cMaxY, mMinX, mMinY, mMaxX, mMaxY, charPixels, mountPixels);
    }

    // ═══════════════ 工具（复制自 PaperdollService 只读逻辑）═══════════════

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

    /// <summary>PNG 尺寸（不解码：读头部大端宽高）。</summary>
    private static string GetPngDims(WzService wz, string piecePath)
    {
        var png = wz.ExtractPng(piecePath);
        if (png == null || png.Length < 24) return "?";
        var w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        var h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return $"{w}x{h}";
    }

    private static string FormatMap(Dictionary<string, (int x, int y)> map)
    {
        if (map.Count == 0) return "∅";
        return string.Join(" ", map.Select(kv => $"{kv.Key}({kv.Value.x},{kv.Value.y})"));
    }

    private static Wz_Node? GetMountInner(WzService wz, string id)
    {
        var imgNode = wz.FindNodeByPath($"Character/TamingMob/{id}.img");
        if (imgNode == null) return null;
        var img = imgNode.GetValue<Wz_Image>();
        if (img != null && !img.TryExtract()) return null;
        return img != null ? img.Node : imgNode;
    }

    /// <summary>characterAction 正向映射（坐骑动作名 → 角色动作名）。</summary>
    private static Dictionary<string, string> GetCharActionMap(WzService wz, string mountRoot)
    {
        var m = new Dictionary<string, string>(StringComparer.Ordinal);
        var inner = wz.WithWzLock(() =>
        {
            var imgNode = wz.FindNodeByPath(mountRoot);
            if (imgNode == null) return (Wz_Node?)null;
            var img = imgNode.GetValue<Wz_Image>();
            if (img != null && !img.TryExtract()) return null;
            return img != null ? img.Node : imgNode;
        });
        if (inner == null) return m;
        var ca = inner.FindNodeByPath("characterAction");
        if (ca == null) return m;
        foreach (Wz_Node kv in ca.Nodes)
        {
            var v = kv.GetValueEx<string>(null);
            if (!string.IsNullOrEmpty(v)) m[kv.Text] = v;
        }
        return m;
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

    /// <summary>复制 PaperdollService.ResolveRideAction：坐骑动作 → characterAction 映射的角色动作（如 stand1→fly）。</summary>
    private static string ResolveRideAction(WzService wz, string mountRoot, string requested)
    {
        string mountAction = ResolveMountAction(wz, mountRoot, requested);
        if (mountAction.Length == 0) return requested;
        var map = GetCharActionMap(wz, mountRoot);
        if (map.TryGetValue(mountAction, out var mapped)
            && !string.IsNullOrEmpty(mapped)
            && !mapped.Equals("hideBody", StringComparison.OrdinalIgnoreCase)
            && GetActionFrameCount(wz, $"Character/00002000.img/{mapped}") > 0)
        {
            return mapped;
        }
        return requested;
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
                if (!r.TryGetValue(x, out var existing))
                {
                    r[x] = kv.Text;
                }
                else if (kv.Text == x && existing != x)
                {
                    r[x] = x;
                }
            }
        }
        return r;
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

    private static ItemInfo? FindFirstItem(WzService wz, string[] ids, string category)
    {
        foreach (var id in ids)
        {
            string p = $"Character/{category}/000{id}.img";
            if (wz.WithWzLock(() => wz.FindNodeByPath(p) != null)) return new ItemInfo { Id = id };
        }
        return null;
    }
}
