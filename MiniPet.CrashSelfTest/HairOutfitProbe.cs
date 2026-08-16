using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Models;
using MiniPet.Services;
using WzComparerR2.WzLib;

namespace MiniPet.CrashSelfTest;

/// <summary>
/// 「查看现在用的搭配」发型链路探针（StartupObject 模式，参考 CapePathProbe/HairProbe 写法）：
/// ① 真实数据：从 settings.json 读 CurrentPaperdoll（当前搭配）与 SavedPaperdolls（搭配存档）；
/// ② 往返完整性：CloneAppearance/CloneDraft 等价克隆 + ConfigService 同款 JSON 序列化/反序列化
///    → 发型字段（Id/Dye/Hue/Saturation/Brightness）是否完整；
/// ③ 合成一致性：直接 CompositeFrame vs 克隆后 vs JSON 往返后 —— 输出 PNG 字节是否一致；
///    并对照「无发型」合成 → 证明发型实际入图（非该显示不显示）；
/// ④ 发型部件管线：ProbeLayerResolution 落层/遮挡/染发路径；
/// ⑤ GetItemName("Hair", id) 发型名显示。
/// 用法：dotnet run --project MiniPet.CrashSelfTest -p:UsedAvaloniaProducts= -p:StartupObject=MiniPet.CrashSelfTest.HairOutfitProbe
/// </summary>
public static class HairOutfitProbe
{
    public static int Main(string[] args)
    {
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        var exitCode = 0;
        var cts = new CancellationTokenSource();
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
        Console.WriteLine($"WZ 路径: {basePath}");
        var (ok, _) = wz.LoadWz(wzConfig.WzLibPath, basePath);
        Console.WriteLine($"WZ 加载: {ok}");
        if (!ok) return;

        var cache = new CacheManager();
        var paperdoll = new PaperdollService(wz, cache, new SpriteService(cache));

        // ── ① 真实数据（settings.json）──
        var settings = config.LoadSettings();
        Console.WriteLine("\n========== ① 磁盘真实数据 ==========");
        Console.WriteLine($"CurrentMobId={settings.CurrentMobId} CurrentEntityType={settings.CurrentEntityType}");
        Console.WriteLine($"CurrentPaperdoll={(settings.CurrentPaperdoll != null ? $"有 hair={settings.CurrentPaperdoll.Hair?.Id} face={settings.CurrentPaperdoll.Face?.Id}" : "无")}");
        Console.WriteLine($"SavedPaperdolls 数量: {settings.SavedPaperdolls.Count} 名称: [{string.Join(", ", settings.SavedPaperdolls.Keys)}]");
        var current = settings.CurrentPaperdoll;
        if (current == null)
        {
            Console.WriteLine("无当前搭配，用构造样例继续");
            current = MakeSample();
        }

        // ── ② 往返完整性 ──
        Console.WriteLine("\n========== ② 发型字段往返完整性 ==========");
        DumpHair("原始 CurrentPaperdoll", current.Hair);
        var cloneA = CloneLikeSettings(current);   // 等价 SettingsWindow.CloneAppearance
        DumpHair("CloneAppearance 后", cloneA.Hair);
        var cloneD = CloneLikeBrowser(current);    // 等价 MaterialBrowserWindow.CloneDraft
        DumpHair("CloneDraft 后", cloneD.Hair);
        var jsonRound = JsonRoundTrip(current);
        DumpHair("JSON 序列化→反序列化后", jsonRound.Hair);

        // 全字段对照（发型 + 其余部位 id）
        CompareHair("CloneAppearance", current.Hair, cloneA.Hair);
        CompareHair("CloneDraft", current.Hair, cloneD.Hair);
        CompareHair("JSON 往返", current.Hair, jsonRound.Hair);

        // ── ③ 合成一致性 + 发型入图证明 ──
        Console.WriteLine("\n========== ③ CompositeFrame 合成一致性 + 发型入图 ==========");
        var pngDirect = paperdoll.CompositeFrame(current, "stand1", 0);
        Console.WriteLine($"直接合成: {(pngDirect != null ? $"{pngDirect.Length} 字节" : "无")}");
        var pngClone = paperdoll.CompositeFrame(cloneA, "stand1", 0);
        Console.WriteLine($"CloneAppearance 后合成: {(pngClone != null ? $"{pngClone.Length} 字节" : "无")} 字节一致={(pngDirect != null && pngClone != null && pngDirect.SequenceEqual(pngClone))}");
        var pngJson = paperdoll.CompositeFrame(jsonRound, "stand1", 0);
        Console.WriteLine($"JSON 往返后合成: {(pngJson != null ? $"{pngJson.Length} 字节" : "无")} 字节一致={(pngDirect != null && pngJson != null && pngDirect.SequenceEqual(pngJson))}");

        // 无发型对照：发型是否实际入图
        var noHair = CloneLikeSettings(current);
        noHair.Hair = new ItemInfo();
        var pngNoHair = paperdoll.CompositeFrame(noHair, "stand1", 0);
        bool hairInPng = pngDirect != null && pngNoHair != null && !pngDirect.SequenceEqual(pngNoHair);
        Console.WriteLine($"无发型合成: {(pngNoHair != null ? $"{pngNoHair.Length} 字节" : "无")} → 发型入图差异: {hairInPng}");

        // 染发对照：HSV 调整是否实际入图
        var dyed = CloneLikeSettings(current);
        dyed.Hair.Hue = 180; dyed.Hair.Saturation = 60; dyed.Hair.Brightness = 10;
        var pngDyed = paperdoll.CompositeFrame(dyed, "stand1", 0);
        bool dyeInPng = pngDirect != null && pngDyed != null && !pngDirect.SequenceEqual(pngDyed);
        Console.WriteLine($"染发(Hue=180 Sat=60 Bright=10)合成: {(pngDyed != null ? $"{pngDyed.Length} 字节" : "无")} → 染发入图差异: {dyeInPng}");

        // ── ④ 发型部件管线（落层/遮挡/收集）──
        Console.WriteLine("\n========== ④ 发型部件管线（stand1/0）==========");
        DumpHairPieces(paperdoll, current, "当前搭配");

        // 帽子遮挡回归：当前搭配 cap + hair 同时穿戴
        Console.WriteLine("\n--- 帽子遮挡回归（cap + hair 同穿）---");
        var capApp = CloneLikeSettings(current);
        capApp.Cap = new ItemInfo { Id = "1003953" };
        DumpHairPieces(paperdoll, capApp, "cap=1003953");

        // hairShade canvas 列表回归（已知修复：canvas 化后应锁定可见）
        Console.WriteLine("\n--- hairShade canvas 列表回归 ---");
        foreach (var hid in new[] { "30000", "36633" })
        {
            var a = MakeSample(hid);
            var items = paperdoll.ProbeLayerResolution(a, "stand1", 0);
            var shade = items.Where(x => x.Category == "hair" && x.PieceName.Equals("hairShade", StringComparison.OrdinalIgnoreCase)).ToList();
            Console.WriteLine($"  hair={hid}: hairShade 件 {shade.Count} 个, 可见 {shade.Count(x => x.Locked && !x.Hidden)} 个");
        }

        // 帽子 vslot 遮挡回归（2026-08-17 修复：vslot 从 WZ info 读取——cap 1003953 真实 CpH5
        // 不锁前发 H1 → hairOverHead 应可见；cap 1000000/1002149 真实 CpH1H5 锁 H1 → 前发应隐藏）
        Console.WriteLine("\n--- 帽子 vslot 遮挡回归 ---");
        foreach (var capId in new[] { "1003953", "1000000", "1002149" })
        {
            var a = CloneLikeSettings(current);
            a.Cap = new ItemInfo { Id = capId };
            var items = paperdoll.ProbeLayerResolution(a, "stand1", 0);
            var hair = items.Where(x => x.Category == "hair").ToList();
            var overHead = hair.FirstOrDefault(x => x.PieceName == "hairOverHead");
            var realVslot = wz.GetStringProperty($"Character/Cap/{capId.PadLeft(8, '0')}.img/info/vslot") ?? "";
            Console.WriteLine($"  cap={capId} 真实vslot=[{realVslot}] hair件={hair.Count} 可见={hair.Count(x => x.Locked && !x.Hidden)} hairOverHead隐藏={overHead?.Hidden}");
        }

        // smap 缺失层回归（2026-08-17 修复：smap 无该层条目时 layerOk 恒真，对齐 MapleSalon2
        // 空 requireLocks——hairBelowHead 部件 zmap 有层但 smap 无条目（smap 只有 backHairBelowHead=Hf），
        // 原回退 vslot 被帽子锁 H5 误伤 → 8 个发型（33550-33557/36440-36447）在帽下被错误隐藏）
        Console.WriteLine("\n--- smap 缺失层回归（hairBelowHead 系列在帽下应全可见）---");
        foreach (var hid in new[] { "33550", "33551", "33552", "33553", "33554", "33555", "33556", "33557", "36440", "36441", "36442", "36443", "36444", "36445", "36446", "36447" })
        {
            foreach (var capId in new[] { "1003953", "1000000" })
            {
                var a = MakeSample(hid);
                a.Face = new ItemInfo { Id = "20000" };
                a.Cap = new ItemInfo { Id = capId };
                var items = paperdoll.ProbeLayerResolution(a, "stand1", 0);
                var hair = items.Where(x => x.Category == "hair").ToList();
                var belowHead = hair.FirstOrDefault(x => x.PieceName == "hairBelowHead");
                if (belowHead == null || belowHead.Hidden)
                {
                    Console.WriteLine($"  hair={hid} cap={capId}: hairBelowHead {(belowHead == null ? "缺失" : "被隐藏!")}");
                }
            }
        }
        Console.WriteLine("  (无输出=全部 hairBelowHead 可见)");

        // ── ⑤ 搭配存档加载（SavedPaperdolls → LoadDraftOutfit 等价路径）──
        Console.WriteLine("\n========== ⑤ SavedPaperdolls 存档加载 ==========");
        foreach (var (name, app) in settings.SavedPaperdolls)
        {
            var loaded = CloneLikeBrowser(app); // 等价 LoadDraftOutfit 的 CloneDraft(app)
            Console.WriteLine($"  [{name}] hair={app.Hair?.Id} face={app.Face?.Id} body={app.BodyId}");
            DumpHair($"    {name} 存档原始", app.Hair);
            DumpHair($"    {name} CloneDraft 后", loaded.Hair);
            var png = paperdoll.CompositeFrame(loaded, "stand1", 0);
            var items = paperdoll.ProbeLayerResolution(loaded, "stand1", 0);
            var hairItems = items.Where(x => x.Category == "hair").ToList();
            Console.WriteLine($"    合成: {(png != null ? $"{png.Length} 字节" : "无")}, hair 件 {hairItems.Count} 个, 可见 {hairItems.Count(x => x.Locked && !x.Hidden)} 个");
        }

        // ── ⑥ 发型名显示 ──
        Console.WriteLine("\n========== ⑥ GetItemName(\"Hair\") 发型名 ==========");
        foreach (var hid in new[] { "36633", "30000", "31000", "32000" })
        {
            var cn = wz.GetItemName("Hair", hid);
            Console.WriteLine($"  Hair {hid} → [{cn}]");
        }
    }

    // ── 工具 ──

    private static CharacterAppearance MakeSample()
    {
        return new CharacterAppearance
        {
            Gender = 0,
            BodyId = 2000,
            Ear = PaperdollService.EarHuman,
            Hair = new ItemInfo { Id = "36633" },
            Face = new ItemInfo { Id = "20094" },
        };
    }

    private static CharacterAppearance MakeSample(string hairId)
    {
        return new CharacterAppearance
        {
            Gender = 0,
            BodyId = 2000,
            Ear = PaperdollService.EarHuman,
            Hair = new ItemInfo { Id = hairId },
        };
    }

    /// <summary>等价 SettingsWindow.CloneAppearance（含 EnableEffect 保留）。</summary>
    private static CharacterAppearance CloneLikeSettings(CharacterAppearance src)
    {
        return new CharacterAppearance
        {
            Gender = src.Gender, Skin = src.Skin, BodyId = src.BodyId, Ear = src.Ear,
            Hair = CloneItem(src.Hair), Face = CloneItem(src.Face),
            Cap = src.Cap != null ? CloneItem(src.Cap) : null,
            Cape = src.Cape != null ? CloneItem(src.Cape) : null,
            Coat = src.Coat != null ? CloneItem(src.Coat) : null,
            Overall = src.Overall != null ? CloneItem(src.Overall) : null,
            Pants = src.Pants != null ? CloneItem(src.Pants) : null,
            Shoes = src.Shoes != null ? CloneItem(src.Shoes) : null,
            Weapon = src.Weapon != null ? CloneItem(src.Weapon) : null,
            Shield = src.Shield != null ? CloneItem(src.Shield) : null,
            Glove = src.Glove != null ? CloneItem(src.Glove) : null,
            FaceAccessory = src.FaceAccessory != null ? CloneItem(src.FaceAccessory) : null,
            EyeAccessory = src.EyeAccessory != null ? CloneItem(src.EyeAccessory) : null,
            Earring = src.Earring != null ? CloneItem(src.Earring) : null,
            Mount = src.Mount != null ? new MountInfo { Id = src.Mount.Id } : null,
            Chair = src.Chair != null ? new ChairInfo { Id = src.Chair.Id } : null,
            EnableEffect = src.EnableEffect,
        };
    }

    /// <summary>等价 MaterialBrowserWindow.CloneDraft（不含 EnableEffect——原实现未克隆，保持等价以复现问题）。</summary>
    private static CharacterAppearance CloneLikeBrowser(CharacterAppearance src)
    {
        return new CharacterAppearance
        {
            Gender = src.Gender, Skin = src.Skin, BodyId = src.BodyId, Ear = src.Ear,
            Hair = CloneItem(src.Hair), Face = CloneItem(src.Face),
            Cap = CloneItem(src.Cap), Cape = CloneItem(src.Cape), Coat = CloneItem(src.Coat),
            Overall = CloneItem(src.Overall), Pants = CloneItem(src.Pants), Shoes = CloneItem(src.Shoes),
            Glove = CloneItem(src.Glove), Shield = CloneItem(src.Shield), Weapon = CloneItem(src.Weapon),
            FaceAccessory = CloneItem(src.FaceAccessory), EyeAccessory = CloneItem(src.EyeAccessory),
            Earring = CloneItem(src.Earring),
            Mount = src.Mount != null ? new MountInfo { Id = src.Mount.Id } : null,
            Chair = src.Chair != null ? new ChairInfo { Id = src.Chair.Id } : null,
        };
    }

    private static ItemInfo? CloneItem(ItemInfo? it)
    {
        if (it == null) return null;
        return new ItemInfo { Id = it.Id, Dye = it.Dye, Hue = it.Hue, Saturation = it.Saturation, Brightness = it.Brightness };
    }

    private static CharacterAppearance JsonRoundTrip(CharacterAppearance src)
    {
        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            Converters = { new LenientStringConverter() },
        };
        var json = JsonSerializer.Serialize(src, opts);
        var back = JsonSerializer.Deserialize<CharacterAppearance>(json, opts);
        return back ?? new CharacterAppearance();
    }

    private static void DumpHair(string label, ItemInfo? hair)
    {
        if (hair == null) { Console.WriteLine($"  {label}: hair=null"); return; }
        Console.WriteLine($"  {label}: Id={hair.Id} Dye={hair.Dye} Hue={hair.Hue} Sat={hair.Saturation} Bright={hair.Brightness}");
    }

    private static void CompareHair(string label, ItemInfo? a, ItemInfo? b)
    {
        bool same = (a == null && b == null)
            || (a != null && b != null && a.Id == b.Id && a.Dye == b.Dye && a.Hue == b.Hue
                && a.Saturation == b.Saturation && a.Brightness == b.Brightness);
        Console.WriteLine($"  发型字段一致 [{label}]: {same}");
    }

    private static void DumpHairPieces(PaperdollService paperdoll, CharacterAppearance a, string label)
    {
        var items = paperdoll.ProbeLayerResolution(a, "stand1", 0);
        var hairItems = items.Where(x => x.Category == "hair").ToList();
        Console.WriteLine($"  [{label}] hair 件 {hairItems.Count} 个, 可见 {hairItems.Count(x => x.Locked && !x.Hidden)} 个");
        foreach (var it in hairItems)
        {
            Console.WriteLine($"    piece={it.PieceName,-16} z={it.ZField,-14} → 层={it.ResolvedLayer,-16} idx={it.ZIndex,4} map={it.HasMap,5} locked={it.Locked,5} hidden={it.Hidden,5}");
        }
    }
}
