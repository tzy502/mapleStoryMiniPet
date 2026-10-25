using Xunit;
using MiniPet.Services;

namespace MiniPet.Tests;

/// <summary>
/// 地图显示名解析（MapCatalogService.ResolveDisplayName 纯函数）：
/// 修「历史菜单/设置历史列表中文缺失」——存量坏数据把 Name 存成了 id 本身
/// （10000/1000000 实测），展示时非空即用导致中文永远丢失。
/// 口径：存量名有效（非空、≠id、非 map_ 哨兵）→ 用存量；否则查缓存；全失败回退存量 ?? id。
/// </summary>
public class MapDisplayNameTests
{
    private static string Lookup(string id) => id switch
    {
        "10000" => "彩虹岛：枫树山丘",
        "1000000" => "彩虹岛：彩虹村",
        _ => $"map_{id}",   // 缓存 miss 哨兵（GetMapName 的 miss 形态）
    };

    [Fact]
    public void StoredNameIsId_FallsBackToLookup()
    {
        Assert.Equal("彩虹岛：枫树山丘", MapCatalogService.ResolveDisplayName("10000", "10000", Lookup));
    }

    [Fact]
    public void StoredNameIsEmpty_FallsBackToLookup()
    {
        Assert.Equal("彩虹岛：彩虹村", MapCatalogService.ResolveDisplayName("1000000", null, Lookup));
        Assert.Equal("彩虹岛：彩虹村", MapCatalogService.ResolveDisplayName("1000000", "", Lookup));
    }

    [Fact]
    public void StoredNameIsSentinel_FallsBackToLookup()
    {
        Assert.Equal("彩虹岛：枫树山丘", MapCatalogService.ResolveDisplayName("10000", "map_10000", Lookup));
    }

    [Fact]
    public void ValidStoredName_WinsOverLookup()
    {
        // 存量名有效（用户自定义/旧数据的好名字）不被覆盖
        Assert.Equal("我的收藏地图", MapCatalogService.ResolveDisplayName("10000", "我的收藏地图", Lookup));
    }

    [Fact]
    public void LookupMiss_ReturnsStoredNameThenId()
    {
        Assert.Equal("123456789", MapCatalogService.ResolveDisplayName("123456789", "123456789", Lookup));  // 全 miss → id
        Assert.Equal("旧名", MapCatalogService.ResolveDisplayName("123456789", "旧名", (i) => ""));          // lookup 空 → 存量
    }
}
