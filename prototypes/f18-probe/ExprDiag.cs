using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using MiniPet.Models;
using MiniPet.Services;

/// <summary>表情渲染验证：对同一外观逐个 PlayExpression 并渲染 stand1，横向拼接对照（PLAN-表情功能.md §7）。</summary>
static class ExprDiag
{
    public static void Run(WzService wz, string outDir)
    {
        var cache = new CacheManager();
        var sprite = new SpriteService(cache);
        var pd = new PaperdollService(wz, cache, sprite);
        pd.SetAppearance(new CharacterAppearance
        {
            Gender = 0, BodyId = 2000,
            Hair = new ItemInfo { Id = "30000" },
            Face = new ItemInfo { Id = "20000" },
            FaceAccessory = new ItemInfo { Id = "1012000" },   // 101 面饰（同构表情结构）
        });

        string[] keys = { "default", "smile", "love", "wink", "cheers", "cry", "angry", "blink" };
        var frames = new List<(string Key, SKBitmap? Bmp)>();
        foreach (var k in keys)
        {
            pd.PlayExpression(k, 0);   // 0 = 常驻（durationMs<=0）
            var (bmp, ox, oy, w, h) = pd.RenderFrame("stand1", 0);
            frames.Add((k, bmp));
            Console.WriteLine($"[expr] {k,-8} → {(bmp == null ? "null" : $"{bmp.Width}x{bmp.Height}")} cur={pd.CurrentExpression}");
        }
        pd.CancelExpression();
        Console.WriteLine($"[expr] Cancel 后 cur={pd.CurrentExpression}");

        int cellW = 90, cellH = 100, labelH = 26;
        using var sheet = new SKBitmap(cellW * frames.Count, cellH + labelH);
        using (var cv = new SKCanvas(sheet))
        {
            cv.Clear(new SKColor(40, 60, 90));
            using var label = new SKPaint { IsAntialias = true, TextSize = 15, Color = SKColors.White };
            for (int i = 0; i < frames.Count; i++)
            {
                int cx = i * cellW + cellW / 2;
                cv.DrawLine(i * cellW, 0, i * cellW, sheet.Height, new SKPaint { Color = new SKColor(90, 110, 140), StrokeWidth = 1 });
                if (frames[i].Bmp != null)
                {
                    var b = frames[i].Bmp!;
                    cv.DrawBitmap(b, cx - b.Width / 2f, cellH - b.Height);
                }
                cv.DrawText(frames[i].Key, i * cellW + 6, cellH + 18, label);
            }
        }
        string f = Path.Combine(outDir, "expr_check.png");
        using (var img = SKImage.FromBitmap(sheet))
        using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
        using (var fs = File.OpenWrite(f)) { fs.SetLength(0); data.SaveTo(fs); }
        Console.WriteLine($"  → expr_check.png ({sheet.Width}x{sheet.Height})");
    }
}
