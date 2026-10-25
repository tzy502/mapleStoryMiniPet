using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using MiniPet.Services;
using WzComparerR2.WzLib;

/// <summary>
/// 椅子 info 字段全量扫描：本 WZ（Data/Item/Install）所有椅子（3010000-3020999）的
/// bodyRelMove/removeBody/sitAction/sitEmotion/tamingMob/group/lvChairInfo/customChair 分布，
/// 为渲染修复与测试断言提供数据面。运行：--chairscan
/// </summary>
static class ChairScanDiag
{
    public static void Run(WzService wz, string outDir)
    {
        Directory.CreateDirectory(outDir);
        Console.WriteLine("===== [chairscan] Item/Install 全椅子 info 字段分布 =====");

        int total = 0, withBodyRelMove = 0, withRemoveBody = 0, withSitAction = 0,
            withSitEmotion = 0, withTamingMob = 0, withGroup = 0, withLvChair = 0, withCustom = 0,
            withRemoveEffect = 0, withInvisibleWeapon = 0;
        var bodyRelMoveSamples = new List<string>();
        var removeBodyIds = new List<string>();
        var sitActionSamples = new List<string>();

        foreach (var (_, imgFile) in wz.GetDirectoryChildren("Item/Install"))
        {
            if (!imgFile.StartsWith("0301") && !imgFile.StartsWith("0302")) { continue; }
            foreach (var idRaw in wz.GetImgChildren($"Item/Install/{imgFile}"))
            {
                if (!int.TryParse(idRaw, out var chairId) || chairId < 3010000 || chairId >= 3021000) { continue; }
                string id = chairId.ToString("D8");   // WZ 节点名 8 位补零
                string root = $"Item/Install/{imgFile}/{id}";
                var infoNode = wz.FindNodeByPath($"{root}/info");
                if (infoNode == null)
                {
                    Console.WriteLine($"  [无info] {root}");
                    continue;
                }
                total++;
                string InfoHas(string f) => wz.FindNodeByPath($"{root}/info/{f}") != null ? "1" : "";
                if (InfoHas("bodyRelMove").Length > 0)
                {
                    withBodyRelMove++;
                    var vecNode = wz.FindNodeByPath($"{root}/info/bodyRelMove");
                    var vec = vecNode?.GetValueEx<WzComparerR2.WzLib.Wz_Vector>(null);
                    if (bodyRelMoveSamples.Count < 15 && vec != null)
                    {
                        bodyRelMoveSamples.Add($"{id}=({vec.X},{vec.Y}) pos={wz.GetIntProperty($"{root}/effect/pos")}");
                    }
                }
                if (InfoHas("removeBody").Length > 0) { withRemoveBody++; if (removeBodyIds.Count < 20) { removeBodyIds.Add(id); } }
                if (InfoHas("removeEffect").Length > 0) { withRemoveEffect++; }
                if (InfoHas("invisibleWeapon").Length > 0) { withInvisibleWeapon++; }
                var sa = wz.GetStringProperty($"{root}/info/sitAction");
                if (!string.IsNullOrEmpty(sa)) { withSitAction++; if (sitActionSamples.Count < 10) { sitActionSamples.Add($"{id}={sa}"); } }
                if (InfoHas("sitEmotion").Length > 0)
                {
                    withSitEmotion++;
                    if (id == "03019632")
                    {
                        var seNode = wz.FindNodeByPath($"{root}/info/sitEmotion");
                        Console.WriteLine($"  [3019632 sitEmotion 值] {seNode?.GetValueEx<object>(null)}");
                    }
                }
                if (InfoHas("tamingMob").Length > 0) { withTamingMob++; }
                if (InfoHas("group").Length > 0) { withGroup++; }
                if (InfoHas("lvChairInfo").Length > 0) { withLvChair++; }
                if (InfoHas("customChair").Length > 0) { withCustom++; }
            }
        }

        Console.WriteLine($"总椅子数={total}");
        Console.WriteLine($"bodyRelMove={withBodyRelMove} removeBody={withRemoveBody} sitAction={withSitAction} sitEmotion={withSitEmotion} tamingMob={withTamingMob}");
        Console.WriteLine($"group={withGroup} lvChairInfo={withLvChair} customChair={withCustom} removeEffect={withRemoveEffect} invisibleWeapon={withInvisibleWeapon}");
        Console.WriteLine("bodyRelMove 样本: " + string.Join(", ", bodyRelMoveSamples));
        Console.WriteLine("removeBody 样本: " + string.Join(", ", removeBodyIds));
        Console.WriteLine("sitAction 样本: " + string.Join(", ", sitActionSamples));
    }
}
