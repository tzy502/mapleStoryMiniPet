using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using MiniPet.Models;
using MiniPet.Services;

/// <summary>
/// 纸娃娃修复对照图：同一外观（含 101 面饰 / 102 眼饰 / 103 耳环）× 多动作，
/// 横向拼接成一张图，用于核对「背面动作不画脸 / backDefault 背面粉 / handMove 锚点 / 层别名」。
/// 运行：--doll
/// </summary>
static class DollDiag
{
    public static void Run(WzService wz, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var cache = new CacheManager();
        var sprite = new SpriteService(cache);
        var pd = new PaperdollService(wz, cache, sprite);
        pd.SetAppearance(new CharacterAppearance
        {
            Gender = 0,
            BodyId = 2000,
            Hair = new ItemInfo { Id = "30000" },
            Face = new ItemInfo { Id = "20000" },
            FaceAccessory = new ItemInfo { Id = "1010000" },   // 101 面饰（表情驱动）
            EyeAccessory = new ItemInfo { Id = "1020000" },    // 102 眼饰（动作驱动）
            Earring = new ItemInfo { Id = "1030000" },         // 103 耳环（有 backDefault 背面粉）
        });

        // 每个动作取一帧（ladder/rope 是背面动作，face=0）
        var acts = new[] { "stand1", "walk1", "alert", "ladder", "rope", "prone", "jump" };
        var frames = new List<(string Act, SKBitmap? Bmp, int Ox, int Oy)>();
        foreach (var act in acts)
        {
            var (bmp, ox, oy, w, h) = pd.RenderFrame(act, 0);
            frames.Add((act, bmp, ox, oy));
            Console.WriteLine($"[doll] {act,-8} → {(bmp == null ? "null" : $"{bmp.Width}x{bmp.Height} origin=({ox},{oy})")}");
        }

        int cellW = 90, cellH = 100, labelH = 26;
        using var sheet = new SKBitmap(cellW * frames.Count, cellH + labelH);
        using (var cv = new SKCanvas(sheet))
        {
            cv.Clear(new SKColor(40, 60, 90));
            using var label = new SKPaint { IsAntialias = true, TextSize = 16, Color = SKColors.White };
            for (int i = 0; i < frames.Count; i++)
            {
                int cx = i * cellW + cellW / 2;
                cv.DrawLine(i * cellW, 0, i * cellW, sheet.Height, new SKPaint { Color = new SKColor(90, 110, 140), StrokeWidth = 1 });
                if (frames[i].Bmp != null)
                {
                    var b = frames[i].Bmp!;
                    // **按 origin 对齐**：origin 落在每个 cell 的 (cx, anchorY) —— 若各动作的锚点一致，
                    // 人物相对该点的位置也应一致（验证「纸娃娃按基础来」是否真的锚点不漂）。
                    float anchorY = cellH - 20;
                    cv.DrawBitmap(b, cx - frames[i].Ox, anchorY - frames[i].Oy);
                    cv.DrawCircle(cx, anchorY, 3, new SKPaint { Color = SKColors.Red, IsAntialias = true });
                }
                cv.DrawText(frames[i].Act, i * cellW + 6, cellH + 18, label);
            }
        }
        string f = Path.Combine(outDir, "doll_修复对照.png");
        using (var img = SKImage.FromBitmap(sheet))
        using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
        using (var fs = File.OpenWrite(f)) { fs.SetLength(0); data.SaveTo(fs); }
        Console.WriteLine($"  → doll_修复对照.png ({sheet.Width}x{sheet.Height})");
    }
}
