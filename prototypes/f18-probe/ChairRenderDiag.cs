using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using MiniPet.Models;
using MiniPet.Services;
using WzComparerR2.WzLib;

/// <summary>
/// 椅子渲染复现探针：
///   --chairrender [itemId]   ① 扫 String/Ins.img 找名字含「花田/魔法椅子」的道具 id（考证同 id 不同道具）
///                            ② PaperdollService 真实渲染 chair={itemId} 的 stand/walk/sit 帧 → out/chairrender_{id}_*.png
///                            ③ 打印渲染决策链（ChairRoot/坐姿动作/特效层数）便于对照修复
/// </summary>
static class ChairRenderDiag
{
    public static void Run(WzService wz, string outDir, string itemId)
    {
        Directory.CreateDirectory(outDir);
        Console.WriteLine($"===== [chairrender] {itemId} =====");

        // ① 扫 String/Ins.img 名字表：找「花田」「魔法椅子」相关 id
        Console.WriteLine("-- ① String/Ins.img 名字扫描（花田/魔法椅） --");
        try
        {
            var insImg = wz.FindNodeByPath("String/Ins.img");
            if (insImg != null)
            {
                foreach (var child in insImg.Nodes)
                {
                    var nameNode = child.Nodes["name"];
                    var nm = nameNode?.GetValueEx<string>(null);
                    if (string.IsNullOrEmpty(nm)) { continue; }
                    if (nm.Contains("花田") || nm.Contains("魔法椅子") || nm.Contains("雪孩子") || nm.Contains("沙漏"))
                    {
                        Console.WriteLine($"  {child.Text} = {nm}");
                    }
                }
            }
            else
            {
                Console.WriteLine("  × String/Ins.img 未加载");
            }
        }
        catch (Exception ex) { Console.WriteLine($"  × 扫描失败: {ex.Message}"); }

        // ② 真实渲染复现
        Console.WriteLine($"-- ② PaperdollService 渲染 chair={itemId} --");
        var cache = new CacheManager();
        var sprite = new SpriteService(cache);
        var pd = new PaperdollService(wz, cache, sprite);
        pd.SetAppearance(new CharacterAppearance
        {
            Gender = 0,
            BodyId = 2000,
            Hair = new ItemInfo { Id = "30000" },
            Face = new ItemInfo { Id = "20000" },
            Weapon = new ItemInfo { Id = "1342000" },   // 持杖（对齐 web 主角持杖）
            Chair = new ChairInfo { Id = itemId },
        });

        foreach (var (act, fr) in new[] { ("stand1", 0), ("stand1", 1), ("walk1", 0), ("sit", 0) })
        {
            var (bmp, ox, oy, w, h) = pd.RenderFrame(act, fr);
            if (bmp == null) { Console.WriteLine($"  [render] {act}/{fr} → null"); continue; }
            var f = Path.Combine(outDir, $"chairrender_{itemId}_{act}_{fr}.png");
            using (var img = SKImage.FromBitmap(bmp))
            using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
            using (var fs = File.OpenWrite(f)) { fs.SetLength(0); data.SaveTo(fs); }
            Console.WriteLine($"  [render] {act}/{fr} → {bmp.Width}x{bmp.Height} origin=({ox},{oy}) bounds={w}x{h} → {f}");
            bmp.Dispose();
        }

        // ②.5 对照：无椅子 sit（验证 body sit 坐姿图形本身）vs 带椅子 stand1
        var pd2 = new PaperdollService(wz, cache, sprite);
        pd2.SetAppearance(new CharacterAppearance
        {
            Gender = 0,
            BodyId = 2000,
            Hair = new ItemInfo { Id = "30000" },
            Face = new ItemInfo { Id = "20000" },
        });
        {
            var (bmp, ox, oy, w, h) = pd2.RenderFrame("sit", 0);
            if (bmp != null)
            {
                var f = Path.Combine(outDir, "chairrender_nochair_sit_0.png");
                using (var img = SKImage.FromBitmap(bmp))
                using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
                using (var fs = File.OpenWrite(f)) { fs.SetLength(0); data.SaveTo(fs); }
                Console.WriteLine($"  [render] 无椅子 sit/0 → {bmp.Width}x{bmp.Height} origin=({ox},{oy}) → {f}");
                bmp.Dispose();
            }
            else
            {
                Console.WriteLine("  [render] 无椅子 sit/0 → null");
            }
        }

        // ②.6 3x 放大对照（sit 姿势肉眼确认）
        foreach (var name in new[] { $"chairrender_{itemId}_sit_0", $"chairrender_{itemId}_stand1_0" })
        {
            var src = Path.Combine(outDir, $"{name}.png");
            if (!File.Exists(src)) { continue; }
            using var bmp = SKBitmap.Decode(src);
            using var zoom = new SKBitmap(bmp.Width * 3, bmp.Height * 3);
            using (var cv = new SKCanvas(zoom))
            {
                cv.Clear(SKColors.Transparent);
                var paint = new SKPaint { IsAntialias = false, FilterQuality = SKFilterQuality.None };
                cv.DrawBitmap(bmp, new SKRect(0, 0, zoom.Width, zoom.Height), paint);
            }
            using (var img = SKImage.FromBitmap(zoom))
            using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
            using (var fs = File.OpenWrite(Path.Combine(outDir, $"{name}_3x.png"))) { fs.SetLength(0); data.SaveTo(fs); }
            Console.WriteLine($"  [zoom] {name}_3x.png");
        }

        // ③ 渲染决策链（复用 CompositeFrame 已走 CollectPiecesForFrame；这里直接打关键 WZ 读取）
        Console.WriteLine("-- ③ 决策链字段 --");
        Console.WriteLine($"  body sit 帧数 = {wz.GetFrameCount("Character/00002000.img/sit")}（stand1={wz.GetFrameCount("Character/00002000.img/stand1")}）");
        int n = int.TryParse(itemId, out var nv) ? nv : 0;
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
            if (wz.FindNodeByPath(c) != null)
            {
                Console.WriteLine($"  ChairRoot 命中: {c}");
                Console.WriteLine($"  info/sitAction = {wz.GetStringProperty($"{c}/info/sitAction") ?? "(无)"}");
                Console.WriteLine($"  info/position  = {(wz.FindNodeByPath($"{c}/info/position") != null ? "有" : "无")}");
                Console.WriteLine($"  info/removeBody = {(wz.FindNodeByPath($"{c}/info/removeBody") != null ? "有" : "无")}");
                Console.WriteLine($"  info/tamingMob = {(wz.FindNodeByPath($"{c}/info/tamingMob") != null ? "有" : "无")}");
                foreach (var eff in new[] { "effect", "effect2", "effect3" })
                {
                    var fc = wz.GetFrameCount($"{c}/{eff}");
                    if (fc > 0)
                    {
                        Console.WriteLine($"  {eff}: {fc} 帧 pos={wz.GetIntProperty($"{c}/{eff}/pos")} z={wz.GetIntProperty($"{c}/{eff}/z")}");
                    }
                }
                break;
            }
            }

        // ── ④ 帧序列实测（胶水 2026-09-16「0,0,1,1,2,2 不流畅」）：另起 walkmode 采样，此处占位 ──
        Console.WriteLine("-- ④ 帧序列采样见 --chairseq --");
    }
}

