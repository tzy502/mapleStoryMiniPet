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
/// 椅子渲染实图验证探针（参考 CapeRenderProbe 骨架 + 像素分析）：
/// ① 扫描多组椅子 id 的 effect 帧数 / PNG 实际尺寸 / origin / z / pos（判定椅子本体是否可出图）；
/// ② 用 PaperdollService.CompositeFrame 合成「带椅子」外观（stand1/0 与 sit/0），PNG 存 /tmp/chair-render/{id}_{action}.png；
/// ③ 与「无椅子」外观逐像素 diff：diff bbox / 与身体重叠 / 相对身体的上中下位置（判断椅子是否画出、画在哪）。
/// 用法：dotnet run --project MiniPet.CrashSelfTest -p:UsedAvaloniaProducts= -p:StartupObject=MiniPet.CrashSelfTest.ChairRenderProbe
/// </summary>
public static class ChairRenderProbe
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
        string outDir = "/tmp/chair-render";
        Directory.CreateDirectory(outDir);

        // 基础外观：皮肤 2000 / 发型 30000 / 脸型 20000（探测存在性）
        var hair = FindFirstItem(wz, new[] { "30000", "31000", "33000", "38000" }, "Hair");
        var face = FindFirstItem(wz, new[] { "20000", "21000", "23000", "28000" }, "Face");
        Console.WriteLine($"基础发型: {(hair != null ? hair.Id : "无")}  基础脸型: {(face != null ? face.Id : "无")}");

        // ① 椅子素材扫描：多组 id 的 effect 帧数 / PNG 尺寸 / origin / z / pos
        var chairIds = new[] { "3010000", "3010001", "3011000", "3015000", "3016000", "3017000", "3018000", "3019000", "3020000", "3010002", "3010010", "3012000", "3014000", "3015800", "3018600" };
        Console.WriteLine("\n== 椅子 effect 素材扫描 ==");
        foreach (var id in chairIds)
        {
            string root = FindChairRoot(wz, id);
            if (root.Length == 0)
            {
                Console.WriteLine($"  [{id}] 根路径未命中");
                continue;
            }
            var info = wz.WithWzLock(() =>
            {
                var n = wz.FindNodeByPath($"{root}/info");
                if (n == null) return "";
                var sb = new System.Text.StringBuilder();
                foreach (Wz_Node c in n.Nodes)
                {
                    if (c.Text is "tamingMob" or "bodyRelMove" or "sitAction" or "sitEmotion" or "removeBody" or "invisibleWeapon" or "invisibleCape")
                    {
                        sb.Append($" {c.Text}={ValueStr(c)}");
                    }
                }
                return sb.ToString();
            });
            int frames = CountNumericChildren(wz, $"{root}/effect");
            var png = wz.ExtractPng($"{root}/effect/0");
            string dim = "?";
            if (png != null && png.Length > 24)
            {
                int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
                int h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
                dim = $"{w}x{h}";
            }
            var origin = wz.GetOrigin($"{root}/effect/0");
            int z = wz.GetIntProperty($"{root}/effect/z");
            int pos = wz.GetIntProperty($"{root}/effect/pos");
            Console.WriteLine($"  [{id}] {root}: effect 帧={frames} effect/0={dim} ({png?.Length ?? 0}字节) origin=({origin.x},{origin.y}) z={z} pos={pos}{info}");
        }

        // ② 渲染验证：抽查 6 把椅子合成 PNG + 像素分析
        var renderIds = new[] { "3010000", "3010001", "3011000", "3015000", "3020000", "3018600", "3010081", "3010082" };
        foreach (var id in renderIds)
        {
            string root = FindChairRoot(wz, id);
            if (root.Length == 0)
            {
                Console.WriteLine($"\n== [{id}] 根路径未命中，跳过渲染 ==");
                continue;
            }
            var withChair = new CharacterAppearance
            {
                Gender = 0,
                BodyId = 2000,
                Ear = PaperdollService.EarHuman,
                Hair = hair ?? new ItemInfo(),
                Face = face ?? new ItemInfo(),
                Chair = new ChairInfo { Id = id },
                EnableEffect = true,
            };
            var noChair = new CharacterAppearance
            {
                Gender = 0,
                BodyId = 2000,
                Ear = PaperdollService.EarHuman,
                Hair = hair ?? new ItemInfo(),
                Face = face ?? new ItemInfo(),
                Chair = null,
                EnableEffect = true,
            };
            Console.WriteLine($"\n== [{id}] 渲染验证 ==");
            // 部件落层摘要
            var items = paperdoll.ProbeLayerResolution(withChair, "stand1", 0);
            var chairItems = items.Where(x => x.Category == "chair").ToList();
            Console.WriteLine($"  stand1/0 部件 {items.Count} 件，椅子 {chairItems.Count} 件:");
            foreach (var c in chairItems)
            {
                Console.WriteLine($"    ▶ {c.PieceName} z={c.ZField} → 层 {c.ResolvedLayer}(idx {c.ZIndex}) map={c.HasMap} locked={c.Locked} hidden={c.Hidden}");
            }
            // 合成 stand1/0 与 sit/0（坐姿）
            foreach (var act in new[] { "stand1", "sit" })
            {
                var png = paperdoll.CompositeFrame(withChair, act, 0);
                if (png != null && png.Length > 0)
                {
                    File.WriteAllBytes($"{outDir}/{id}_{act}_withchair.png", png);
                    Console.WriteLine($"  合成 {act}/0 → {outDir}/{id}_{act}_withchair.png ({png.Length} 字节)");
                }
                else
                {
                    Console.WriteLine($"  合成 {act}/0 失败");
                }
                var pngNo = paperdoll.CompositeFrame(noChair, act, 0);
                if (pngNo != null && pngNo.Length > 0)
                {
                    File.WriteAllBytes($"{outDir}/{id}_{act}_nochair.png", pngNo);
                }
            }
            // 像素分析：带椅子（内部解析坐姿）vs 无椅子（显式 sit）——同为坐姿才可比（否则 diff 混入姿势差）
            paperdoll.SetAppearance(withChair);
            var (bw, bh, boX, boY) = paperdoll.GetActionBounds("sit");
            paperdoll.SetAppearance(noChair);
            var (nbw, nbh, nboX, nboY) = paperdoll.GetActionBounds("sit");
            Console.WriteLine($"  [几何] 带椅子 sit union: {bw}x{bh} origin=({boX},{boY}) | 无椅子 sit union: {nbw}x{nbh} origin=({nboX},{nboY})");
            PixelDiff(paperdoll, withChair, noChair, boX, boY, nboX, nboY, id, "sit", outDir);
            // 椅子本体精灵另存（判断椅子图像本身是什么）
            var chairPng = wz.ExtractPng($"{root}/effect/0");
            if (chairPng != null && chairPng.Length > 0)
            {
                File.WriteAllBytes($"{outDir}/sprite_{id}_effect0.png", chairPng);
                Console.WriteLine($"  椅子本体精灵 → {outDir}/sprite_{id}_effect0.png");
            }
        }

        // ④ 坐姿可行性：基础外观在 stand1 vs sit 下各部件是否都有帧（强制坐姿会不会导致部件消失）
        Console.WriteLine("\n== 坐姿可行性：stand1 vs sit 部件覆盖 ==");
        foreach (var act in new[] { "stand1", "sit" })
        {
            var baseA = new CharacterAppearance
            {
                Gender = 0, BodyId = 2000, Ear = PaperdollService.EarHuman,
                Hair = hair ?? new ItemInfo(), Face = face ?? new ItemInfo(),
            };
            var items = paperdoll.ProbeLayerResolution(baseA, act, 0);
            var cats = items.GroupBy(x => x.Category).Select(g => $"{g.Key}:{g.Count()}").ToList();
            Console.WriteLine($"  {act}/0: 部件 {items.Count} 件 [{string.Join(" ", cats)}]");
            // 各部件帧数
            foreach (var root in new[]
            {
                "Character/00002000.img", "Character/00012000.img",
                $"Character/Hair/{((hair ?? new ItemInfo()).Id ?? "").PadLeft(8, '0')}.img",
                $"Character/Face/{((face ?? new ItemInfo()).Id ?? "").PadLeft(8, '0')}.img",
            })
            {
                int c = CountFrames(wz, $"{root}/{act}");
                Console.WriteLine($"    {root}/{act} 帧数: {c}");
            }
        }

        // ⑤ body 锚点对比：stand1/0 vs sit/0 的 navel 绝对位置（坐姿时 navel 下移多少 → 椅子应跟随）
        Console.WriteLine("\n== body navel 锚点对比 ==");
        foreach (var act in new[] { "stand1", "sit" })
        {
            var map = wz.GetPieceMap($"Character/00002000.img/{act}/0/body");
            if (map != null && map.TryGetValue("navel", out var nv))
            {
                Console.WriteLine($"  {act}: navel map=({nv.x},{nv.y})");
            }
            else
            {
                Console.WriteLine($"  {act}: 无 navel map (map={(map == null ? "null" : string.Join(",", map.Keys))})");
            }
        }

        // ⑥ effect2 采样：z/pos 分布（评估 effect2 层与位置）
        Console.WriteLine("\n== effect2 采样（前 8 个带 effect2 的椅子）==");
        int sampled = 0;
        foreach (var group in new[] { "03010.img", "03011.img", "03014.img", "03018.img", "030150.img", "0302.img" })
        {
            foreach (var id in wz.GetImgChildren($"Item/Install/{group}"))
            {
                if (!int.TryParse(id, out var v) || v < 3010000 || v >= 3021000) continue;
                string node = v.ToString("D8");
                bool hasE2 = wz.WithWzLock(() => wz.FindNodeByPath($"Item/Install/{group}/{node}/effect2") != null);
                if (!hasE2) continue;
                string root = $"Item/Install/{group}/{node}";
                int e2z = wz.GetIntProperty($"{root}/effect2/z");
                int e2pos = wz.GetIntProperty($"{root}/effect2/pos");
                int e2frames = CountNumericChildren(wz, $"{root}/effect2");
                var e2png = wz.ExtractPng($"{root}/effect2/0");
                string dim = "?";
                if (e2png != null && e2png.Length > 24)
                {
                    dim = $"{((e2png[16] << 24) | (e2png[17] << 16) | (e2png[18] << 8) | e2png[19])}x{((e2png[20] << 24) | (e2png[21] << 16) | (e2png[22] << 8) | e2png[23])}";
                }
                var e2origin = wz.GetOrigin($"{root}/effect2/0");
                Console.WriteLine($"  {v}: effect2 z={e2z} pos={e2pos} 帧={e2frames} effect2/0={dim} origin=({e2origin.x},{e2origin.y})");
                if (++sampled >= 8) break;
            }
            if (sampled >= 8) break;
        }
    }

    /// <summary>像素级椅子可见性验证：带椅子 vs 无椅子逐像素 diff（世界坐标对齐）。
    /// 输出 diff 像素数 / 椅子 bbox / 与身体重叠数 / 椅子相对身体的上中下位置。</summary>
    private static void PixelDiff(PaperdollService paperdoll, CharacterAppearance withChair, CharacterAppearance noChair,
        int oxA, int oyA, int oxB, int oyB, string id, string action, string outDir)
    {
        try
        {
            var pngA = paperdoll.CompositeFrame(withChair, action, 0);
            var pngB = paperdoll.CompositeFrame(noChair, action, 0);
            if (pngA == null || pngB == null)
            {
                Console.WriteLine($"  [像素验证] 合成失败，跳过");
                return;
            }
            using var bmpA = SKBitmap.Decode(pngA);
            using var bmpB = SKBitmap.Decode(pngB);
            int aw = bmpA.Width, ah = bmpA.Height, bw2 = bmpB.Width, bh2 = bmpB.Height;
            int wMinX = Math.Min(-oxA, -oxB);
            int wMinY = Math.Min(-oyA, -oyB);
            int wMaxX = Math.Max(-oxA + aw, -oxB + bw2);
            int wMaxY = Math.Max(-oyA + ah, -oyB + bh2);
            int W = wMaxX - wMinX, H = wMaxY - wMinY;
            if (W <= 0 || H <= 0)
            {
                return;
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
            // 身体轮廓 bbox（无椅子图不透明像素的包围盒）——用于判定椅子相对位置
            int bodyMinX = W, bodyMinY = H, bodyMaxX = -1, bodyMaxY = -1;
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
                        if (bOpaque)
                        {
                            if (dx < bodyMinX) bodyMinX = dx;
                            if (dy < bodyMinY) bodyMinY = dy;
                            if (dx > bodyMaxX) bodyMaxX = dx;
                            if (dy > bodyMaxY) bodyMaxY = dy;
                        }
                        bool same = aIn && bIn && pa[ai] == pb[bi] && pa[ai + 1] == pb[bi + 1] && pa[ai + 2] == pb[bi + 2] && pa[ai + 3] == pb[bi + 3];
                        if (same) continue;
                        if (!aOpaque) continue;
                        diffCount++;
                        if (dx < minX) minX = dx;
                        if (dy < minY) minY = dy;
                        if (dx > maxX) maxX = dx;
                        if (dy > maxY) maxY = dy;
                        if (bOpaque) overlapBody++;
                        int di = (dy * W + dx) * 4;
                        pd[di] = 0; pd[di + 1] = 0; pd[di + 2] = 255; pd[di + 3] = pa[ai + 3];
                    }
                }
            }
            using var img = SKImage.FromBitmap(diff);
            var png = img.Encode(SKEncodedImageFormat.Png, 100).ToArray();
            File.WriteAllBytes($"{outDir}/{id}_{action}_chair_diff.png", png);
            if (diffCount == 0)
            {
                Console.WriteLine($"  [像素验证] ✗ 椅子完全不可见（diff=0）");
                return;
            }
            // 位置判定：椅子 bbox 中心相对身体 bbox 中心
            int cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
            int bodyCy = (bodyMinY + bodyMaxY) / 2;
            string pos;
            if (cy < bodyMinY) pos = "身体上方";
            else if (cy > bodyMaxY) pos = "身体下方";
            else pos = cy < bodyCy ? "身体中部偏上" : "身体中部偏下";
            Console.WriteLine($"  [像素验证] 椅子可见像素 {diffCount} 个, bbox=({minX},{minY})-({maxX},{maxY})");
            Console.WriteLine($"  [像素验证] 身体轮廓 bbox=({bodyMinX},{bodyMinY})-({bodyMaxX},{bodyMaxY})");
            Console.WriteLine($"  [像素验证] 椅子中心 ({cx},{cy}) vs 身体中心 y={bodyCy} → 位置: {pos}；与身体重叠 {overlapBody} 像素");
            Console.WriteLine($"  [像素验证] 结论: {(overlapBody > diffCount / 3 ? "⚠ 椅子大面积被身体遮挡/重叠（疑似锚点/层级不对）" : "✓ 椅子可见且基本在身体轮廓外")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [像素验证] 异常: {ex.Message}");
        }
    }

    private static string FindChairRoot(WzService wz, string id)
    {
        int n = int.Parse(id);
        string node = n.ToString("D8");
        string[] candidates =
        {
            $"Item/Install/{n / 1000:D5}.img/{node}",
            $"Item/Install/{n / 100:D6}.img/{node}",
            $"Item/Install/{n / 10000:D4}.img/{node}",
            $"Item/Install/0301.img/{node}",
            $"Item/Cash/{n / 1000:D5}.img/{node}",
            $"Item/Cash/{n / 100:D6}.img/{node}",
            $"Item/Cash/{n / 10000:D4}.img/{node}",
        };
        foreach (var c in candidates)
        {
            if (wz.WithWzLock(() => wz.FindNodeByPath(c) != null)) return c;
        }
        return "";
    }

    private static int CountNumericChildren(WzService wz, string path)
    {
        try
        {
            return wz.WithWzLock(() =>
            {
                var node = wz.FindNodeByPath(path);
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

    private static string ValueStr(Wz_Node n)
    {
        var v = n.Value;
        if (v == null) return "(null)";
        if (v is Wz_Vector vec) return $"({vec.X},{vec.Y})";
        if (v is string s) return $"\"{s}\"";
        if (v is int i) return i.ToString();
        return v.GetType().Name;
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
