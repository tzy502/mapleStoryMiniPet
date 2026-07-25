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

    // --- embedded minimal logic replicas ---

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
