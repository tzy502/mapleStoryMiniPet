using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Models;
using MiniPet.Services;
using WzComparerR2.WzLib;

namespace MiniPet.CrashSelfTest;

/// <summary>
/// 坐骑动作 probe（R18 修复验证）：
/// ① 扫描代表坐骑 img：顶层动作节点名 / characterAction 映射内容 / 各动作帧数；
/// ② 「修复前」ResolveMountAction（同名→stand1/stand2→枚举）与「修复后」（characterAction 反向映射优先）
///    对角色 stand1/walk1/jump/fly/sit/prone 的动作选择对比；
/// ③ hideBody 真实管线验证：坐骑动作映射 hideBody 时，ProbeLayerResolution 只收 mount、不收 body/head/hair/face。
/// 09010000 系列本 WZ 不存在 → 如实报告跳过，以真实 019 系列坐骑为准。
/// </summary>
public static class MountActionProbe
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
                exitCode = 1;
            }
            Environment.Exit(exitCode);
        });
        Dispatcher.UIThread.MainLoop(CancellationToken.None);
        return exitCode;
    }

    private static void Run()
    {
        var basePath = Environment.GetEnvironmentVariable("MINIPET_WZ_BASE");
        var config = new ConfigService();
        var wzConfig = config.LoadWzConfig();
        var wz = new WzService();
        var (ok, _) = wz.LoadWz(wzConfig.WzLibPath, basePath ?? wzConfig.BaseWzPath);
        Console.WriteLine($"WZ 加载: {ok}");
        if (!ok) return;

        // 任务指定 09010000 系列：本 WZ 如实报告缺失
        Console.WriteLine("=== 0901 系列坐骑存在性（任务指定，本 WZ 缺失则跳过）===");
        foreach (var p in new[] { "Character/TamingMob/09010000.img", "Character/TamingMob/09010001.img", "Character/TamingMob/09010002.img" })
        {
            Console.WriteLine($"  存在[{p}]: {wz.FindNodeByPath(p) != null}");
        }

        // 代表坐骑：普通 019 系列 + 飞行映射 + 战车映射 + hideBody 全覆盖
        var targets = new[] { "01902000", "01902001", "01902002", "01930001", "01992031", "01932294", "01992033" };
        Console.WriteLine($"\n=== 扫描代表坐骑: {string.Join(", ", targets)} ===");
        foreach (var id in targets)
        {
            ScanMount(wz, id);
        }

        // hideBody 真实管线验证（01932294：所有动作映射 hideBody → 只收坐骑）
        Console.WriteLine("\n=== hideBody 真实管线验证（01932294，角色 stand1/0）===");
        VerifyHideBody(wz);

        // 对照：非 hideBody 坐骑（01902000）管线应包含角色部件
        Console.WriteLine("\n=== 对照：普通坐骑 01902000（角色 stand1/0）===");
        VerifyNormalMount(wz);
    }

    private static Wz_Node? GetMountInner(WzService wz, string id)
    {
        var imgNode = wz.FindNodeByPath($"Character/TamingMob/{id}.img");
        if (imgNode == null) return null;
        var img = imgNode.GetValue<Wz_Image>();
        if (img != null && !img.TryExtract()) return null;
        return img != null ? img.Node : imgNode;
    }

    private static void ScanMount(WzService wz, string id)
    {
        Console.WriteLine($"\n--- {id} ---");
        var inner = GetMountInner(wz, id);
        if (inner == null)
        {
            Console.WriteLine($"  {id}.img 不存在或提取失败（跳过）");
            return;
        }
        var actionNodes = new List<string>();
        foreach (Wz_Node top in inner.Nodes)
        {
            if (top.Text == "info" || top.Text == "characterAction" || top.Text == "forcingItem") continue;
            actionNodes.Add(top.Text);
        }
        Console.WriteLine($"  动作节点({actionNodes.Count}): {string.Join(", ", actionNodes)}");

        var ca = inner.FindNodeByPath("characterAction");
        if (ca != null)
        {
            Console.WriteLine("  characterAction:");
            foreach (Wz_Node kv in ca.Nodes)
            {
                Console.WriteLine($"    {kv.Text} → {kv.GetValueEx<string>(null)}");
            }
        }
        else
        {
            Console.WriteLine("  characterAction: 无");
        }

        Console.WriteLine("  各动作帧数:");
        foreach (var act in actionNodes)
        {
            Console.WriteLine($"    {act}: {CountNumericChildren(inner.FindNodeByPath(act))} 帧");
        }

        Console.WriteLine("  动作选择对比（修复前 → 修复后）:");
        foreach (var requested in new[] { "stand1", "walk1", "jump", "fly", "sit", "prone" })
        {
            string oldAct = OldResolveMountAction(inner, requested);
            string newAct = NewResolveMountAction(inner, requested);
            string mark = oldAct == newAct ? "  " : "←差异";
            Console.WriteLine($"    角色 {requested,-6} → 旧:{oldAct,-8} 新:{newAct,-8} {mark}");
        }
    }

    /// <summary>修复前逻辑（复制）：同名 → stand1/stand2 → walk1/walk2/jump/fly/tired/prone 枚举。</summary>
    private static string OldResolveMountAction(Wz_Node mountRoot, string requested)
    {
        if (!string.IsNullOrEmpty(requested) && GetRideActionFrameCount(mountRoot, requested) > 0) return requested;
        if (GetRideActionFrameCount(mountRoot, "stand1") > 0) return "stand1";
        if (GetRideActionFrameCount(mountRoot, "stand2") > 0) return "stand2";
        foreach (var act in new[] { "walk1", "walk2", "jump", "fly", "tired", "prone" })
        {
            if (GetRideActionFrameCount(mountRoot, act) > 0) return act;
        }
        return "";
    }

    /// <summary>修复后逻辑（复制）：characterAction 反向映射（含隐式同名）优先 → 原回退链。</summary>
    private static string NewResolveMountAction(Wz_Node mountRoot, string requested)
    {
        var reverse = BuildCharActionReverse(mountRoot);
        if (!string.IsNullOrEmpty(requested))
        {
            if (reverse.TryGetValue(requested, out var mapped) && GetRideActionFrameCount(mountRoot, mapped) > 0)
            {
                return mapped;
            }
        }
        if (!string.IsNullOrEmpty(requested) && GetRideActionFrameCount(mountRoot, requested) > 0) return requested;
        if (GetRideActionFrameCount(mountRoot, "stand1") > 0) return "stand1";
        if (GetRideActionFrameCount(mountRoot, "stand2") > 0) return "stand2";
        foreach (var act in new[] { "walk1", "walk2", "jump", "fly", "tired", "prone" })
        {
            if (GetRideActionFrameCount(mountRoot, act) > 0) return act;
        }
        return "";
    }

    /// <summary>复制 GetMountCharActionReverse：角色动作名 → 坐骑动作名（隐式同名 + characterAction 反向）。</summary>
    private static Dictionary<string, string> BuildCharActionReverse(Wz_Node inner)
    {
        var r = new Dictionary<string, string>(StringComparer.Ordinal);
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

    private static int GetRideActionFrameCount(Wz_Node mountRoot, string action)
    {
        var actNode = mountRoot.FindNodeByPath(action);
        if (actNode == null) return 0;
        return CountNumericChildren(actNode);
    }

    private static int CountNumericChildren(Wz_Node? node)
    {
        if (node == null) return 0;
        int count = 0;
        foreach (Wz_Node child in node.Nodes)
        {
            if (int.TryParse(child.Text, out _)) count++;
        }
        return count;
    }

    /// <summary>hideBody 坐骑真实管线：ProbeLayerResolution 应只收 mount 部件（body/head/hair/face 全跳过）。</summary>
    private static void VerifyHideBody(WzService wz)
    {
        var cache = new CacheManager();
        var paperdoll = new PaperdollService(wz, cache, new SpriteService(cache));
        var appearance = new CharacterAppearance
        {
            Gender = 0,
            BodyId = 2000,
            Ear = PaperdollService.EarHuman,
            Hair = new ItemInfo { Id = "30000" },
            Face = new ItemInfo { Id = "20000" },
            Mount = new MountInfo { Id = "1932294" } // 01932294：所有动作映射 hideBody
        };
        var items = paperdoll.ProbeLayerResolution(appearance, "stand1", 0);
        var cats = items.Select(x => x.Category).Distinct().OrderBy(x => x).ToList();
        Console.WriteLine($"  部件总数: {items.Count}, 分类: [{string.Join(", ", cats)}]");
        bool hasBody = items.Any(x => x.Category == "body" || x.Category == "head" || x.Category == "hair" || x.Category == "face");
        bool hasMount = items.Any(x => x.Category == "mount");
        Console.WriteLine($"  含角色部件(body/head/hair/face): {hasBody}（期望 False=hideBody 生效）");
        Console.WriteLine($"  含坐骑部件: {hasMount}（期望 True）");
        Console.WriteLine(hasBody == false && hasMount ? "  hideBody 验证: PASS" : "  hideBody 验证: FAIL");
    }

    /// <summary>普通坐骑对照：ProbeLayerResolution 应同时含角色部件与坐骑部件。</summary>
    private static void VerifyNormalMount(WzService wz)
    {
        var cache = new CacheManager();
        var paperdoll = new PaperdollService(wz, cache, new SpriteService(cache));
        var appearance = new CharacterAppearance
        {
            Gender = 0,
            BodyId = 2000,
            Ear = PaperdollService.EarHuman,
            Hair = new ItemInfo { Id = "30000" },
            Face = new ItemInfo { Id = "20000" },
            Mount = new MountInfo { Id = "1902000" } // 01902000：无 characterAction
        };
        var items = paperdoll.ProbeLayerResolution(appearance, "stand1", 0);
        var cats = items.Select(x => x.Category).Distinct().OrderBy(x => x).ToList();
        Console.WriteLine($"  部件总数: {items.Count}, 分类: [{string.Join(", ", cats)}]");
        bool hasBody = items.Any(x => x.Category == "body" || x.Category == "head" || x.Category == "hair" || x.Category == "face");
        bool hasMount = items.Any(x => x.Category == "mount");
        Console.WriteLine($"  含角色部件: {hasBody}（期望 True）");
        Console.WriteLine($"  含坐骑部件: {hasMount}（期望 True）");
        Console.WriteLine(hasBody && hasMount ? "  普通坐骑对照: PASS" : "  普通坐骑对照: FAIL");
    }
}
