using Xunit;
using MiniPet.Models;
using MiniPet.Services;
using System;
using System.IO;
using System.Collections.Generic;
using System.Text.Json;

namespace MiniPet.Tests;

public class ModelsTest
{
    [Fact]
    public void Sprite_Defaults()
    {
        var s = new Sprite();
        Assert.Equal(0, s.Width);
        Assert.Equal(0, s.Height);
        Assert.Equal(0, s.OriginX);
        Assert.Equal(0, s.OriginY);
        Assert.Equal(0, s.Z);
        Assert.Equal("", s.ResourceUrl);
    }

    [Fact]
    public void Frame_InheritsSprite()
    {
        var f = new Frame { Width = 100, Height = 200, Delay = 150, A0 = 255, A1 = 128 };
        Assert.Equal(100, f.Width);
        Assert.Equal(200, f.Height);
        Assert.Equal(150, f.Delay);
        Assert.Equal(255, f.A0);
        Assert.Equal(128, f.A1);
    }

    [Fact]
    public void SpriteStrip_Properties()
    {
        var s = new SpriteStrip { Action = "stand", FrameWidth = 100, FrameHeight = 120, FrameCount = 8 };
        Assert.Equal("stand", s.Action);
        Assert.Equal(100, s.FrameWidth);
        Assert.Equal(120, s.FrameHeight);
        Assert.Equal(8, s.FrameCount);
        Assert.Equal(800, s.TotalWidth);
    }

    [Fact]
    public void PetConfig_Serialization()
    {
        var cfg = new PetConfig { Name = "路西德", Version = 1, Type = "mob" };
        cfg.Sprites["stand"] = new SpriteStrip { Action = "stand", FrameWidth = 100, FrameHeight = 120, FrameCount = 8 };
        var json = JsonSerializer.Serialize(cfg);
        Assert.Contains("路西德", json);
        Assert.Contains("stand", json);
        var restored = JsonSerializer.Deserialize<PetConfig>(json);
        Assert.NotNull(restored);
        Assert.Equal("路西德", restored!.Name);
        Assert.Equal(1, restored.Version);
        Assert.True(restored.Sprites.ContainsKey("stand"));
    }

    [Fact]
    public void PetSettings_Defaults()
    {
        var s = new PetSettings();
        Assert.Equal("", s.WzLibPath);
        Assert.Equal("", s.BaseWzPath);
        Assert.Equal("", s.CurrentMobId);
        Assert.Equal("", s.CurrentEntityType);
        Assert.Equal(1.0, s.WindowScale);
        Assert.NotNull(s.Favorites);
    }

    [Fact]
    public void WzConfig_Defaults()
    {
        var c = new WzConfig();
        Assert.Equal("", c.WzLibPath);
        Assert.Equal("", c.BaseWzPath);
    }

    [Fact]
    public void ConnectionStatus_Enum()
    {
        Assert.Equal(0, (int)ConnectionStatus.Disconnected);
        Assert.Equal(1, (int)ConnectionStatus.Connected);
        Assert.Equal(2, (int)ConnectionStatus.Reconnecting);
        Assert.Equal(3, (int)ConnectionStatus.Error);
    }

    [Fact]
    public void FavoriteItem_Properties()
    {
        var f = new FavoriteItem { Type = "mob", Id = "8880150", Name = "路西德", AddedAt = DateTime.Now };
        Assert.Equal("mob", f.Type);
        Assert.Equal("8880150", f.Id);
        Assert.Equal("路西德", f.Name);
    }

    [Fact]
    public void BalloonData_Defaults()
    {
        var b = new BalloonData();
        Assert.Equal(0, b.Id);
        Assert.Equal("", b.Name);
        Assert.NotNull(b.Tiles);
        Assert.Equal("", b.DefaultText);
    }

    [Fact]
    public void CharacterAppearance_GenderBodyCode()
    {
        var male = new CharacterAppearance { Gender = 0 };
        var female = new CharacterAppearance { Gender = 1 };
        Assert.Equal("00002000", male.BodyCode);
        Assert.Equal("00012000", female.BodyCode);
    }
}

public class ConfigServiceTest : IDisposable
{
    private readonly string _testDir;

    public ConfigServiceTest()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "MiniPetTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
    }

    [Fact]
    public void SaveAndLoad_Settings()
    {
        var svc = new ConfigService(_testDir);
        var settings = new PetSettings { CurrentMobId = "8880150", Theme = "dark", WindowScale = 1.5 };
        svc.SaveSettings(settings);
        var loaded = svc.LoadSettings();
        Assert.Equal("8880150", loaded.CurrentMobId);
        Assert.Equal("dark", loaded.Theme);
        Assert.Equal(1.5, loaded.WindowScale);
    }

    [Fact]
    public void LoadSettings_FileNotExist_ReturnsDefaults()
    {
        var svc = new ConfigService(_testDir);
        var loaded = svc.LoadSettings();
        Assert.NotNull(loaded);
        Assert.Equal("", loaded.CurrentMobId);
        Assert.Equal(1.0, loaded.WindowScale);
    }

    [Fact]
    public void SaveAndLoad_WzConfig()
    {
        var svc = new ConfigService(_testDir);
        var cfg = new WzConfig { WzLibPath = "/path/to/dll", BaseWzPath = "/data" };
        svc.SaveWzConfig(cfg);
        var loaded = svc.LoadWzConfig();
        Assert.Equal("/path/to/dll", loaded.WzLibPath);
        Assert.Equal("/data", loaded.BaseWzPath);
    }

    [Fact]
    public void ResetToDefaults()
    {
        var svc = new ConfigService(_testDir);
        svc.SaveSettings(new PetSettings { CurrentMobId = "test", Theme = "light" });
        svc.ResetToDefaults();
        var loaded = svc.LoadSettings();
        Assert.Equal("", loaded.CurrentMobId);
        Assert.Equal("dark", loaded.Theme);
    }

    [Fact]
    public void OnSettingsChanged_Fires()
    {
        var svc = new ConfigService(_testDir);
        PetSettings? received = null;
        svc.OnSettingsChanged += s => received = s;
        svc.SaveSettings(new PetSettings { CurrentMobId = "test" });
        Assert.NotNull(received);
        Assert.Equal("test", received!.CurrentMobId);
    }
}

public class CacheManagerTest : IDisposable
{
    private readonly string _testDir;
    private readonly CacheManager _cache;

    public CacheManagerTest()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "MiniPetCache_" + Guid.NewGuid().ToString("N")[..8]);
        _cache = new CacheManager(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
    }

    [Fact]
    public void SaveAndLoad_SpriteStrip()
    {
        var pngData = new byte[] { 1, 2, 3, 4 };
        var cfgData = new byte[] { 10, 20, 30 };
        _cache.SaveSpriteStrip("8880150", "stand", pngData, cfgData);
        Assert.True(_cache.IsSpriteCached("8880150", "stand"));
        var (png, cfg) = _cache.LoadSpriteStrip("8880150", "stand")!.Value;
        Assert.Equal(pngData, png);
        Assert.Equal(cfgData, cfg);
    }

    [Fact]
    public void NotCached_ReturnsFalse()
    {
        Assert.False(_cache.IsSpriteCached("nonexistent", "stand"));
        Assert.Null(_cache.LoadSpriteStrip("nonexistent", "stand"));
    }

    [Fact]
    public void GetCacheDir()
    {
        var dir = _cache.GetCacheDir();
        Assert.Contains("MiniPet", dir);
    }

    [Fact]
    public void ClearOldCache()
    {
        _cache.SaveSpriteStrip("test", "stand", [1,2], [3,4]);
        _cache.ClearOldCache(0); // clear all
        Assert.False(_cache.IsSpriteCached("test", "stand"));
    }
}

public class AnimServiceTest : IDisposable
{
    private readonly string _testDir;
    private readonly CacheManager _cache;

    public AnimServiceTest()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "MiniPetAnim_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_testDir);
        _cache = new CacheManager(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
    }

    [Fact]
    public void Play_Stand_Loop()
    {
        var spriteSvc = new SpriteService(_cache);
        var anim = new AnimService(spriteSvc);
        anim.Play("stand", PlayMode.Loop);
        Assert.Equal("stand", anim.GetCurrentAction());
    }

    [Fact]
    public void SwitchTo_ChangesAction()
    {
        var spriteSvc = new SpriteService(_cache);
        var anim = new AnimService(spriteSvc);
        anim.Play("stand", PlayMode.Loop);
        anim.SwitchTo("fly");
        Assert.Equal("fly", anim.GetCurrentAction());
    }
}
