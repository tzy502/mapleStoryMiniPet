using Xunit;
using System;
using System.Linq;
using MiniPet.Views;

namespace MiniPet.Tests;

/// <summary>
/// 素材浏览窗口纯逻辑单测：发型同名分组（E）、性别联动过滤（I2）、icon 根路径（D）。
/// 只测 static 纯函数，不实例化 Avalonia Window。
/// </summary>
public class MaterialBrowserLogicTests
{
    private static MaterialBrowserWindow.MaterialEntry Hair(string id, string name) =>
        new("发型", id, name, "💇");

    private static MaterialBrowserWindow.MaterialEntry Face(string id, string name) =>
        new("脸型", id, name, "😊");

    // ── E: 发型同名分组 ──

    [Fact]
    public void GroupHairByName_FoldsMultiVariantSameName()
    {
        var rows = MaterialBrowserWindow.GroupHairByName(new[]
        {
            Hair("30000", "短发"),
            Hair("30001", "短发"),
            Hair("30002", "短发"),
            Hair("31000", "长发"),
        });
        Assert.Single(rows);
        Assert.Equal("短发", rows[0].Name);
        Assert.Equal(3, rows[0].Variants.Count);
        // 变体按 id 排序
        Assert.Equal(new[] { "30000", "30001", "30002" }, rows[0].Variants.Select(v => v.Id).ToArray());
    }

    [Fact]
    public void GroupHairByName_SingleVariantStaysUngrouped()
    {
        var rows = MaterialBrowserWindow.GroupHairByName(new[]
        {
            Hair("30000", "独有发型"),
            Hair("31000", "另一个独有发型"),
        });
        Assert.Empty(rows);
    }

    [Fact]
    public void GroupHairByName_MissingNameFallbackDoesNotFold()
    {
        // 缺名回退 "{cat}_{id}"（发型_30000）+ 单例 → 均不折叠（无组）
        var rows = MaterialBrowserWindow.GroupHairByName(new[]
        {
            Hair("30000", "发型_30000"),
            Hair("30001", "发型_30001"),
            Hair("30002", "短发"),
        });
        Assert.Empty(rows);
        // 真多变体组不受缺名条目干扰，仍正常折叠
        var rows2 = MaterialBrowserWindow.GroupHairByName(new[]
        {
            Hair("30000", "发型_30000"),
            Hair("30010", "短发"),
            Hair("30011", "短发"),
        });
        Assert.Single(rows2);
        Assert.Equal("短发", rows2[0].Name);
        Assert.Equal(2, rows2[0].Variants.Count);
    }

    [Fact]
    public void IsFallbackHairName_DetectsMissingNameFallback()
    {
        Assert.True(MaterialBrowserWindow.IsFallbackHairName("发型_30000"));
        Assert.False(MaterialBrowserWindow.IsFallbackHairName("短发"));
        Assert.False(MaterialBrowserWindow.IsFallbackHairName("发型"));
    }

    // ── I2: 性别联动过滤 ──

    [Theory]
    [InlineData("发型", "30000", 0, true)]   // 男 → 男发
    [InlineData("发型", "30999", 0, true)]   // 男 → 男发边界
    [InlineData("发型", "31000", 0, false)]  // 男 → 女发过滤
    [InlineData("发型", "31000", 1, true)]   // 女 → 女发
    [InlineData("发型", "30000", 1, false)]  // 女 → 男发过滤
    [InlineData("脸型", "20000", 0, true)]   // 男 → 男脸
    [InlineData("脸型", "21000", 0, false)]  // 男 → 女脸过滤
    [InlineData("脸型", "21000", 1, true)]   // 女 → 女脸
    [InlineData("脸型", "20000", 1, false)]  // 女 → 男脸过滤
    [InlineData("帽子", "1000000", 0, true)] // 非发型/脸型不过滤
    [InlineData("帽子", "1000000", 1, true)]
    public void MatchesGender_FiltersHairAndFaceByGender(string category, string id, int gender, bool expected)
    {
        Assert.Equal(expected, MaterialBrowserWindow.MatchesGender(category, id, gender));
    }

    // ── D: icon 根路径 ──

    [Theory]
    [InlineData("发型", "30000", "Character/Hair/00030000.img")]
    [InlineData("脸型", "20000", "Character/Face/00020000.img")]
    [InlineData("帽子", "1000000", "Character/Cap/01000000.img")]
    [InlineData("武器", "1302000", "Character/Weapon/01302000.img")]
    [InlineData("坐骑", "1902000", "Character/TamingMob/01902000.img")]
    [InlineData("椅子", "3010000", "Item/Install/0301.img/03010000")]
    [InlineData("怪物", "8880150", null)]
    // 饰品三分类（对齐 MapleSalon2 拆分）：101 面饰 / 102 眼饰 / 103 耳环 → 同一 Accessory 目录
    [InlineData("面饰", "1010000", "Character/Accessory/01010000.img")]
    [InlineData("眼饰", "1020000", "Character/Accessory/01020000.img")]
    [InlineData("耳环", "1030000", "Character/Accessory/01030000.img")]
    public void CategoryWzRoot_MapsCategoryToIconRoot(string category, string id, string? expected)
    {
        Assert.Equal(expected, MaterialBrowserWindow.CategoryWzRoot(category, id));
    }
}
