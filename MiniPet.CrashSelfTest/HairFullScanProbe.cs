using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Models;
using MiniPet.Services;
using WzComparerR2.WzLib;

namespace MiniPet.CrashSelfTest;

/// <summary>全量发型 × 3 帽子组合隐藏扫描：确认 hidden 完整分布（后台长跑）。</summary>
public static class HairFullScanProbe
{
    public static int Main(string[] args)
    {
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        var exitCode = 0;
        var cts = new CancellationTokenSource();
        Dispatcher.UIThread.Post(() =>
        {
            try { Run(); exitCode = 0; }
            catch (Exception ex) { Console.WriteLine($"probe error: {ex.Message}"); Console.WriteLine(ex.StackTrace); exitCode = 1; }
            cts.Cancel();
        });
        Dispatcher.UIThread.MainLoop(cts.Token);
        return exitCode;
    }

    private static void Run()
    {
        var config = new ConfigService();
        var wzConfig = config.LoadWzConfig();
        var wz = new WzService();
        string basePath = Environment.GetEnvironmentVariable("MINIPET_WZ_BASE");
        if (string.IsNullOrEmpty(basePath)) basePath = wzConfig.BaseWzPath;
        var (ok, _) = wz.LoadWz(wzConfig.WzLibPath, basePath);
        Console.WriteLine($"WZ 加载: {ok}");
        if (!ok) return;

        var cache = new CacheManager();
        var paperdoll = new PaperdollService(wz, cache, new SpriteService(cache));

        var hairDir = wz.FindNodeByPath("Character/Hair");
        var ids = new List<string>();
        foreach (Wz_Node c in hairDir.Nodes)
        {
            if (!c.Text.EndsWith(".img")) continue;
            var id = c.Text[..^4];
            if (id.Length >= 7 && id.StartsWith("0")) id = id.TrimStart('0');
            if (int.TryParse(id, out _)) ids.Add(id);
        }
        Console.WriteLine($"全量发型: {ids.Count}");

        var caps = new (string Label, string? Id)[]
        {
            ("无帽", null),
            ("cap1003953", "1003953"),
            ("cap1000000", "1000000"),
        };

        var hiddenByCombo = new Dictionary<string, Dictionary<string, int>>();
        int total = 0, hidden = 0;
        foreach (var hid in ids)
        {
            foreach (var (capLabel, capId) in caps)
            {
                var a = new CharacterAppearance
                {
                    Gender = 0,
                    BodyId = 2000,
                    Ear = PaperdollService.EarHuman,
                    Hair = new ItemInfo { Id = hid },
                    Face = new ItemInfo { Id = "20000" },
                    Cap = capId != null ? new ItemInfo { Id = capId } : null,
                };
                var items = paperdoll.ProbeLayerResolution(a, "stand1", 0);
                foreach (var it in items.Where(x => x.Category == "hair"))
                {
                    total++;
                    if (!it.Hidden) continue;
                    hidden++;
                    if (!hiddenByCombo.TryGetValue(capLabel, out var byPiece))
                    {
                        byPiece = new Dictionary<string, int>();
                        hiddenByCombo[capLabel] = byPiece;
                    }
                    byPiece.TryGetValue($"{it.PieceName}→{it.ResolvedLayer}", out var n);
                    byPiece[$"{it.PieceName}→{it.ResolvedLayer}"] = n + 1;
                }
            }
        }
        Console.WriteLine($"\nhair 部件总判定: {total}, hidden: {hidden}");
        foreach (var kv in hiddenByCombo)
        {
            Console.WriteLine($"  组合 [{kv.Key}]:");
            foreach (var p in kv.Value.OrderByDescending(x => x.Value))
            {
                Console.WriteLine($"    {p.Key}: {p.Value}");
            }
        }
    }
}
