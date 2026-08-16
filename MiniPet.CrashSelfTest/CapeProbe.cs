using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Models;
using MiniPet.Services;
using WzComparerR2.WzLib;

namespace MiniPet.CrashSelfTest;

/// <summary>
/// 披风渲染调研探针（StartupObject 模式，参考 IconProbe 写法）：
/// ① dump 真实 zmap 层序（183 层）+ 披风/effect 相关层位置；
/// ② 多披风（含 1102149 冰凌披风）部件 ZField 分布 + PaperdollService.ProbeLayerResolution 落层结果
///    （同一二进制在修复前后各跑一次 → 修复前后落层对比）；
/// ③ Character/Cape/01102149.img 的 effect 节点结构（节点级/帧级？动画帧？canvas 结构）+ info/enableEffect。
/// 用法：dotnet run --project MiniPet.CrashSelfTest -p:UsedAvaloniaProducts= -p:StartupObject=MiniPet.CrashSelfTest.CapeProbe
/// </summary>
public static class CapeProbe
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
        // 环境变量 MINIPET_WZ_BASE 可覆盖 WZ 路径（桌宠运行中锁文件时，探针可指向 /tmp 副本）
        string basePath = Environment.GetEnvironmentVariable("MINIPET_WZ_BASE");
        if (string.IsNullOrEmpty(basePath)) basePath = wzConfig.BaseWzPath;
        Console.WriteLine($"WZ 路径: {basePath}");
        var (ok, _) = wz.LoadWz(wzConfig.WzLibPath, basePath);
        Console.WriteLine($"WZ 加载: {ok}");
        if (!ok) return;

        DumpZmap(wz);
        DumpCapePieces(wz);
        DumpEffectStructure(wz);
        DumpInfoNode(wz, "1102149");
        ScanEffects(wz);
    }

    // ── ① zmap 层序 ──
    private static void DumpZmap(WzService wz)
    {
        var zmap = wz.GetZmapOrder();
        Console.WriteLine($"\n========== zmap 层序 ({zmap.Count} 层) ==========");
        for (int i = 0; i < zmap.Count; i++)
        {
            Console.WriteLine($"{i,4}: {zmap[i]}");
        }
        Console.WriteLine("\n== cape/effect 相关层 ==");
        for (int i = 0; i < zmap.Count; i++)
        {
            var lower = zmap[i].ToLowerInvariant();
            if (lower.Contains("cape") || lower.Contains("effect") || lower.Contains("mail") ||
                lower.Contains("weapon") || lower.Contains("arm") || lower.Contains("glove"))
            {
                Console.WriteLine($"{i,4}: {zmap[i]}");
            }
        }
    }

    // ── ② 披风部件 ZField 分布 + 落层（修复前后同一二进制对照）──
    private static void DumpCapePieces(WzService wz)
    {
        // MapleSalon2 fixLayers 注释里出现过的披风 id + 任务指定 110249 + 本 WZ 实测含帧级 effect 的披风
        var capeIds = new[] { "1102149", "1102000", "1102081", "1102270", "1102613", "1102657", "1102665", "1102682", "1103058", "1103098", "1103741", "1103488", "1103489", "1103490", "1103733", "1103734" };
        var cache = new CacheManager();
        var paperdoll = new PaperdollService(wz, cache, new SpriteService(cache));
        string[] actions = { "stand1", "walk1", "jump", "alert", "swingOF", "swingO1", "sit", "prone" };

        foreach (var id in capeIds)
        {
            string root = $"Character/Cape/{id.PadLeft(8, '0')}.img";
            var node = wz.FindNodeByPath(root);
            if (node == null)
            {
                Console.WriteLine($"\n== [{id}] {root} 不存在 ==");
                continue;
            }
            var appearance = new CharacterAppearance
            {
                Gender = 0,
                BodyId = 2000,
                Ear = PaperdollService.EarHuman,
                Cape = new ItemInfo { Id = id }
            };
            // 找一个该披风有部件的动作/帧
            string usedAction = "";
            int usedFrame = -1;
            List<PaperdollService.LayerProbeItem>? usedItems = null;
            foreach (var act in actions)
            {
                var actNode = node.FindNodeByPath(act);
                if (actNode == null) continue;
                foreach (Wz_Node c in actNode.Nodes)
                {
                    if (!int.TryParse(c.Text, out _)) continue;
                    var items = paperdoll.ProbeLayerResolution(appearance, act, int.Parse(c.Text));
                    if (items.Any(x => x.Category == "cape"))
                    {
                        usedAction = act;
                        usedFrame = int.Parse(c.Text);
                        usedItems = items;
                        break;
                    }
                }
                if (usedItems != null) break;
            }
            if (usedItems == null)
            {
                Console.WriteLine($"\n== [{id}] {root} 无任何动作部件 ==");
                continue;
            }
            Console.WriteLine($"\n== [{id}] {root} 动作 {usedAction}/{usedFrame} 部件落层（共 {usedItems.Count} 件，披风 {usedItems.Count(x => x.Category == "cape")} 件）==");
            foreach (var it in usedItems)
            {
                string mark = it.Category == "cape" ? "▶" : " ";
                Console.WriteLine($"{mark} [{it.Category,-7}] piece={it.PieceName,-18} z={it.ZField,-18} → 层={it.ResolvedLayer,-20} idx={it.ZIndex,4} map={it.HasMap,5} locked={it.Locked,5} hidden={it.Hidden,5}");
            }
            // R13：特效开关对照——含 effect 部件的披风，EnableEffect=false 时应剔除 effect 件
            if (usedItems.Any(x => x.Category == "cape" && x.PieceName.StartsWith("effect", StringComparison.OrdinalIgnoreCase)))
            {
                var off = CloneWithEffect(appearance, false);
                var itemsOff = paperdoll.ProbeLayerResolution(off, usedAction, usedFrame);
                var effectOn = usedItems.Count(x => x.PieceName.StartsWith("effect", StringComparison.OrdinalIgnoreCase));
                var effectOff = itemsOff.Count(x => x.PieceName.StartsWith("effect", StringComparison.OrdinalIgnoreCase));
                Console.WriteLine($"  特效开关对照: EnableEffect=true 含 effect 件 {effectOn} 个, false 含 {effectOff} 个 → {(effectOff == 0 ? "关闭生效 ✓" : "关闭未生效 ✗")}");
            }
        }
    }

    private static CharacterAppearance CloneWithEffect(CharacterAppearance src, bool enable)
    {
        return new CharacterAppearance
        {
            Gender = src.Gender, BodyId = src.BodyId, Ear = src.Ear,
            Cape = src.Cape != null ? new ItemInfo { Id = src.Cape.Id } : null,
            EnableEffect = enable,
        };
    }

    // ── ③ effect 节点结构 ──
    private static void DumpEffectStructure(WzService wz)
    {
        var ids = new[] { "1102149", "1102000", "1102081", "1102270", "1103488", "1103733" };
        foreach (var id in ids)
        {
            string root = $"Character/Cape/{id.PadLeft(8, '0')}.img";
            Console.WriteLine($"\n========== [{id}] effect 节点结构 ==========");
            // 节点级 effect
            var effNode = wz.FindNodeByPath($"{root}/effect");
            if (effNode == null)
            {
                Console.WriteLine($"{root}/effect → 不存在");
            }
            else
            {
                DumpNode(wz, $"{root}/effect", 0, 3, "effect-root");
            }
            // 帧级 effect：stand1/0/effect（含非 canvas 子节点）
            foreach (var act in new[] { "stand1", "walk1" })
            {
                var actNode = wz.FindNodeByPath($"{root}/{act}");
                if (actNode == null) continue;
                foreach (Wz_Node c in actNode.Nodes)
                {
                    if (!int.TryParse(c.Text, out _)) continue;
                    var fe = wz.FindNodeByPath($"{root}/{act}/{c.Text}/effect");
                    Console.WriteLine($"[{id}] {act}/{c.Text}/effect → {(fe == null ? "无" : "有")}");
                    if (fe != null)
                    {
                        DumpNode(wz, $"{root}/{act}/{c.Text}/effect", 1, 2, "frame-effect");
                    }
                    break;
                }
            }
        }
    }

    /// <summary>递归 dump 节点：子节点名 + 值类型 + origin/map/z 字段存在性。depth 限深防爆。</summary>
    private static void DumpNode(WzService wz, string path, int depth, int maxDepth, string label)
    {
        try
        {
            var node = wz.FindNodeByPath(path);
            if (node == null)
            {
                Console.WriteLine($"{Indent(depth)}{label} {path} → 不存在");
                return;
            }
            var kids = new List<(string Text, string Kind, bool Origin, bool Map, bool Z)>();
            foreach (Wz_Node c in node.Nodes)
            {
                string kind = c.Value switch
                {
                    Wz_Png => "png",
                    Wz_Uol => "uol",
                    Wz_Vector => "vec",
                    Wz_Image => "img",
                    _ => c.GetValueEx<string>(null) != null ? "str" : "node"
                };
                var cPath = $"{path}/{c.Text}";
                kids.Add((c.Text, kind,
                    c.FindNodeByPath("origin") != null || wz.FindNodeByPath($"{cPath}/origin") != null,
                    c.FindNodeByPath("map") != null || wz.FindNodeByPath($"{cPath}/map") != null,
                    c.FindNodeByPath("z") != null || wz.FindNodeByPath($"{cPath}/z") != null));
            }
            Console.WriteLine($"{Indent(depth)}{label} {path}（{kids.Count} 子）: " +
                string.Join(", ", kids.Take(30).Select(k => $"{k.Text}({k.Kind}{(k.Origin ? ",o" : "")}{(k.Map ? ",m" : "")}{(k.Z ? ",z" : "")}")) +
                (kids.Count > 30 ? $" …共{kids.Count}" : ""));
            if (depth >= maxDepth) return;
            // 只递归数值帧节点与 effect 命名字节点（canvas 叶子不再深入）
            foreach (var (text, kind, _, _, _) in kids)
            {
                if (kind == "png" || kind == "uol" || kind == "vec" || kind == "str") continue;
                bool numeric = int.TryParse(text, out _);
                bool effectName = text.ToLowerInvariant().Contains("effect");
                if (numeric || effectName || (depth == 0 && kids.Count <= 40))
                {
                    DumpNode(wz, $"{path}/{text}", depth + 1, maxDepth, "");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{Indent(depth)}DumpNode({path}) 异常: {ex.Message}");
        }
    }

    private static void DumpInfoNode(WzService wz, string id)
    {
        string root = $"Character/Cape/{id.PadLeft(8, '0')}.img";
        Console.WriteLine($"\n========== [{id}] info 节点 ==========");
        var info = wz.FindNodeByPath($"{root}/info");
        if (info == null)
        {
            Console.WriteLine($"{root}/info → 不存在");
            return;
        }
        var kids = new List<string>();
        foreach (Wz_Node c in info.Nodes)
        {
            string val = c.Value switch
            {
                Wz_Png => "<png>",
                Wz_Vector => $"vec({(c.GetValueEx<Wz_Vector>(null)?.X ?? 0)},{(c.GetValueEx<Wz_Vector>(null)?.Y ?? 0)})",
                Wz_Uol => $"uol→{c.GetValueEx<string>(null)}",
                _ => c.GetValueEx<string>(null) ?? c.Value?.ToString() ?? "?"
            };
            kids.Add($"{c.Text}={val}");
        }
        Console.WriteLine($"info 子节点: {string.Join(", ", kids)}");
        var ee = info.FindNodeByPath("enableEffect");
        Console.WriteLine($"info/enableEffect = {ee?.GetValueEx<int>(-999) ?? -999}");
    }

    /// <summary>扫描本 WZ 全部披风 + 抽样武器：effect 节点 / 帧级 effect / info/enableEffect 分布（判断装备特效在该 WZ 的实际情况）。</summary>
    private static void ScanEffects(WzService wz)
    {
        Console.WriteLine("\n========== 全披风 effect 扫描 ==========");
        var capeDir = wz.FindNodeByPath("Character/Cape");
        var capeIds = new List<string>();
        if (capeDir != null)
        {
            foreach (Wz_Node c in capeDir.Nodes)
            {
                if (!c.Text.EndsWith(".img")) continue;
                var id = c.Text[..^4];
                if (id.Length >= 7 && id.StartsWith("0")) id = id.TrimStart('0');
                if (int.TryParse(id, out _)) capeIds.Add(id);
            }
        }
        Console.WriteLine($"本 WZ 披风总数: {capeIds.Count}");
        int withEffect = 0, withFrameEffect = 0, withEnableEffect = 0;
        var effectIds = new List<string>();
        foreach (var id in capeIds)
        {
            string root = $"Character/Cape/{id.PadLeft(8, '0')}.img";
            try
            {
                bool hasEff = wz.FindNodeByPath($"{root}/effect") != null;
                bool hasFrmEff = false;
                bool hasEnable = wz.FindNodeByPath($"{root}/info/enableEffect") != null;
                if (!hasEff)
                {
                    foreach (var act in new[] { "stand1", "walk1", "jump" })
                    {
                        var actNode = wz.FindNodeByPath($"{root}/{act}");
                        if (actNode == null) continue;
                        foreach (Wz_Node fr in actNode.Nodes)
                        {
                            if (!int.TryParse(fr.Text, out _)) continue;
                            if (wz.FindNodeByPath($"{root}/{act}/{fr.Text}/effect") != null) { hasFrmEff = true; break; }
                        }
                        if (hasFrmEff) break;
                    }
                }
                if (hasEff || hasFrmEff || hasEnable)
                {
                    withEffect += hasEff ? 1 : 0;
                    withFrameEffect += hasFrmEff ? 1 : 0;
                    withEnableEffect += hasEnable ? 1 : 0;
                    effectIds.Add($"{id}({(hasEff ? "eff" : "")}{(hasFrmEff ? "frm" : "")}{(hasEnable ? "en" : "")})");
                }
            }
            catch { }
        }
        Console.WriteLine($"含 effect 节点: {withEffect} 个: {string.Join(", ", effectIds.Where(x => x.Contains("eff") && !x.Contains("frm"))) }");
        Console.WriteLine($"含帧级 effect: {withFrameEffect} 个: {string.Join(", ", effectIds.Where(x => x.Contains("frm"))) }");
        Console.WriteLine($"含 info/enableEffect: {withEnableEffect} 个: {string.Join(", ", effectIds.Where(x => x.Contains("en"))) }");
        if (effectIds.Count > 0)
        {
            Console.WriteLine($"全部命中 id: {string.Join(", ", effectIds)}");
        }

        Console.WriteLine("\n========== 武器 effect 抽样（前 40 个武器） ==========");
        var weaponDir = wz.FindNodeByPath("Character/Weapon");
        int weaponChecked = 0;
        int weaponEff = 0, weaponFrm = 0;
        var weaponHit = new List<string>();
        if (weaponDir != null)
        {
            foreach (Wz_Node c in weaponDir.Nodes)
            {
                if (!c.Text.EndsWith(".img")) continue;
                if (weaponChecked >= 40) break;
                weaponChecked++;
                var id = c.Text[..^4];
                if (id.Length >= 7 && id.StartsWith("0")) id = id.TrimStart('0');
                string root = $"Character/Weapon/{id.PadLeft(8, '0')}.img";
                try
                {
                    bool hasEff = wz.FindNodeByPath($"{root}/effect") != null;
                    bool hasFrmEff = false;
                    if (!hasEff)
                    {
                        foreach (var act in new[] { "stand1", "swingO1", "swingOF", "stabO1" })
                        {
                            var actNode = wz.FindNodeByPath($"{root}/{act}");
                            if (actNode == null) continue;
                            foreach (Wz_Node fr in actNode.Nodes)
                            {
                                if (!int.TryParse(fr.Text, out _)) continue;
                                if (wz.FindNodeByPath($"{root}/{act}/{fr.Text}/effect") != null) { hasFrmEff = true; break; }
                            }
                            if (hasFrmEff) break;
                        }
                    }
                    if (hasEff || hasFrmEff)
                    {
                        weaponEff += hasEff ? 1 : 0;
                        weaponFrm += hasFrmEff ? 1 : 0;
                        weaponHit.Add($"{id}({(hasEff ? "eff" : "")}{(hasFrmEff ? "frm" : "")})");
                    }
                }
                catch { }
            }
        }
        Console.WriteLine($"抽查 {weaponChecked} 个武器: 含 effect 节点 {weaponEff} 个, 帧级 effect {weaponFrm} 个: {string.Join(", ", weaponHit.Take(20))}");
    }

    private static string Indent(int depth) => new string(' ', depth * 2);
}
