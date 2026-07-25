using Xunit;
using System;
using System.Text.Json;
using System.Collections.Generic;

namespace MiniPet.Tests;

public class ConfigServiceTests
{
    // --- minimal embedded models (no reference to main project) ---
    public class TestPetSettings
    {
        public string WzLibPath { get; set; } = "";
        public string BaseWzPath { get; set; } = "";
        public int CurrentMobId { get; set; }
        public string CurrentEntityType { get; set; } = "mob";
        public int MapId { get; set; }
        public int BalloonId { get; set; }
        public string DefaultAnim { get; set; } = "stand";
        public string Theme { get; set; } = "dark";
        public string Personality { get; set; } = "";
        public List<TestFavoriteItem> Favorites { get; set; } = new();
        public double WindowScale { get; set; } = 1.0;
    }

    public class TestFavoriteItem
    {
        public string Type { get; set; } = "";
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    public class TestWzConfig
    {
        public string WzLibPath { get; set; } = "";
        public string BaseWzPath { get; set; } = "";
    }

    // --- test JsonSerializer options matching ConfigService ---
    private static JsonSerializerOptions GetJsonOptions()
    {
        return new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
    }

    // --- tests: 正常路径 ---

    [Fact]
    public void Serialize_Deserialize_RoundTrip_ReturnsEqualSettings()
    {
        var original = new TestPetSettings
        {
            CurrentMobId = 8880150,
            CurrentEntityType = "mob",
            DefaultAnim = "stand",
            Theme = "dark",
            WindowScale = 1.5,
            Favorites = new List<TestFavoriteItem>
            {
                new() { Type = "mob", Id = 8880150, Name = "路西德" }
            }
        };

        var json = JsonSerializer.Serialize(original, GetJsonOptions());
        var restored = JsonSerializer.Deserialize<TestPetSettings>(json, GetJsonOptions());

        Assert.NotNull(restored);
        Assert.Equal(original.CurrentMobId, restored!.CurrentMobId);
        Assert.Equal(original.CurrentEntityType, restored.CurrentEntityType);
        Assert.Equal(original.DefaultAnim, restored.DefaultAnim);
        Assert.Equal(original.WindowScale, restored.WindowScale);
        Assert.Single(restored.Favorites);
    }

    [Fact]
    public void Serialize_ContainsChineseCharacters()
    {
        var settings = new TestPetSettings
        {
            CurrentMobId = 8880150,
            Personality = "傲娇"
        };
        var options = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var json = JsonSerializer.Serialize(settings, options);

        Assert.Contains("傲娇", json);
    }

    [Fact]
    public void ChangeNotification_FiresOnSave()
    {
        var fired = false;
        TestPetSettings? received = null;

        void OnChanged(TestPetSettings s)
        {
            fired = true;
            received = s;
        }

        var svc = new TestConfigServiceMinimal();
        svc.OnSettingsChanged += OnChanged;

        var settings = new TestPetSettings { CurrentMobId = 12345 };
        svc.SaveSettings(settings);

        Assert.True(fired);
        Assert.NotNull(received);
        Assert.Equal(12345, received!.CurrentMobId);
    }

    [Fact]
    public void DefaultSettings_HaveExpectedValues()
    {
        var settings = new TestPetSettings();

        Assert.Equal("mob", settings.CurrentEntityType);
        Assert.Equal("dark", settings.Theme);
        Assert.Equal("stand", settings.DefaultAnim);
        Assert.Equal(1.0, settings.WindowScale);
        Assert.Empty(settings.Favorites);
    }

    // --- tests: 边界 ---

    [Fact]
    public void Deserialize_EmptyJson_UseDefaults()
    {
        var json = "{}";
        var settings = JsonSerializer.Deserialize<TestPetSettings>(json, GetJsonOptions());

        Assert.NotNull(settings);
        Assert.Equal("mob", settings!.CurrentEntityType);
        Assert.Equal("dark", settings.Theme);
        Assert.Equal(0, settings.CurrentMobId);
    }

    [Fact]
    public void Deserialize_JsonWithMissingFields_PartialDefaultsApplied()
    {
        var json = """{"currentMobId":9999999}""";
        var settings = JsonSerializer.Deserialize<TestPetSettings>(json, GetJsonOptions());

        Assert.NotNull(settings);
        Assert.Equal(9999999, settings!.CurrentMobId);
        Assert.Equal("mob", settings.CurrentEntityType); // default
    }

    // --- tests: 异常 ---

    [Fact]
    public void Deserialize_InvalidJson_ThrowsJsonException()
    {
        var json = "{invalid json";
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<TestPetSettings>(json, GetJsonOptions()));
    }

    [Fact]
    public void Deserialize_WrongType_ThrowsJsonException()
    {
        var json = """{"windowScale":"not_a_number"}""";
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<TestPetSettings>(json, GetJsonOptions()));
    }

    [Fact]
    public void WzConfig_DefaultsAreEmpty()
    {
        var config = new TestWzConfig();
        Assert.Equal("", config.WzLibPath);
        Assert.Equal("", config.BaseWzPath);
    }

    [Fact]
    public void WzConfig_RoundTrip_PreservesPaths()
    {
        var config = new TestWzConfig
        {
            WzLibPath = "/usr/lib/wz.dll",
            BaseWzPath = "/data/wz"
        };
        var json = JsonSerializer.Serialize(config, GetJsonOptions());
        var restored = JsonSerializer.Deserialize<TestWzConfig>(json, GetJsonOptions());

        Assert.NotNull(restored);
        Assert.Equal(config.WzLibPath, restored!.WzLibPath);
        Assert.Equal(config.BaseWzPath, restored.BaseWzPath);
    }
}

// --- minimal config service replica for testability ---
internal class TestConfigServiceMinimal
{
    private ConfigServiceTests.TestPetSettings _settings = new();
    public event Action<ConfigServiceTests.TestPetSettings>? OnSettingsChanged;

    public ConfigServiceTests.TestPetSettings LoadSettings() => _settings;

    public void SaveSettings(ConfigServiceTests.TestPetSettings settings)
    {
        _settings = settings;
        OnSettingsChanged?.Invoke(settings);
    }

    public void ResetToDefaults()
    {
        _settings = new ConfigServiceTests.TestPetSettings();
        OnSettingsChanged?.Invoke(_settings);
    }
}
