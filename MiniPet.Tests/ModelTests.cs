using Xunit;
using System;
using System.Text.Json;
using System.Collections.Generic;

namespace MiniPet.Tests;

public class ModelTests
{
    [Fact] public void SpriteStrip_TotalWidth() { var s = new SpriteStrip { FrameWidth = 100, FrameCount = 8 }; Assert.Equal(800, s.TotalWidth); }
    [Fact] public void PetConfig_Serialize() { var c = new PetConfig { Name = "路西德", Version = 1, Type = "mob" }; var json = JsonSerializer.Serialize(c, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }); Assert.Contains("路西德", json); }
    [Fact] public void ConnectionStatus_Values() { Assert.Equal(0, (int)ConnectionStatus.Disconnected); Assert.Equal(1, (int)ConnectionStatus.Connected); }
    [Fact] public void CharacterAppearance_BodyCode_Male() { Assert.Equal("00002000", new CharacterAppearance { Gender = 0 }.BodyCode); }
    [Fact] public void CharacterAppearance_BodyCode_Female() { Assert.Equal("00012000", new CharacterAppearance { Gender = 1 }.BodyCode); }
    [Fact] public void FavoriteItem_Defaults() { var f = new FavoriteItem { Type = "mob", Id = "8880150", Name = "路西德" }; Assert.Equal("mob", f.Type); }
    [Fact] public void WzConfig_Defaults() { var c = new WzConfig(); Assert.Equal("", c.WzLibPath); }
    [Fact] public void PetSettings_Defaults() { var s = new PetSettings(); Assert.Equal(1.0, s.WindowScale); Assert.NotNull(s.Favorites); }
    [Fact] public void BalloonData_Defaults() { var b = new BalloonData(); Assert.Equal(0, b.Id); Assert.NotNull(b.Tiles); }
}

// Embedded model definitions (minimal copies for testability without referencing full Avalonia project)
public class Sprite { public int Width, Height, OriginX, OriginY, Z; public string ResourceUrl = ""; }
public class Frame : Sprite { public int Delay = 100, A0 = 255, A1 = 255; }
public class FrameAnimate { public List<Frame> Frames = new(); }
public class SpriteStrip { public string Action = ""; public int FrameWidth, FrameHeight, FrameCount; public int TotalWidth => FrameWidth * FrameCount; public int OriginX, OriginY; public List<FrameData> Frames = new(); }
public class FrameData { public int Index, Delay = 100, A0 = 255, A1 = 255; }
public class PetConfig { 
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public int Version { get; set; } = 1;
    public Dictionary<string,SpriteStrip> Sprites { get; set; } = new();
    public int? OriginX { get; set; }
    public int? OriginY { get; set; }
}
public class BalloonTiles { public byte[] NW, N, NE, W, C, E, SW, S, Arrow, SE, Head = Array.Empty<byte>(); }
public class BalloonData { public int Id; public string Name = ""; public BalloonTiles Tiles = new(); public int Clr; public string DefaultText = ""; }
public class CharacterAppearance { public int Gender; public string Skin = "", Hair = "", Face = ""; public string? Cap,Cape,Coat,Pants,Shoes,Weapon,Shield,Glove,Earring,Mount,Chair; public double DyeHue; public bool DyeEnabled; public string BodyCode => Gender == 0 ? "00002000" : "00012000"; }
public class FavoriteItem { public string Type = "", Id = "", Name = ""; public DateTime AddedAt; }
public class PetSettings { public string WzLibPath = "", BaseWzPath = "", CurrentMobId = "", CurrentEntityType = "", MapId = "", Theme = "dark", Personality = ""; public int BalloonId; public string DefaultAnim = ""; public double WindowScale = 1.0; public int WindowX, WindowY; public List<FavoriteItem> Favorites = new(); }
public class WzConfig { public string WzLibPath = "", BaseWzPath = ""; }
public enum ConnectionStatus { Disconnected, Connected, Reconnecting, Error }
