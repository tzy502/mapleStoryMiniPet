using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using WzComparerR2.WzLib;

/// <summary>
/// 变身椅 WZ 数据考证探针（3015632）：
///   --chairdiag [itemId]   ① MiniPet 已加载树探测椅子候选路径
///                          ② dump 已加载树 {id} 子树（info 字段 + effect 帧结构），effect 帧导出 out/chair_{id}_*.png
///                          ③ 直读 Data/Item/Special|Install 分片文件对照（验证分片加载）
/// </summary>
static class ChairDiag
{
    const string DataDir = "/Volumes/SSD/mxd/mxd/Data";

    public static void Run(MiniPet.Services.WzService wz, string outDir, string itemId)
    {
        Directory.CreateDirectory(outDir);
        Console.WriteLine($"===== [chairdiag] {itemId} =====");
        if (!int.TryParse(itemId, out var n) || n <= 0) { Console.WriteLine("id 非法"); return; }
        string node = n.ToString("D8");

        // ① MiniPet 已加载树里的候选路径（PaperdollService.ChairRoot 同款候选 + Special）
        Console.WriteLine("-- ① MiniPet WzService 已加载树探测 --");
        string[] candidates =
        {
            $"Item/Install/{n / 1000:D5}.img/{node}",
            $"Item/Install/{n / 100:D6}.img/{node}",
            $"Item/Install/{n / 10000:D4}.img/{node}",
            $"Item/Install/0301.img/{node}",
            $"Item/Cash/{n / 1000:D5}.img/{node}",
            $"Item/Cash/{n / 100:D6}.img/{node}",
            $"Item/Cash/{n / 10000:D4}.img/{node}",
            $"Item/Special/0301.img/{node}",
        };
        string hitPath = "";
        foreach (var c in candidates)
        {
            var found = wz.FindNodeByPath(c);
            Console.WriteLine($"  {(found == null ? "×" : "○")} {c}");
            if (found != null && hitPath.Length == 0) { hitPath = c.Substring(0, c.Length - node.Length - 1); }
        }

        // ② dump 已加载树中命中的椅子节点子树（info 字段 + effect 结构）——在直读分片**之前**跑，
        //    隔离 Wz_Structure 静态状态对懒加载 outlink 解析的干扰
        Console.WriteLine($"-- ② dump 已加载树 {hitPath}/{node} 子树 --");
        var hitNode = wz.FindNodeByPath($"{hitPath}/{node}");
        if (hitNode == null)
        {
            Console.WriteLine("  × 未命中");
        }
        else
        {
            Dump(hitNode, 0, 6);
            ExportEffects(wz, hitPath, node, outDir);
            var icon = wz.ExtractPng($"{hitPath}/{node}/info/icon");
            if (icon != null)
            {
                var f = Path.Combine(outDir, $"chair_{itemId}_icon.png");
                File.WriteAllBytes(f, icon);
                Console.WriteLine($"      [png] info/icon → {f}");
            }
            else
            {
                Console.WriteLine("      [png] info/icon 解析失败");
            }
            Console.WriteLine($"      [name] Ins: {wz.GetItemName("Ins", itemId)} / Item.Install: {wz.GetItemName("Install", itemId)}");
        }

        // ③ 直读 Special.wz / Install 分片（文件模式，同 Mob.wz 加载法）——验证任务假设的「Special/Install 0301.img」
        Console.WriteLine("-- ③ 直读 Special / Install 分片文件对照 --");
        foreach (var (dir, fns) in new (string, string[])[] { ("Special", new[] { "Special.wz", "Special_000.wz" }), ("Install", new[] { "Install.wz", "Install_000.wz" }) })
        {
            foreach (var fn in fns)
            {
                var path = Path.Combine(Path.Combine(DataDir, "Item", dir), fn);
                if (!File.Exists(path)) { Console.WriteLine($"  × {fn} 不存在"); continue; }
                try
                {
                    Wz_Structure.DefaultAutoDetectExtFiles = true;
                    Wz_Structure.DefaultImgCheckDisabled = true;
                    var wzs = new Wz_Structure();
                    var root = new Wz_Node(fn);
                    wzs.LoadFile(path, root, false, false);
                    var tops = root.Nodes.Select(t => t.Text).ToList();
                    Console.WriteLine($"  {fn}: 顶层 {tops.Count} 项: {string.Join(", ", tops.Take(32))}{(tops.Count > 32 ? " …" : "")}");
                    var img301 = FindImg(root, "0301.img");
                    Console.WriteLine($"    0301.img: {(img301 == null ? "× 无" : "○ 有 @ " + img301.FullPathToFile.Replace(path, fn))}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  × {fn} 加载失败: {ex.Message}");
                }
            }
        }
    }

    static Wz_Node FindImg(Wz_Node root, string imgName)
    {
        if (root.Text == imgName) return root;
        foreach (var c in root.Nodes)
        {
            var r = FindImg(c, imgName);
            if (r != null) return r;
        }
        return null;
    }

    static string Describe(Wz_Node n)
    {
        var v = n.Value;
        switch (v)
        {
            case null: return "";
            case Wz_Png png: return $"PNG {png.Width}x{png.Height}";
            case Wz_Vector vec: return $"({vec.X},{vec.Y})";
            case Wz_Uol uol: return $"UOL {uol.Uol}";
            case Wz_Image img: return $"IMG(链接)";
            default: return v.ToString() ?? "";
        }
    }

    static void Dump(Wz_Node node, int depth, int maxDepth)
    {
        var val = Describe(node);
        Console.WriteLine($"{new string(' ', depth * 2)}{node.Text}{(val.Length > 0 ? " = " + val : "")}");
        if (depth >= maxDepth) { if (node.Nodes.Count > 0) { Console.WriteLine($"{new string(' ', (depth + 1) * 2)}… ({node.Nodes.Count} 子节点省略)"); } return; }
        foreach (var c in node.Nodes) { Dump(c, depth + 1, maxDepth); }
    }

    static void ExportEffects(MiniPet.Services.WzService wz, string hitPath, string itemId, string outDir)
    {
        foreach (var effName in new[] { "effect", "effect1", "effect2", "effect3" })
        {
            var frameCount = wz.GetFrameCount($"{hitPath}/{itemId}/{effName}");
            if (frameCount <= 0) { continue; }
            for (int i = 0; i < frameCount; i++)
            {
                var path = $"{hitPath}/{itemId}/{effName}/{i}";
                var raw = wz.ExtractPng(path);
                if (raw == null) { Console.WriteLine($"      [png] {effName}/{i} 解析失败"); continue; }
                var f = Path.Combine(outDir, $"chair_{itemId}_{effName}_{i}.png");
                File.WriteAllBytes(f, raw);
                var size = wz.GetPngSize(path);
                Console.WriteLine($"      [png] {effName}/{i} {size?.W}x{size?.H} origin=({wz.GetOrigin(path).x},{wz.GetOrigin(path).y}) z={wz.GetZField(path) ?? "-"} → {f}");
            }
        }
    }
}
