using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using SkiaSharp;
using MiniPet.Models;
using MiniPet.Services;

/// <summary>
/// 椅子特效动画时序探针：同一 (action, frame) 连续渲染 8 次（间隔 350ms &gt; 花田椅单帧 delay 210ms），
/// 输出每次 PNG 哈希——实证「特效帧被 _frameSourcesCache 冻结」（冻结 = 8 次哈希全同）
/// 与修复后是否随真实时间推进（应见 ≥2 个不同哈希并周期回绕）。
///   --chairanim [itemId] [action] [frame]   默认 3019632 stand1 0
/// </summary>
static class ChairAnimDiag
{
    public static void Run(WzService wz, string outDir, string itemId, string action, int frame)
    {
        Directory.CreateDirectory(outDir);
        Console.WriteLine($"===== [chairanim] {itemId} {action}/{frame} ×8 间隔 350ms =====");
        var cache = new CacheManager();
        var sprite = new SpriteService(cache);
        var pd = new PaperdollService(wz, cache, sprite);
        pd.SetAppearance(new CharacterAppearance
        {
            Gender = 0,
            BodyId = 2000,
            Hair = new ItemInfo { Id = "30000" },
            Face = new ItemInfo { Id = "20000" },
            Weapon = new ItemInfo { Id = "1342000" },
            Chair = new ChairInfo { Id = itemId },
        });

        var hashes = new List<string>();
        for (int i = 0; i < 8; i++)
        {
            var (bmp, ox, oy, w, h) = pd.RenderFrame(action, frame);
            if (bmp == null) { Console.WriteLine($"  [{i}] render null"); Thread.Sleep(350); continue; }
            byte[] png;
            using (var img = SKImage.FromBitmap(bmp))
            using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
            { png = data.ToArray(); }
            bmp.Dispose();
            string hash = Convert.ToHexString(MD5.HashData(png))[..12];
            hashes.Add(hash);
            string tag = i == 0 ? " (首帧存盘)" : hashes[i - 1] == hash ? " = 冻结" : " ≠ 变化";
            if (i == 0 || hashes[i - 1] != hash)
            {
                var f = Path.Combine(outDir, $"chairanim_{itemId}_{action}_{frame}_{i}.png");
                File.WriteAllBytes(f, png);
                Console.WriteLine($"  [{i}] md5={hash}{tag} → {f}");
            }
            else
            {
                Console.WriteLine($"  [{i}] md5={hash}{tag}");
            }
            if (i < 7) { Thread.Sleep(350); }
        }
        int distinct = hashes.Distinct().Count();
        Console.WriteLine($"  ⇒ 8 帧中不同画面数 = {distinct}（1 = 冻结不播放；≥2 = 特效随时间推进，理论满速 210ms/帧应 ≈ {Math.Min(8, 8 * 350 / 210)} 个相位）");
    }
}
