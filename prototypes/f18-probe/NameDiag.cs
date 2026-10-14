using System;
using System.Linq;
using MiniPet.Services;
using WzComparerR2.WzLib;

/// <summary>短 id 地图名考证：GetMapName 端到端（含短 id 双查）+ ResolveDisplayName 兜底 + String/Map.img 原始值对照。</summary>
static class NameDiag
{
    public static void Run(WzService wz, string outDir)
    {
        // ① GetMapName 端到端（需等后台目录缓存构建完成）
        wz.EnsureMapCatalog();
        Console.WriteLine($"目录缓存就绪: {wz.WaitForMapCatalog(15000)}");
        foreach (var id in new[] { "10000", "1000000", "260000000", "000010000" })
        {
            Console.WriteLine($"[GetMapName] {id} → {wz.GetMapName(id)}");
            Console.WriteLine($"[Resolve]   {id} (存量=坏id) → {wz.ResolveDisplayName(id, id)}");
        }

        // ② String/Map.img 原始值对照
        var imgNode = wz.FindNodeByPath("String/Map.img");
        var img = imgNode?.GetValue<Wz_Image>();
        if (img == null || !img.TryExtract()) { Console.WriteLine("String/Map.img 无法解压"); return; }
        foreach (Wz_Node group in img.Node.Nodes)
        {
            foreach (Wz_Node idNode in group.Nodes)
            {
                if (idNode.Text != "10000" && idNode.Text != "1000000" && idNode.Text != "260000000") { continue; }
                var resolved = idNode.ResolveUol() ?? idNode;
                string Get(string k) => resolved.FindNodeByPath(k)?.GetValueEx<string>(null) ?? "<null>";
                Console.WriteLine($"[WZ {group.Text}/{idNode.Text}] mapName={Get("mapName")} streetName={Get("streetName")}");
            }
        }
    }
}
