using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Models;
using MiniPet.Services;
using WzComparerR2.WzLib;

namespace MiniPet.CrashSelfTest;

/// <summary>
/// 饰品（脸/眼/耳环）渲染调研探针（StartupObject 模式，参考 CapeProbe 写法）：
/// ① dump 脸型 img（Character/Face/{id}.img）结构：表情（default/blink/…）→ 帧号 → piece（face/eye/…）的
///    实际形态（是否带数字帧、piece 名、z/origin/map），对照 MapleSalon2 的 blink/0/face 图标路径规则；
/// ② dump 饰品 img（Character/Accessory/{id}.img，101 面饰 / 102 眼饰 / 103 耳环）：顶层键、帧结构、piece 名；
/// ③ 用完整管线（ProbeLayerResolution）合成含 脸+耳环+面饰 的外观，看 face/earring 部件是否被收集、落层是否正确；
/// ④ 验证 MapleSalon2 图标路径规则（face→blink/0/face、hair→default/hair、equip→info/icon）在本 WZ 是否存在。
/// 用法：dotnet run --project MiniPet.CrashSelfTest -p:UsedAvaloniaProducts= -p:StartupObject=MiniPet.CrashSelfTest.AccessoryProbe
/// </summary>
public static class AccessoryProbe
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

        DumpFaceStructure(wz);
        DumpAccessoryStructure(wz);
        DumpFaceAccessoryIconPaths(wz);
        DumpPaperdollPipeline(wz);
    }

    // ── ① 脸型结构 ──
    private static void DumpFaceStructure(WzService wz)
    {
        var dirNode = wz.FindNodeByPath("Character/Face");
        var faceIds = new List<string>();
        if (dirNode != null)
        {
            foreach (Wz_Node c in dirNode.Nodes)
            {
                if (!c.Text.EndsWith(".img")) continue;
                var id = c.Text[..^4];
                if (id.Length >= 7 && id.StartsWith("0")) id = id.TrimStart('0');
                if (int.TryParse(id, out _)) faceIds.Add(id);
                if (faceIds.Count >= 20) break;
            }
        }
        Console.WriteLine($"\n========== 脸型结构（共取 {faceIds.Count} 个脸） ==========");
        foreach (var id in new[] { "20000", "20001", "20002" })
        {
            DumpFace(wz, id);
        }
    }

    private static void DumpFace(WzService wz, string id)
    {
        string root = $"Character/Face/{id.PadLeft(8, '0')}.img";
        var node = wz.FindNodeByPath(root);
        Console.WriteLine($"\n== 脸 [{id}] {root} ==\n  顶层子节点:");
        if (node == null)
        {
            Console.WriteLine($"  {root} 不存在");
            return;
        }
        var topKids = new List<string>();
        foreach (Wz_Node c in node.Nodes)
        {
            topKids.Add(c.Text);
            // info 子节点
            if (c.Text == "info")
            {
                var infoKids = new List<string>();
                foreach (Wz_Node i in c.Nodes)
                {
                    string val = i.Value switch
                    {
                        Wz_Vector => $"vec({(i.GetValueEx<Wz_Vector>(null)?.X ?? 0)},{(i.GetValueEx<Wz_Vector>(null)?.Y ?? 0)})",
                        _ => i.GetValueEx<string>(null) ?? i.Value?.ToString() ?? "?"
                    };
                    infoKids.Add($"{i.Text}={val}");
                }
                Console.WriteLine($"    info: {string.Join(", ", infoKids)}");
            }
        }
        Console.WriteLine($"  表情节点: {string.Join(", ", topKids)}");
        foreach (var expr in new[] { "default", "blink", "angry", "smile" })
        {
            var exprNode = wz.FindNodeByPath($"{root}/{expr}");
            if (exprNode == null)
            {
                Console.WriteLine($"  [{expr}] → 不存在");
                continue;
            }
            var kids = new List<string>();
            foreach (Wz_Node c in exprNode.Nodes)
            {
                string kind = c.Value switch
                {
                    Wz_Png => "png",
                    Wz_Uol => "uol",
                    Wz_Vector => "vec",
                    Wz_Image => "img",
                    _ => c.GetValueEx<string>(null) != null ? "str" : "node"
                };
                var cPath = $"{root}/{expr}/{c.Text}";
                string z = "";
                var zNode = c.FindNodeByPath("z");
                if (zNode == null) zNode = wz.FindNodeByPath($"{cPath}/z");
                if (zNode != null) z = $" z={zNode.GetValueEx<string>(null) ?? zNode.Value?.ToString() ?? "?"}";
                kids.Add($"{c.Text}({kind}{z})");
            }
            Console.WriteLine($"  [{expr}] 子节点({kids.Count}): {string.Join(", ", kids)}");
            // 逐帧 dump 第一帧内部 piece
            DumpFaceFrame(wz, root, expr);
        }
    }

    /// <summary>dump 表情下的首帧（或直接 piece）内部结构：piece 名 + z + origin/map。</summary>
    private static void DumpFaceFrame(WzService wz, string faceRoot, string expr)
    {
        // 结构 A：{expr}/0/{piece}；结构 B：{expr}/{piece}（default 单帧无数字）
        string framePath = $"{faceRoot}/{expr}/0";
        var frameNode = wz.FindNodeByPath(framePath);
        if (frameNode != null)
        {
            Console.WriteLine($"  [{expr}] 帧 0 子节点:");
            DumpPieces(wz, framePath, "    ");
            return;
        }
        var directNode = wz.FindNodeByPath($"{faceRoot}/{expr}");
        if (directNode != null)
        {
            bool hasPngChild = false;
            foreach (Wz_Node c in directNode.Nodes)
            {
                if (c.GetValue<Wz_Png>() != null || c.GetValue<Wz_Uol>() != null) { hasPngChild = true; break; }
            }
            if (hasPngChild)
            {
                Console.WriteLine($"  [{expr}] 无数字帧，直接 piece:");
                DumpPieces(wz, $"{faceRoot}/{expr}", "    ");
            }
        }
    }

    /// <summary>dump 某节点下所有 piece：名 + 值类型 + z 值 + origin/map 存在性。</summary>
    private static void DumpPieces(WzService wz, string path, string indent)
    {
        var node = wz.FindNodeByPath(path);
        if (node == null)
        {
            Console.WriteLine($"{indent}{path} → 不存在");
            return;
        }
        foreach (Wz_Node c in node.Nodes)
        {
            if (c.Text == "delay" || c.Text == "move") continue;
            string kind = c.Value switch
            {
                Wz_Png => "png",
                Wz_Uol => "uol",
                Wz_Vector => "vec",
                _ => c.GetValueEx<string>(null) != null ? "str" : "node"
            };
            var cPath = $"{path}/{c.Text}";
            string z = "";
            var zNode = c.FindNodeByPath("z");
            if (zNode == null) zNode = wz.FindNodeByPath($"{cPath}/z");
            if (zNode != null) z = zNode.GetValueEx<string>(null) ?? zNode.Value?.ToString() ?? "?";
            bool origin = c.FindNodeByPath("origin") != null || wz.FindNodeByPath($"{cPath}/origin") != null;
            bool map = c.FindNodeByPath("map") != null || wz.FindNodeByPath($"{cPath}/map") != null;
            Console.WriteLine($"{indent}{c.Text}({kind}{(origin ? ",o" : "")}{(map ? ",m" : "")} z={z})");
        }
    }

    // ── ② 饰品结构 ──
    private static void DumpAccessoryStructure(WzService wz)
    {
        // 101 面饰 / 102 眼饰 / 103 耳环
        var samples = new[]
        {
            ("101", "01010000.img"), ("101", "01010011.img"),
            ("102", "01020000.img"), ("102", "01020010.img"),
            ("103", "01030000.img"), ("103", "01030010.img"),
        };
        Console.WriteLine("\n========== 饰品结构（Character/Accessory） ==========");
        foreach (var (prefix, img) in samples)
        {
            string root = $"Character/Accessory/{img}";
            var node = wz.FindNodeByPath(root);
            Console.WriteLine($"\n== Accessory [{prefix}] {root} ==");
            if (node == null)
            {
                Console.WriteLine($"  {root} 不存在");
                continue;
            }
            var topKids = new List<string>();
            foreach (Wz_Node c in node.Nodes)
            {
                topKids.Add(c.Text);
                if (c.Text == "info")
                {
                    var infoKids = new List<string>();
                    foreach (Wz_Node i in c.Nodes)
                    {
                        string val = i.Value switch
                        {
                            Wz_Vector => $"vec({(i.GetValueEx<Wz_Vector>(null)?.X ?? 0)},{(i.GetValueEx<Wz_Vector>(null)?.Y ?? 0)})",
                            _ => i.GetValueEx<string>(null) ?? i.Value?.ToString() ?? "?"
                        };
                        infoKids.Add($"{i.Text}={val}");
                    }
                    Console.WriteLine($"    info: {string.Join(", ", infoKids)}");
                }
            }
            Console.WriteLine($"  顶层子节点: {string.Join(", ", topKids)}");
            // 逐节点 dump 内部：default / backDefault / stand1/0 / stand1/1
            foreach (var sub in new[] { "default", "backDefault" })
            {
                var subNode = wz.FindNodeByPath($"{root}/{sub}");
                if (subNode == null) continue;
                bool hasPng = false;
                foreach (Wz_Node c in subNode.Nodes)
                {
                    if (c.GetValue<Wz_Png>() != null || c.GetValue<Wz_Uol>() != null) { hasPng = true; break; }
                }
                if (hasPng)
                {
                    Console.WriteLine($"  [{sub}] 直接 piece:");
                    DumpPieces(wz, $"{root}/{sub}", "    ");
                }
                else
                {
                    Console.WriteLine($"  [{sub}] 帧:");
                    foreach (Wz_Node c in subNode.Nodes)
                    {
                        if (!int.TryParse(c.Text, out _)) continue;
                        Console.WriteLine($"    [{sub}/{c.Text}] piece:");
                        DumpPieces(wz, $"{root}/{sub}/{c.Text}", "      ");
                        break;
                    }
                }
            }
            foreach (var act in new[] { "stand1", "walk1" })
            {
                var actNode = wz.FindNodeByPath($"{root}/{act}");
                if (actNode == null) continue;
                foreach (Wz_Node c in actNode.Nodes)
                {
                    if (!int.TryParse(c.Text, out _)) continue;
                    Console.WriteLine($"  [{act}/{c.Text}] piece:");
                    DumpPieces(wz, $"{root}/{act}/{c.Text}", "    ");
                    // uol 目标解析
                    foreach (Wz_Node p in c.Nodes)
                    {
                        if (p.GetValue<Wz_Uol>() != null)
                        {
                            var uol = p.GetValue<Wz_Uol>();
                            Console.WriteLine($"    uol {p.Text} → {uol}");
                        }
                    }
                    break;
                }
                break;
            }
        }
    }

    private static void DumpAccessoryFrame(WzService wz, string root, string expr)
    {
        string framePath = $"{root}/{expr}/0";
        var frameNode = wz.FindNodeByPath(framePath);
        if (frameNode != null)
        {
            Console.WriteLine($"  [{expr}/0] 子节点:");
            DumpPieces(wz, framePath, "    ");
        }
    }

    // ── ③ MapleSalon2 图标路径规则验证 ──
    private static void DumpFaceAccessoryIconPaths(WzService wz)
    {
        Console.WriteLine("\n========== MapleSalon2 图标路径规则验证 ==========");
        // face→blink/0/face；hair→default/hair；equip→info/icon
        var checks = new[]
        {
            ("face", "Character/Face/00020000.img/blink/0/face"),
            ("face", "Character/Face/00020000.img/0/face"),
            ("face", "Character/Face/00020000.img/default/0/face"),
            ("face", "Character/Face/00020000.img/default/face"),
            ("hair", "Character/Hair/00030000.img/default/hair"),
            ("hair", "Character/Hair/00030000.img/default/0/hair"),
            ("cap", "Character/Cap/01000000.img/info/icon"),
            ("earring", "Character/Accessory/01030000.img/info/icon"),
        };
        foreach (var (kind, path) in checks)
        {
            var node = wz.FindNodeByPath(path);
            string extra = "";
            if (node != null)
            {
                var png = node.GetValue<Wz_Png>();
                var uol = node.GetValue<Wz_Uol>();
                extra = png != null ? $" png={png.Width}x{png.Height}" : uol != null ? $" uol→{uol.ToString()}" : " (无png)";
            }
            Console.WriteLine($"  [{kind}] {path} → {(node == null ? "不存在" : "存在" + extra)}");
        }
        // hair default/hair 出图验证
        foreach (var hairId in new[] { "30000", "30001", "30002" })
        {
            var png = wz.ExtractPng($"Character/Hair/{hairId.PadLeft(8, '0')}.img/default/hair");
            Console.WriteLine($"  hair {hairId} default/hair 出图: {png != null} ({png?.Length ?? 0} 字节)");
        }
    }

    // ── ④ 完整管线：脸+耳环+面饰+眼饰 合成收集与落层 ──
    private static void DumpPaperdollPipeline(WzService wz)
    {
        Console.WriteLine("\n========== 完整管线合成（脸 + 耳环 + 面饰 + 眼饰） ==========");
        var cache = new CacheManager();
        var paperdoll = new PaperdollService(wz, cache, new SpriteService(cache));

        // 先探测可用脸/饰品 id
        string faceId = PickFirstId(wz, "Character/Face");
        string acc101 = PickFirstId(wz, "Character/Accessory", "101");
        string acc102 = PickFirstId(wz, "Character/Accessory", "102");
        string acc103 = PickFirstId(wz, "Character/Accessory", "103");
        string hairId = PickFirstId(wz, "Character/Hair", "3");
        Console.WriteLine($"  探测: face={faceId} acc101={acc101} acc102={acc102} acc103={acc103} hair={hairId}");

        var appearance = new CharacterAppearance
        {
            Gender = 0,
            BodyId = 2000,
            Ear = PaperdollService.EarHuman,
            Face = new ItemInfo { Id = faceId },
            Hair = new ItemInfo { Id = hairId },
            Earring = new ItemInfo { Id = acc103 },
            // 101 面饰 / 102 眼饰 也塞进 Earring（当前 UI 就这么映射）
        };
        foreach (var act in new[] { "stand1", "walk1", "jump" })
        {
            var items = paperdoll.ProbeLayerResolution(appearance, act, 0);
            Console.WriteLine($"\n  == 动作 {act} 帧0: 收集 {items.Count} 件, 其中 face={items.Count(x => x.Category == "face")} earring={items.Count(x => x.Category == "earring")} ==");
            foreach (var it in items.Where(x => x.Category is "face" or "earring" or "head" or "hair" or "body"))
            {
                Console.WriteLine($"    [{it.Category,-7}] piece={it.PieceName,-18} z={it.ZField,-16} → 层={it.ResolvedLayer,-22} idx={it.ZIndex,4} map={it.HasMap,5} locked={it.Locked,5} hidden={it.Hidden,5}");
            }
        }

        // 面饰 101 / 眼饰 102 单独测（当前 UI 都映射到 Earring → 验证是否错误）
        foreach (var (label, accId) in new[] { ("面饰101", acc101), ("眼饰102", acc102), ("耳环103", acc103) })
        {
            if (string.IsNullOrEmpty(accId)) continue;
            var a2 = new CharacterAppearance
            {
                Gender = 0,
                BodyId = 2000,
                Ear = PaperdollService.EarHuman,
                Face = new ItemInfo { Id = faceId },
                Hair = new ItemInfo { Id = hairId },
                Earring = new ItemInfo { Id = accId },
            };
            var items2 = paperdoll.ProbeLayerResolution(a2, "stand1", 0);
            Console.WriteLine($"\n  == {label} {accId}: 收集 {items2.Count} 件, earring={items2.Count(x => x.Category == "earring")} ==");
            foreach (var it in items2.Where(x => x.Category == "earring"))
            {
                Console.WriteLine($"    piece={it.PieceName,-18} z={it.ZField,-16} → 层={it.ResolvedLayer,-22} idx={it.ZIndex,4} map={it.HasMap,5} locked={it.Locked,5} hidden={it.Hidden,5}");
            }
        }

        // blink 表情合成验证（face blink 是数字帧 blink/0/face，修复前 {root}/blink 子节点被当 piece → 全丢 → 脸消失）
        var blinkAppearance = new CharacterAppearance
        {
            Gender = 0,
            BodyId = 2000,
            Ear = PaperdollService.EarHuman,
            Face = new ItemInfo { Id = faceId },
            Hair = new ItemInfo { Id = hairId },
        };
        foreach (var expr in new[] { PaperdollService.ExpressionDefault, PaperdollService.ExpressionBlink })
        {
            foreach (var f in new[] { 0, 1, 2 })
            {
                var png = paperdoll.CompositeFrame(blinkAppearance, "stand1", f, expr);
                Console.WriteLine($"  == 表情 {expr} stand1 帧{f} 合成: {png != null} ({png?.Length ?? 0} 字节) ==");
            }
        }
    }

    /// <summary>取某目录第一个数字 id（可按 id 前缀过滤，如 "101" 匹配 101xxxxx 面饰）。</summary>
    private static string PickFirstId(WzService wz, string dirPath, string? prefix = null)
    {
        var dir = wz.FindNodeByPath(dirPath);
        if (dir == null) return "";
        foreach (Wz_Node c in dir.Nodes)
        {
            if (!c.Text.EndsWith(".img")) continue;
            var name = c.Text[..^4]; // 8 位补零，如 01010000
            if (!int.TryParse(name, out var n)) continue;
            if (!string.IsNullOrEmpty(prefix))
            {
                if (!int.TryParse(prefix, out var p)) continue;
                if (n / 10000 != p) continue;
            }
            // 验证根节点存在
            if (wz.FindNodeByPath($"{dirPath}/{c.Text}") != null) return n.ToString();
        }
        return "";
    }
}
