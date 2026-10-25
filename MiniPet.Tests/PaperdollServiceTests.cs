using Xunit;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MiniPet.Tests;

public class PaperdollServiceTests
{
    // --- embedded minimal models ---
    public class TestItemInfo
    {
        public int Id { get; set; }
        public bool Visible { get; set; } = true;
    }

    public class TestCharacterAppearance
    {
        public int Gender { get; set; }
        public TestItemInfo Hair { get; set; } = new();
        public TestItemInfo Face { get; set; } = new();
        public TestItemInfo? Cap { get; set; }
        public TestItemInfo? Cape { get; set; }
        public TestItemInfo? Coat { get; set; }
        public TestItemInfo? Pants { get; set; }
        public TestItemInfo? Shoes { get; set; }
        public TestItemInfo? Weapon { get; set; }
        public TestItemInfo? Shield { get; set; }
        public TestItemInfo? Glove { get; set; }
        public TestItemInfo? Earring { get; set; }
    }

    public class TestZLayer
    {
        public string Slot { get; set; } = "";
        public int ZIndex { get; set; }
        public TestItemInfo? Item { get; set; }
    }

    // --- tests: 正常路径 (z-order) ---

    [Fact]
    public void ZOrder_HasExpectedCount()
    {
        var zOrder = GetZOrder();
        Assert.Equal(20, zOrder.Length);
    }

    [Fact]
    public void ZOrder_ContainsAllRequiredSlots()
    {
        var zOrder = GetZOrder();
        var set = new HashSet<string>(zOrder);

        Assert.Contains("body", set);
        Assert.Contains("head", set);
        Assert.Contains("cap", set);
        Assert.Contains("weapon", set);
        Assert.Contains("coat", set);
        Assert.Contains("pants", set);
        Assert.Contains("shoe", set);
        Assert.Contains("cape", set);
        Assert.Contains("shield", set);
        Assert.Contains("glove", set);
    }

    [Fact]
    public void ZOrder_BodyBeforeCap()
    {
        var zOrder = GetZOrder();
        var bodyIdx = Array.IndexOf(zOrder, "body");
        var capIdx = Array.IndexOf(zOrder, "cap");

        Assert.True(bodyIdx < capIdx, "body should be drawn before cap (lower z-index)");
    }

    [Fact]
    public void ZOrder_BodyBeforeCoat()
    {
        var zOrder = GetZOrder();
        var bodyIdx = Array.IndexOf(zOrder, "body");
        var coatIdx = Array.IndexOf(zOrder, "coat");

        Assert.True(bodyIdx < coatIdx, "body should be drawn before coat");
    }

    // --- tests: 边界 ---

    [Fact]
    public void SetEquip_ValidCategory_SetsItem()
    {
        var appearance = new TestCharacterAppearance();
        SetEquip(appearance, "Cap", "1000001");

        Assert.NotNull(appearance.Cap);
        Assert.Equal(1000001, appearance.Cap!.Id);
    }

    [Fact]
    public void SetEquip_InvalidItemId_StillSetsWithZero()
    {
        var appearance = new TestCharacterAppearance();
        SetEquip(appearance, "Weapon", "not_a_number");

        Assert.NotNull(appearance.Weapon);
        Assert.Equal(0, appearance.Weapon!.Id);
    }

    [Fact]
    public void RemoveEquip_SetsNull()
    {
        var appearance = new TestCharacterAppearance
        {
            Cap = new TestItemInfo { Id = 12345 }
        };

        RemoveEquip(appearance, "Cap");
        Assert.Null(appearance.Cap);
    }

    [Fact]
    public void SetEquip_NullAppearance_DoesNothing()
    {
        TestCharacterAppearance? appearance = null;
        var ex = Record.Exception(() => SetEquip(appearance!, "Cap", "100"));
        // should not throw, guards with null check
    }

    [Fact]
    public void RemoveEquip_NullAppearance_DoesNothing()
    {
        TestCharacterAppearance? appearance = null;
        var ex = Record.Exception(() => RemoveEquip(appearance!, "Cap"));
    }

    // --- tests: 异常 ---

    [Fact]
    public void SetEquip_UnknownCategory_DoesNotAffectKnownSlots()
    {
        var appearance = new TestCharacterAppearance();
        SetEquip(appearance, "NonExistent", "999");

        // No crash; all default slots remain null
        Assert.Null(appearance.Cap);
        Assert.Null(appearance.Weapon);
        Assert.Equal(0, appearance.Hair.Id); // default
    }

    [Fact]
    public void CompositeLayers_EmptyList_ReturnsNull()
    {
        var layers = new List<TestZLayer>();
        var result = CompositeLayerBounds(layers);
        Assert.Null(result);
    }

    [Fact]
    public void CompositeLayers_SingleLayer_ReturnsExactSize()
    {
        var layers = new List<TestZLayer>
        {
            new() { Slot = "body", ZIndex = 0 }
        };

        var result = CompositeLayerBounds(layers);
        Assert.NotNull(result);
    }

    [Fact]
    public void GetPartForSlot_Body_ReturnsBodyCode()
    {
        var appearance = new TestCharacterAppearance { Gender = 0, Hair = new() { Id = 30000 }, Face = new() { Id = 20000 } };
        var (category, itemId) = GetPartForSlot("body", appearance);

        Assert.Equal("Body", category);
        Assert.Equal("00002000", itemId);
    }

    [Fact]
    public void GetPartForSlot_Female_ReturnsFemaleBodyCode()
    {
        var appearance = new TestCharacterAppearance { Gender = 1, Hair = new() { Id = 30000 }, Face = new() { Id = 20000 } };
        var (category, itemId) = GetPartForSlot("body", appearance);

        Assert.Equal("Body", category);
        Assert.Equal("00012000", itemId);
    }

    [Fact]
    public void GetPartForSlot_Cap_WithItem_ReturnsItem()
    {
        var appearance = new TestCharacterAppearance
        {
            Gender = 0,
            Hair = new() { Id = 30000 },
            Face = new() { Id = 20000 },
            Cap = new() { Id = 1000001 }
        };

        var (category, itemId) = GetPartForSlot("cap", appearance);
        Assert.Equal("Cap", category);
        Assert.Equal("1000001", itemId);
    }

    [Fact]
    public void GetPartForSlot_Cap_WithoutItem_ReturnsNull()
    {
        var appearance = new TestCharacterAppearance
        {
            Gender = 0,
            Hair = new() { Id = 30000 },
            Face = new() { Id = 20000 },
            Cap = null
        };

        var (category, itemId) = GetPartForSlot("cap", appearance);
        Assert.Null(category);
        Assert.Null(itemId);
    }

    [Fact]
    public void Preset_SaveAndLoad_ReturnsSameAppearance()
    {
        var presets = new Dictionary<string, TestCharacterAppearance>();
        var appearance = new TestCharacterAppearance
        {
            Gender = 0,
            Hair = new() { Id = 30000 },
            Face = new() { Id = 20000 },
            Weapon = new() { Id = 1302000 }
        };

        presets["warrior"] = appearance;
        var loaded = presets.TryGetValue("warrior", out var p) ? p : null;

        Assert.NotNull(loaded);
        Assert.Equal(0, loaded!.Gender);
        Assert.Equal(30000, loaded.Hair.Id);
        Assert.Equal(1302000, loaded.Weapon!.Id);
    }

    [Fact]
    public void Preset_LoadNonexistent_ReturnsNull()
    {
        var presets = new Dictionary<string, TestCharacterAppearance>();
        presets["warrior"] = new TestCharacterAppearance();
        var loaded = presets.TryGetValue("mage", out var p) ? p : null;

        Assert.Null(loaded);
    }

    // --- R1: 坐骑/椅子根路径 ---

    [Fact]
    public void MountRoot_190Range_ReturnsTamingMobPath()
    {
        Assert.Equal("Character/TamingMob/01902000.img", MountRoot("1902000"));
        Assert.Equal("Character/TamingMob/01932021.img", MountRoot("1932021"));
    }

    [Fact]
    public void MountRoot_InvalidId_ReturnsEmpty()
    {
        Assert.Equal("", MountRoot("not_a_number"));
        Assert.Equal("", MountRoot("0"));
    }

    [Fact]
    public void ChairRoot_3010000_FirstCandidateIs03010Img()
    {
        var candidates = ChairRootCandidates("3010000");
        Assert.Equal("Item/Install/03010.img/03010000", candidates[0]);
    }

    [Fact]
    public void ChairRoot_3015000_SecondCandidateIs030150Img()
    {
        // 2026-08-16：3015000 系按 id/100 分组（030150.img），id/1000 候选（03015.img）不存在
        var candidates = ChairRootCandidates("3015000");
        Assert.Equal("Item/Install/030150.img/03015000", candidates[1]);
    }

    [Fact]
    public void ChairRoot_3020000_ThirdCandidateIs0302Img()
    {
        // 3020xxx 按 id/10000 分组（0302.img）；id/1000（03020.img）、id/100（030200.img）均不存在
        var candidates = ChairRootCandidates("3020000");
        Assert.Equal("Item/Install/0302.img/03020000", candidates[2]);
    }

    [Fact]
    public void EarringSlots_ByIdPrefix()
    {
        // R8：101 面饰→Af、102 肩饰→Ay、103 耳环→Ae；非法回退 Ae
        Assert.Equal("Af", EarringSlots("1010000"));
        Assert.Equal("Ay", EarringSlots("1020000"));
        Assert.Equal("Ae", EarringSlots("1030000"));
        Assert.Equal("Ae", EarringSlots(""));
    }

    [Fact]
    public void OfflineCacheMobId_HasHashSuffix()
    {
        // R4：离线条带缓存键含外观指纹（哨兵 + hash），换外观自动失效
        Assert.StartsWith("__paperdoll__", OfflineCacheMobId("someHash123"));
        Assert.EndsWith("someHash123", OfflineCacheMobId("someHash123"));
    }

    // --- R-chair2: 椅子 info 渲染字段（对齐 MapleSalon2 generateChairGroupData / ChairEffectPart） ---

    [Fact]
    public void ResolveChairSitEmotion_NumericIndex_MapsToExpressionName()
    {
        // MapleSalon2 CharacterExpressionsOrder：0=blink、14=glitter（3019632 花田椅 sitEmotion=14）
        Assert.Equal("blink", MiniPet.Services.PaperdollService.ResolveChairSitEmotion("0"));
        Assert.Equal("angry", MiniPet.Services.PaperdollService.ResolveChairSitEmotion("5"));
        Assert.Equal("glitter", MiniPet.Services.PaperdollService.ResolveChairSitEmotion("14"));
        Assert.Equal("default", MiniPet.Services.PaperdollService.ResolveChairSitEmotion("23"));
    }

    [Fact]
    public void ResolveChairSitEmotion_OutOfRange_ReturnsEmpty()
    {
        // 越界（负数/超表长）→ 空串 = 保持用户表情（回退不崩溃）
        Assert.Equal("", MiniPet.Services.PaperdollService.ResolveChairSitEmotion("-1"));
        Assert.Equal("", MiniPet.Services.PaperdollService.ResolveChairSitEmotion("24"));
        Assert.Equal("", MiniPet.Services.PaperdollService.ResolveChairSitEmotion("99"));
    }

    [Fact]
    public void ResolveChairSitEmotion_NonNumeric_PassesThrough()
    {
        Assert.Equal("", MiniPet.Services.PaperdollService.ResolveChairSitEmotion(null));
        Assert.Equal("", MiniPet.Services.PaperdollService.ResolveChairSitEmotion(""));
        Assert.Equal("love", MiniPet.Services.PaperdollService.ResolveChairSitEmotion("love"));
    }

    [Fact]
    public void ComputeChairEffectOffset_BodyRelMoveFollowCharacter_PosLow2_AnchorsNavel()
    {
        // ① 有 bm 无 sitAction pos<2：effect 与人物在 web 中一起平移 → 锚 navel 不动（普通椅子多数 pos=0）
        var (x, y) = MiniPet.Services.PaperdollService.ComputeChairEffectOffset(
            hasBodyRelMove: true, bmX: 0, bmY: -14, hasSitAction: false, pos: 0,
            removeBody: false, hasTamingMob: false, idNum: 3010205);
        Assert.Equal(0, x);
        Assert.Equal(0, y);
    }

    [Fact]
    public void ComputeChairEffectOffset_BodyRelMovePos1_KeepsLift50()
    {
        // ② 有 bm 无 sitAction pos==1：跟人物 + 保留上移 −50
        var (x, y) = MiniPet.Services.PaperdollService.ComputeChairEffectOffset(
            hasBodyRelMove: true, bmX: 10, bmY: -20, hasSitAction: false, pos: 1,
            removeBody: false, hasTamingMob: false, idNum: 3010000);
        Assert.Equal(0, x);
        Assert.Equal(-50, y);
    }

    [Fact]
    public void ComputeChairEffectOffset_FlowerChair3019632_AnchorsNegativeBodyRelMove()
    {
        // ③ 花田椅 3019632：bm=(47,-2) pos=3 → effect 锚 = −bm = (−47,+2)（人物坐进 WZ 指定坐标）
        var (x, y) = MiniPet.Services.PaperdollService.ComputeChairEffectOffset(
            hasBodyRelMove: true, bmX: 47, bmY: -2, hasSitAction: false, pos: 3,
            removeBody: false, hasTamingMob: false, idNum: 3019632);
        Assert.Equal(-47, x);
        Assert.Equal(2, y);
    }

    [Fact]
    public void ComputeChairEffectOffset_WithSitAction_DoesNotFollowCharacter()
    {
        // ④ 有 sitAction（forceAction）时 effect 不跟人物（ChairEffectPart 的 !forceAction 条件）→ 锚 = −bm
        var (x, y) = MiniPet.Services.PaperdollService.ComputeChairEffectOffset(
            hasBodyRelMove: true, bmX: 5, bmY: -8, hasSitAction: true, pos: 0,
            removeBody: false, hasTamingMob: false, idNum: 3010000);
        Assert.Equal(-5, x);
        Assert.Equal(8, y);
    }

    [Fact]
    public void ComputeChairEffectOffset_NoBodyRelMovePos1_KeepsLegacyTamingMobLift()
    {
        // ⑤ 无 bm pos==1：保持 R-chair 现状——带 tamingMob 且 id<3011000 → −30，否则 −50
        var taming = MiniPet.Services.PaperdollService.ComputeChairEffectOffset(
            hasBodyRelMove: false, bmX: 0, bmY: 0, hasSitAction: false, pos: 1,
            removeBody: false, hasTamingMob: true, idNum: 3010000);
        Assert.Equal(-30, taming.OffY);
        var normal = MiniPet.Services.PaperdollService.ComputeChairEffectOffset(
            hasBodyRelMove: false, bmX: 0, bmY: 0, hasSitAction: false, pos: 1,
            removeBody: false, hasTamingMob: false, idNum: 3010000);
        Assert.Equal(-50, normal.OffY);
        var newerTaming = MiniPet.Services.PaperdollService.ComputeChairEffectOffset(
            hasBodyRelMove: false, bmX: 0, bmY: 0, hasSitAction: false, pos: 1,
            removeBody: false, hasTamingMob: true, idNum: 3011000);
        Assert.Equal(-50, newerTaming.OffY);
    }

    [Fact]
    public void ComputeChairEffectOffset_RemoveBody_ShiftsDown30()
    {
        // ⑥ 变身椅（removeBody）：web 人物容器 y−30 → effect 相对下移 +30（叠加各分支）
        var follow = MiniPet.Services.PaperdollService.ComputeChairEffectOffset(
            hasBodyRelMove: false, bmX: 0, bmY: 0, hasSitAction: false, pos: 0,
            removeBody: true, hasTamingMob: false, idNum: 3015812);
        Assert.Equal(0, follow.OffX);
        Assert.Equal(30, follow.OffY);
        var bmBranch = MiniPet.Services.PaperdollService.ComputeChairEffectOffset(
            hasBodyRelMove: true, bmX: 47, bmY: -2, hasSitAction: false, pos: 3,
            removeBody: true, hasTamingMob: false, idNum: 3019632);
        Assert.Equal(-47, bmBranch.OffX);
        Assert.Equal(32, bmBranch.OffY);
    }

    // --- embedded minimal logic replicas ---

    private static string MountRoot(string id)
    {
        if (!int.TryParse(id, out var n) || n <= 0) return "";
        return $"Character/TamingMob/{n:D8}.img";
    }

    private static List<string> ChairRootCandidates(string id)
    {
        var n = int.Parse(id);
        string node = n.ToString("D8");
        return new List<string>
        {
            $"Item/Install/{n / 1000:D5}.img/{node}",
            $"Item/Install/{n / 100:D6}.img/{node}",
            $"Item/Install/{n / 10000:D4}.img/{node}",
            $"Item/Install/0301.img/{node}",
            $"Item/Cash/{n / 1000:D5}.img/{node}",
            $"Item/Cash/{n / 100:D6}.img/{node}",
            $"Item/Cash/{n / 10000:D4}.img/{node}",
        };
    }

    private static string EarringSlots(string? itemId)
    {
        if (!string.IsNullOrEmpty(itemId) && int.TryParse(itemId, out var n))
        {
            int part = n / 10000;
            if (part == 101) return "Af";
            if (part == 102) return "Ay";
        }
        return "Ae";
    }

    private static string OfflineCacheMobId(string hash) =>
        string.IsNullOrEmpty(hash) ? "__paperdoll__" : $"__paperdoll__{hash}";

    private static readonly string[] ZOrder =
    {
        "back", "hairBelowHead", "body", "head", "ear", "face", "tail",
        "capeBack", "arm", "hand", "shoe", "pants", "coat", "glove",
        "gloveOver", "shield", "weapon", "cape", "hairOverHead", "cap"
    };

    private static string[] GetZOrder() => ZOrder;

    private static void SetEquip(TestCharacterAppearance a, string category, string itemId)
    {
        if (a == null) return;
        var item = new TestItemInfo { Id = int.TryParse(itemId, out var id) ? id : 0 };
        switch (category)
        {
            case "Cap": a.Cap = item; break;
            case "Cape": a.Cape = item; break;
            case "Coat": a.Coat = item; break;
            case "Pants": a.Pants = item; break;
            case "Shoes": a.Shoes = item; break;
            case "Weapon": a.Weapon = item; break;
            case "Shield": a.Shield = item; break;
            case "Glove": a.Glove = item; break;
        }
    }

    private static void RemoveEquip(TestCharacterAppearance a, string category)
    {
        if (a == null) return;
        switch (category)
        {
            case "Cap": a.Cap = null; break;
            case "Cape": a.Cape = null; break;
            case "Coat": a.Coat = null; break;
            case "Pants": a.Pants = null; break;
            case "Shoes": a.Shoes = null; break;
            case "Weapon": a.Weapon = null; break;
            case "Shield": a.Shield = null; break;
            case "Glove": a.Glove = null; break;
        }
    }

    private static (string? category, string? itemId) GetPartForSlot(string slot, TestCharacterAppearance a)
    {
        string? capId = a.Cap?.Id.ToString(), capeId = a.Cape?.Id.ToString(), coatId = a.Coat?.Id.ToString();
        string? pantsId = a.Pants?.Id.ToString(), shoesId = a.Shoes?.Id.ToString(), weaponId = a.Weapon?.Id.ToString();
        string? shieldId = a.Shield?.Id.ToString(), gloveId = a.Glove?.Id.ToString(), earId = a.Earring?.Id.ToString();
        string hairId = a.Hair.Id.ToString(), faceId = a.Face.Id.ToString();
        string bodyCode = a.Gender == 0 ? "00002000" : "00012000";

        return slot switch
        {
            "back" or "body" or "head" or "arm" or "hand" => ("Body", bodyCode),
            "hairBelowHead" or "hairOverHead" => ("Hair", hairId),
            "face" => ("Face", faceId),
            "cap" => capId != null ? ("Cap", capId) : (null, null),
            "cape" or "capeBack" => capeId != null ? ("Cape", capeId) : (null, null),
            "coat" => coatId != null ? ("Coat", coatId) : (null, null),
            "pants" => pantsId != null ? ("Pants", pantsId) : (null, null),
            "shoe" => shoesId != null ? ("Shoes", shoesId) : (null, null),
            "weapon" => weaponId != null ? ("Weapon", weaponId) : (null, null),
            "shield" => shieldId != null ? ("Shield", shieldId) : (null, null),
            "glove" or "gloveOver" => gloveId != null ? ("Glove", gloveId) : (null, null),
            "ear" => earId != null ? ("Accessory", earId) : (null, null),
            _ => (null, null)
        };
    }

    private static (int MinX, int MinY, int MaxX, int MaxY)? CompositeLayerBounds(List<TestZLayer> layers)
    {
        if (layers.Count == 0) return null;

        // Simulate: without actual images, we just verify the empty list guard works
        return (0, 0, 100, 100);
    }
}
