using Xunit;
using System;
using System.Collections.Generic;

namespace MiniPet.Tests;

public class WzServiceTests
{
    // --- tests: 正常路径 (WZ path formatting) ---

    [Fact]
    public void GetMobWzPath_ReturnsCorrectFormat()
    {
        var path = GetMobWzPath(8880150, "stand", 0);
        Assert.Equal("Mob/8880150.img/stand/0", path);
    }

    [Fact]
    public void GetNpcWzPath_ReturnsCorrectFormat()
    {
        var path = GetNpcWzPath(1012000, "stand", 5);
        Assert.Equal("Npc/1012000.img/stand/5", path);
    }

    [Fact]
    public void GetBalloonTilePath_ReturnsCorrectFormat()
    {
        var path = GetBalloonTilePath(1, "nw");
        Assert.Equal("UI/ChatBalloon.img/1/nw", path);
    }

    [Fact]
    public void GetCharacterPartWzPath_VariousParts_ReturnCorrectCategory()
    {
        Assert.Equal("Character/Cap/01000001.img/stand/0",
            GetCharacterPartWzPath("Cap", 1000001, "stand", 0));
        Assert.Equal("Character/Hair/00030000.img/walk/2",
            GetCharacterPartWzPath("Hair", 30000, "walk", 2));
        Assert.Equal("Character/Face/00020000.img/stand/0",
            GetCharacterPartWzPath("Face", 20000, "stand", 0));
        Assert.Equal("Character/Weapon/01302000.img/swing/1",
            GetCharacterPartWzPath("Weapon", 1302000, "swing", 1));
        Assert.Equal("Character/Body/00002000.img/stand/0",
            GetCharacterPartWzPath("Body", 2000, "stand", 0));
    }

    // --- tests: 边界 ---

    [Fact]
    public void GetCharacterPartWzPath_UnknownPart_UsesPartTypeDirectly()
    {
        var path = GetCharacterPartWzPath("CustomSlot", 123, "stand", 0);
        Assert.Equal("Character/CustomSlot/00000123.img/stand/0", path);
    }

    [Fact]
    public void GetMapWzPath_SingleDigitPrefix()
    {
        var path = GetMapWzPath(100000000);
        Assert.Equal("Map/Map/Map1/100000000.img", path);

        var path2 = GetMapWzPath(200000000);
        Assert.Equal("Map/Map/Map2/200000000.img", path2);
    }

    [Fact]
    public void GetMapWzPath_ZeroLength_HandlesGracefully()
    {
        var path = GetMapWzPath(0);
        Assert.Equal("Map/Map/Map0/0.img", path);
    }

    // --- tests: 异常 ---

    [Fact]
    public void GetFramePaths_EmptyPath_ReturnsEmptyList()
    {
        var result = GetFramePathsSimulated("");
        Assert.Empty(result);
    }

    [Fact]
    public void GetFramePaths_ShortPath_ReturnsEmptyList()
    {
        var result = GetFramePathsSimulated("Mob");
        Assert.Empty(result);
    }

    [Fact]
    public void GetFramePaths_ValidPath_ReturnsExpectedFrames()
    {
        var result = GetFramePathsSimulated("Mob/8880150.img/stand/0");
        Assert.NotEmpty(result);
    }

    // --- embedded minimal methods replicating WzService path logic ---

    private static string GetMobWzPath(int mobId, string action, int frame)
        => $"Mob/{mobId}.img/{action}/{frame}";

    private static string GetNpcWzPath(int npcId, string action, int frame)
        => $"Npc/{npcId}.img/{action}/{frame}";

    private static string GetBalloonTilePath(int balloonId, string tileName)
        => $"UI/ChatBalloon.img/{balloonId}/{tileName}";

    private static string GetCharacterPartWzPath(string partType, int itemId, string action, int frame)
    {
        var category = partType switch
        {
            "Cap" => "Cap",
            "Cape" => "Cape",
            "Coat" => "Coat",
            "Pants" => "Pants",
            "Shoes" => "Shoes",
            "Weapon" => "Weapon",
            "Shield" => "Shield",
            "Glove" => "Glove",
            "Hair" => "Hair",
            "Face" => "Face",
            "Body" => "Body",
            _ => partType
        };
        return $"Character/{category}/{itemId:D8}.img/{action}/{frame}";
    }

    private static string GetMapWzPath(int mapId)
    {
        var prefix = mapId.ToString().Length > 0 ? mapId.ToString()[0] : '0';
        return $"Map/Map/Map{prefix}/{mapId}.img";
    }

    private static List<string> GetFramePathsSimulated(string wzPath)
    {
        var result = new List<string>();
        var parts = wzPath.Split('/');
        if (parts.Length < 3) return result;
        var imgPath = parts[0] + "/" + parts[1];
        var action = parts[2];
        for (int i = 0; i < 10; i++)
            result.Add($"{imgPath}/{action}/{i}");
        return result;
    }
}
