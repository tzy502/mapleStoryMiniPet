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
/// 披风渲染实图验证探针（StartupObject 模式，参考 CapeProbe 骨架）：
/// ① 用 PaperdollService.CompositeFrame 合成多个带披风外观（1102149/1102000/1102081/1102270/1103488/1103734，
///    配基础发型/脸型/身体），PNG 存 /tmp/cape-render/{id}_{action}_{frame}.png；
/// ② 打印每个外观收集到的部件数 / 披风部件落层摘要（ProbeLayerResolution）；
/// ③ 对比 body 与 cape 各动作帧数（披风在某动作帧缺失 → 动画中披风消失）。
/// 用法：dotnet run --project MiniPet.CrashSelfTest -p:UsedAvaloniaProducts= -p:StartupObject=MiniPet.CrashSelfTest.CapeRenderProbe
/// </summary>
public static class CapeRenderProbe
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

    private static void Run()
    {
        var config = new ConfigService();
        var wzConfig = config.LoadWzConfig();
        var wz = new WzService();
        string basePath = Environment.GetEnvironmentVariable("MINIPET_WZ_BASE");
        if (string.IsNullOrEmpty(basePath)) basePath = wzConfig.BaseWzPath;
        Console.WriteLine($"WZ 路径: {basePath}");
        var (ok, _) = wz.LoadWz(wzConfig.WzLibPath, basePath);
        Console.WriteLine($"WZ 加载: {ok}");
        if (!ok) return;

        var cache = new CacheManager();
        var paperdoll = new PaperdollService(wz, cache, new SpriteService(cache));
        string outDir = "/tmp/cape-render";
        Directory.CreateDirectory(outDir);

        // 基础发型/脸型：探测本 WZ 实际存在的 id（tmp 副本可能缺 Hair/Face，缺则跳过）
        var hair = FindFirstItem(wz, new[] { "30000", "31000", "33000", "38000" }, "Hair");
        var face = FindFirstItem(wz, new[] { "20000", "21000", "23000", "28000" }, "Face");
        Console.WriteLine($"基础发型: {(hair != null ? hair.Id : "无(本 WZ 缺 Hair)")}  基础脸型: {(face != null ? face.Id : "无(本 WZ 缺 Face)")}");

        var capeIds = new[] { "1102149", "1102000", "1102081", "1102270", "1102682", "1103058", "1103488", "1103734" };
        string[] actions = { "stand1", "walk1", "jump", "alert" };

        // 精灵尺寸基准：body/head/hair/face/cape 首帧位图实际像素尺寸（判断渲染画布大小的成因）
        Console.WriteLine("\n== 精灵尺寸基准（stand1/0 各部件位图）==");
        DumpBmpDims(wz, "Character/00002000.img/stand1/0/body", "body");
        DumpBmpDims(wz, "Character/00012000.img/stand1/0/head", "head");
        if (hair != null) DumpBmpDims(wz, $"Character/Hair/{hair.Id.PadLeft(8, '0')}.img/stand1/0/hair", "hair");
        if (face != null) DumpBmpDims(wz, $"Character/Face/{face.Id.PadLeft(8, '0')}.img/stand1/0/face", "face");
        DumpBmpDims(wz, "Character/Cape/01102149.img/stand1/0/cape", "cape1102149");
        // body 锚点 + 披风锚点（判断披风相对身体的位置）
        DumpAnchor(wz, "Character/00002000.img/stand1/0/body", "body");
        DumpAnchor(wz, "Character/Cape/01102149.img/stand1/0/cape", "cape1102149");
        // 披风精灵本体另存（观察其形状/颜色）
        SaveRawPng(wz, "Character/Cape/01102149.img/stand1/0/cape", "/tmp/cape-render/sprite_cape_1102149.png", "cape1102149");
        SaveRawPng(wz, "Character/Cape/01102000.img/stand1/0/cape", "/tmp/cape-render/sprite_cape_1102000.png", "cape1102000");
        SaveRawPng(wz, "Character/00002000.img/stand1/0/body", "/tmp/cape-render/sprite_body.png", "body");

        // 全动作帧扫描：body 各动作帧数 vs 披风各动作帧数 + 披风部件逐帧存在性
        // （披风在某动作整体缺失 = 动画中披风消失；某帧缺 cape 部件 = 该帧披风闪没）
        Console.WriteLine("\n== 全动作披风帧扫描 ==");
        string[] allActions = { "stand1", "stand2", "walk1", "walk2", "jump", "alert", "fly", "heal", "sit", "prone",
            "swingO1", "swingO2", "swingO3", "swingOF", "swingP1", "swingPF", "swingT1", "swingTF",
            "stabO1", "stabO2", "stabOF", "shoot1", "shoot2", "shootF", "rope", "ladder" };
        foreach (var id in capeIds)
        {
            string root = $"Character/Cape/{id.PadLeft(8, '0')}.img";
            if (wz.WithWzLock(() => wz.FindNodeByPath(root) == null)) continue;
            var missing = new List<string>();
            var zeroFrame = new List<string>();
            foreach (var act in allActions)
            {
                int bodyFrames = CountFrames(wz, $"Character/00002000.img/{act}");
                int capeFrames = CountFrames(wz, $"{root}/{act}");
                if (bodyFrames > 0 && capeFrames == 0)
                {
                    missing.Add($"{act}(body={bodyFrames},cape=无)");
                }
                else if (bodyFrames > 0 && capeFrames > 0)
                {
                    // 逐帧检查 cape 部件存在性
                    for (int f = 0; f < capeFrames; f++)
                    {
                        bool hasCape = false;
                        try
                        {
                            hasCape = wz.WithWzLock(() =>
                            {
                                var frameNode = wz.FindNodeByPath($"{root}/{act}/{f}");
                                if (frameNode == null) return false;
                                foreach (Wz_Node piece in frameNode.Nodes)
                                {
                                    // cape 部件可能是 canvas(Wz_Png) 或链接(Wz_Uol)——两者都算有效部件
                                    if (piece.Value is Wz_Png or Wz_Uol) { hasCape = true; break; }
                                }
                                return hasCape;
                            });
                        }
                        catch { }
                        if (!hasCape)
                        {
                            zeroFrame.Add($"{act}/{f}");
                            break;
                        }
                    }
                }
            }
            if (missing.Count > 0 || zeroFrame.Count > 0)
            {
                Console.WriteLine($"  [{id}] 披风缺失动作: {string.Join(", ", missing.Take(12))}{(missing.Count > 12 ? "…" : "")}" +
                    (zeroFrame.Count > 0 ? $" | 空帧: {string.Join(", ", zeroFrame.Take(10))}" : ""));
            }
            else
            {
                Console.WriteLine($"  [{id}] 全部动作披风帧齐全 ✓");
            }
        }

        // 全帧 canvas 解析检查：每个披风部件（png/uol）必须能 ExtractPng 出像素——
        // UOL 指向帧目录（如 1103058 heal/0 → ../../alert/1）在 R14 修复前会导致该帧披风消失
        Console.WriteLine("\n== 全帧披风 canvas 解析检查 ==");
        foreach (var id in capeIds)
        {
            string root = $"Character/Cape/{id.PadLeft(8, '0')}.img";
            if (wz.WithWzLock(() => wz.FindNodeByPath(root) == null)) continue;
            var badFrames = new List<string>();
            foreach (var act in allActions)
            {
                int capeFrames = CountFrames(wz, $"{root}/{act}");
                for (int f = 0; f < capeFrames; f++)
                {
                    try
                    {
                        bool anyPng = wz.WithWzLock(() =>
                        {
                            var frameNode = wz.FindNodeByPath($"{root}/{act}/{f}");
                            if (frameNode == null) return false;
                            foreach (Wz_Node piece in frameNode.Nodes)
                            {
                                if (piece.Value is not (Wz_Png or Wz_Uol)) continue;
                                var png = wz.ExtractPng($"{root}/{act}/{f}/{piece.Text}");
                                if (png != null && png.Length > 0) return true;
                            }
                            return false;
                        });
                        if (!anyPng) badFrames.Add($"{act}/{f}");
                    }
                    catch { badFrames.Add($"{act}/{f}"); }
                }
            }
            Console.WriteLine($"  [{id}] {(badFrames.Count == 0 ? "全部帧 canvas 可解析 ✓" : "✗ 坏帧: " + string.Join(", ", badFrames.Take(15)))}");
        }

        foreach (var id in capeIds)
        {
            string root = $"Character/Cape/{id.PadLeft(8, '0')}.img";
            var node = wz.WithWzLock(() => wz.FindNodeByPath(root));
            if (node == null)
            {
                Console.WriteLine($"== [{id}] {root} 不存在 ==");
                continue;
            }
            var appearance = new CharacterAppearance
            {
                Gender = 0,
                BodyId = 2000,
                Ear = PaperdollService.EarHuman,
                Hair = hair ?? new ItemInfo(),
                Face = face ?? new ItemInfo(),
                Cape = new ItemInfo { Id = id },
                EnableEffect = true,
            };
            Console.WriteLine($"\n== [{id}] {root} ==");
            // 帧数对比：body 每动作帧数 vs 披风每动作帧数（披风帧数少 → 动画后段披风消失）
            foreach (var act in actions)
            {
                int bodyFrames = CountFrames(wz, $"Character/00002000.img/{act}");
                int capeFrames = CountFrames(wz, $"{root}/{act}");
                Console.WriteLine($"  帧数对比 {act}: body={bodyFrames} cape={capeFrames} {(capeFrames < bodyFrames ? $"⚠ 披风后 {bodyFrames - capeFrames} 帧缺失" : "")}");
            }
            // 首帧部件落层摘要
            var items = paperdoll.ProbeLayerResolution(appearance, "stand1", 0);
            var capeItems = items.Where(x => x.Category == "cape").ToList();
            Console.WriteLine($"  stand1/0 部件 {items.Count} 件，披风 {capeItems.Count} 件:");
            foreach (var c in capeItems)
            {
                Console.WriteLine($"    ▶ {c.PieceName} z={c.ZField} → 层 {c.ResolvedLayer}(idx {c.ZIndex}) map={c.HasMap} locked={c.Locked} hidden={c.Hidden}");
            }
            // 逐动作合成首帧 PNG
            foreach (var act in actions)
            {
                int frame = 0;
                var png = paperdoll.CompositeFrame(appearance, act, frame);
                string fn = $"{outDir}/{id}_{act}_{frame}.png";
                if (png != null && png.Length > 0)
                {
                    File.WriteAllBytes(fn, png);
                    Console.WriteLine($"  合成 {act}/{frame} → {fn} ({png.Length} 字节)");
                }
                else
                {
                    Console.WriteLine($"  合成 {act}/{frame} 失败（无输出）");
                }
            }
            // 像素级验证：无披风对照合成 → 逐像素 diff 得「披风可见像素」bbox 与身体轮廓重叠率
            // （肉眼判断的客观化：diff 非空 = 披风真的画出来了；与身体轮廓重叠 ≈ 0 = 披风在身后被身体遮挡）
            var noCape = new CharacterAppearance
            {
                Gender = 0, BodyId = 2000, Ear = PaperdollService.EarHuman,
                Hair = hair ?? new ItemInfo(), Face = face ?? new ItemInfo(),
                Cape = null, EnableEffect = true,
            };
            var pngNoCape = paperdoll.CompositeFrame(noCape, "stand1", 0);
            if (pngNoCape != null && pngNoCape.Length > 0)
            {
                File.WriteAllBytes($"{outDir}/{id}_nocape_stand1_0.png", pngNoCape);
            }
            // 仅身体（无头/发/脸/披风）：定位身体实际落点
            var bodyOnly = new CharacterAppearance
            {
                Gender = 0, BodyId = 2000, Ear = PaperdollService.EarHuman,
                Hair = new ItemInfo(), Face = new ItemInfo(),
                Cape = null, EnableEffect = true,
            };
            var pngBody = paperdoll.CompositeFrame(bodyOnly, "stand1", 0);
            if (pngBody != null && pngBody.Length > 0)
            {
                File.WriteAllBytes($"{outDir}/bodyonly_stand1_0.png", pngBody);
            }
            // 精确几何：设置当前外观后取 stand1 union bounds（originX=-Left, originY=-Top），
            // 再按管线公式反推 body/cape 各部件画布落点（piece.Final - bodyAnchor - bounds.Left/Top + move）
            paperdoll.SetAppearance(appearance);
            var (bw, bh, boX, boY) = paperdoll.GetActionBounds("stand1");
            Console.WriteLine($"  [几何] stand1 union bounds: {bw}x{bh} origin=({boX},{boY}) → Left={-boX} Top={-boY}");
            paperdoll.SetAppearance(noCape);
            var (nbw, nbh, nboX, nboY) = paperdoll.GetActionBounds("stand1");
            Console.WriteLine($"  [几何] 无披风 union bounds: {nbw}x{nbh} origin=({nboX},{nboY})");
            PixelDiff(wz, paperdoll, appearance, noCape, boX, boY, nboX, nboY, id, outDir);
            // UOL 链接帧验证：stand2/0、prone/0、heal/0、shoot1/0 的 cape 是 Wz_Uol 链接——确认管线能渲染
            foreach (var uolAct in new[] { "stand2", "prone", "heal", "shoot1" })
            {
                var pngUol = paperdoll.CompositeFrame(appearance, uolAct, 0);
                if (pngUol != null && pngUol.Length > 0)
                {
                    File.WriteAllBytes($"{outDir}/{id}_{uolAct}_0.png", pngUol);
                    var pngNoUol = paperdoll.CompositeFrame(noCape, uolAct, 0);
                    if (pngNoUol != null && pngNoUol.Length > 0)
                    {
                        paperdoll.SetAppearance(appearance);
                        var (ua, ub, uox, uoy) = paperdoll.GetActionBounds(uolAct);
                        paperdoll.SetAppearance(noCape);
                        var (nb2, nh2, nox2, noy2) = paperdoll.GetActionBounds(uolAct);
                        var pngUolDiff = DiffToPng(paperdoll, appearance, noCape, uox, uoy, nox2, noy2, uolAct);
                        if (pngUolDiff != null)
                        {
                            File.WriteAllBytes($"{outDir}/{id}_{uolAct}_0_diff.png", pngUolDiff);
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"  [UOL帧] {uolAct}/0 合成失败");
                }
            }

            // 特效披风：EnableEffect 开关对照合成
            if (capeItems.Any(x => x.PieceName.StartsWith("effect", StringComparison.OrdinalIgnoreCase)))
            {
                var off = new CharacterAppearance
                {
                    Gender = 0, BodyId = 2000, Ear = PaperdollService.EarHuman,
                    Hair = hair ?? new ItemInfo(), Face = face ?? new ItemInfo(),
                    Cape = new ItemInfo { Id = id }, EnableEffect = false,
                };
                var pngOff = paperdoll.CompositeFrame(off, "stand1", 0);
                if (pngOff != null && pngOff.Length > 0)
                {
                    File.WriteAllBytes($"{outDir}/{id}_stand1_0_noeffect.png", pngOff);
                    Console.WriteLine($"  特效关合成 → {outDir}/{id}_stand1_0_noeffect.png ({pngOff.Length} 字节)");
                }
            }
        }
    }

    /// <summary>
    /// 像素级披风可见性验证：合成「带披风」与「无披风」同一外观（stand1/0），逐像素比较。
    /// 输出：diff 像素数 / 披风可见 bbox / 与身体轮廓（无披风图不透明像素）重叠数。
    /// 判断：diff 数 > 0 → 披风可见；重叠数 ≈ 0 → 披风在身体轮廓之外（身后披风被身体遮挡，符合预期）。
    /// 同时把 diff 图存为 {id}_cape_diff.png（披风像素红染，便于人工/视觉模型复核）。
    /// </summary>
    private static void PixelDiff(WzService wz, PaperdollService paperdoll, CharacterAppearance withCape, CharacterAppearance noCape, int oxA, int oyA, int oxB, int oyB, string id, string outDir)
    {
        var (png, diffCount, W, H, minX, minY, maxX, maxY, overlapBody) = ComputeDiff(paperdoll, withCape, noCape, oxA, oyA, oxB, oyB, "stand1", 0);
        if (png == null)
        {
            Console.WriteLine($"  [像素验证] 合成失败，跳过");
            return;
        }
        Console.WriteLine($"  [像素验证] 披风可见像素 {diffCount} 个, bbox=({minX},{minY})-({maxX},{maxY}) (diff 图 {W}x{H}), 与身体轮廓重叠 {overlapBody} 个");
        Console.WriteLine($"  [像素验证] 结论: {(diffCount == 0 ? "✗ 披风完全不可见！" : overlapBody > diffCount / 3 ? "⚠ 披风大面积盖在身体上（疑似层级过高）" : diffCount > 10 ? "✓ 披风可见且主要在身体轮廓之外（身后披风符合预期）" : "⚠ 披风可见像素过少，需人工复核")}");
        File.WriteAllBytes($"{outDir}/{id}_cape_diff.png", png);
    }

    /// <summary>任意动作/帧的「带披风 vs 无披风」diff PNG（世界坐标对齐）。返回 (png字节, diff数, W, H, bbox, 重叠数)。</summary>
    private static (byte[]? Png, int DiffCount, int W, int H, int MinX, int MinY, int MaxX, int MaxY, int Overlap) ComputeDiff(
        PaperdollService paperdoll, CharacterAppearance withCape, CharacterAppearance noCape,
        int oxA, int oyA, int oxB, int oyB, string action, int frame)
    {
        try
        {
            var pngA = paperdoll.CompositeFrame(withCape, action, frame);
            var pngB = paperdoll.CompositeFrame(noCape, action, frame);
            if (pngA == null || pngB == null)
            {
                return (null, 0, 0, 0, 0, 0, -1, -1, 0);
            }
            using var bmpA = SKBitmap.Decode(pngA);
            using var bmpB = SKBitmap.Decode(pngB);
            int aw = bmpA.Width, ah = bmpA.Height, bw2 = bmpB.Width, bh2 = bmpB.Height;
            // 世界坐标对齐：canvas(x,y) → world(x - ox, y - oy)。两图 origin 不同（披风撑大 bounds 时）按世界坐标逐点比较。
            int wMinX = Math.Min(-oxA, -oxB);
            int wMinY = Math.Min(-oyA, -oyB);
            int wMaxX = Math.Max(-oxA + aw, -oxB + bw2);
            int wMaxY = Math.Max(-oyA + ah, -oyB + bh2);
            int W = wMaxX - wMinX, H = wMaxY - wMinY;
            if (W <= 0 || H <= 0)
            {
                return (null, 0, 0, 0, 0, 0, -1, -1, 0);
            }
            using var diff = new SKBitmap(W, H, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(diff);
            canvas.Clear(SKColors.Transparent);
            using var diffPix = diff.PeekPixels();
            using var pixA = bmpA.PeekPixels();
            using var pixB = bmpB.PeekPixels();
            int diffCount = 0;
            int minX = W, minY = H, maxX = -1, maxY = -1;
            int overlapBody = 0;
            unsafe
            {
                byte* pa = (byte*)pixA.GetPixels();
                byte* pb = (byte*)pixB.GetPixels();
                byte* pd = (byte*)diffPix.GetPixels();
                for (int dy = 0; dy < H; dy++)
                {
                    for (int dx = 0; dx < W; dx++)
                    {
                        int wx = wMinX + dx;
                        int wy = wMinY + dy;
                        int ax = wx + oxA, ay = wy + oyA;
                        int bx = wx + oxB, by = wy + oyB;
                        bool aIn = ax >= 0 && ax < aw && ay >= 0 && ay < ah;
                        bool bIn = bx >= 0 && bx < bw2 && by >= 0 && by < bh2;
                        int ai = aIn ? (ay * aw + ax) * 4 : -1;
                        int bi = bIn ? (by * bw2 + bx) * 4 : -1;
                        bool aOpaque = ai >= 0 && pa[ai + 3] > 30;
                        bool bOpaque = bi >= 0 && pb[bi + 3] > 30;
                        bool same = aIn && bIn && pa[ai] == pb[bi] && pa[ai + 1] == pb[bi + 1] && pa[ai + 2] == pb[bi + 2] && pa[ai + 3] == pb[bi + 3];
                        if (same) continue;
                        // 只有 A 有像素（B 无或不同）→ 披风可见像素；与身体轮廓（B 不透明像素）重叠计入
                        if (!aOpaque) continue;
                        diffCount++;
                        if (dx < minX) minX = dx;
                        if (dy < minY) minY = dy;
                        if (dx > maxX) maxX = dx;
                        if (dy > maxY) maxY = dy;
                        if (bOpaque) overlapBody++;
                        // diff 图：披风像素染红（保留 alpha）
                        int di = (dy * W + dx) * 4;
                        pd[di] = 0; pd[di + 1] = 0; pd[di + 2] = 255; pd[di + 3] = pa[ai + 3];
                    }
                }
            }
            using var img = SKImage.FromBitmap(diff);
            var png = img.Encode(SKEncodedImageFormat.Png, 100).ToArray();
            return (png, diffCount, W, H, minX, minY, maxX, maxY, overlapBody);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [像素验证] 异常: {ex.Message}");
            return (null, 0, 0, 0, 0, 0, -1, -1, 0);
        }
    }

    /// <summary>UOL 帧验证：对指定动作/帧合成 diff，若披风可见像素 > 0 打印 ✓。</summary>
    private static byte[]? DiffToPng(PaperdollService paperdoll, CharacterAppearance withCape, CharacterAppearance noCape, int oxA, int oyA, int oxB, int oyB, string action)
    {
        var (png, diffCount, W, H, _, _, _, _, _) = ComputeDiff(paperdoll, withCape, noCape, oxA, oyA, oxB, oyB, action, 0);
        Console.WriteLine($"  [UOL帧] {action}/0 披风可见像素 {diffCount} 个 → {(diffCount > 0 ? "✓ UOL 链接帧渲染正常" : "✗ UOL 帧披风不可见！")}");
        return png;
    }

    private static void DumpAnchor(WzService wz, string path, string label)
    {
        var map = wz.WithWzLock(() => wz.GetPieceMap(path));
        if (map == null || map.Count == 0)
        {
            Console.WriteLine($"  {label} {path} 锚点: 无 map");
            return;
        }
        var parts = map.Select(kv => $"{kv.Key}=({kv.Value.x},{kv.Value.y})");
        Console.WriteLine($"  {label} {path} 锚点: {string.Join(" ", parts)}");
    }

    private static void SaveRawPng(WzService wz, string path, string outPath, string label)
    {
        var png = wz.WithWzLock(() => wz.ExtractPng(path));
        if (png != null && png.Length > 0)
        {
            File.WriteAllBytes(outPath, png);
            Console.WriteLine($"  {label} 精灵已存: {outPath}");
        }
    }

    private static void DumpBmpDims(WzService wz, string path, string label)
    {
        var png = wz.WithWzLock(() => wz.ExtractPng(path));
        if (png == null || png.Length < 24)
        {
            Console.WriteLine($"  {label} {path} → 无位图");
            return;
        }
        int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        int h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        var origin = wz.WithWzLock(() => wz.GetOrigin(path));
        Console.WriteLine($"  {label} {path} → {w}x{h} origin=({origin.x},{origin.y})");
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

    private static int CountFrames(WzService wz, string actionPath)
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
}
