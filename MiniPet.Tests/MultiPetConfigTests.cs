using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MiniPet.Models;
using MiniPet.Services;
using Xunit;
// 注意：ModelTests.cs 里另有一个副本模型 MiniPet.Tests.PetSettings（同命名空间成员会遮蔽 using 别名），
// 因此这里用独立别名 PetSettingsModel 锁定被测实现类型 MiniPet.Models.PetSettings
using PetSettingsModel = MiniPet.Models.PetSettings;

namespace MiniPet.Tests;

/// <summary>
/// V0.1.3 多桌宠配置契约单测（被测实现：MiniPet/Services/ConfigService.cs、MiniPet/Models/AppConfig.cs，不改实现）。
///
/// 配置隔离说明（关键：不能污染真实用户配置 ~/.minipet/settings.json）：
/// 现有 MiniPet.Tests/ConfigServiceTests.cs 只用「副本模型 + 副本服务」做纯 JSON 断言，从不实例化真实
/// ConfigService，因此没有可直接沿用的隔离手法。这里改用反射：把 ConfigService 的私有 readonly 字段
/// _settingsPath / _wzConfigPath 指向 Path.GetTempPath() 下的随机临时目录，并清空两个内存 JSON 缓存；
/// 替换后立即回读校验，校验失败直接抛异常（宁可不测，也绝不落盘到真实用户配置）；
/// 测试结束（Dispose）递归删除该临时目录。
/// </summary>
public class MultiPetConfigTests
{
    // ───────────────────────── 1. 旧配置迁移 ─────────────────────────

    /// <summary>空 Pets + 旧 CurrentMobId → EnsurePets 迁移出 pet-1（单只 + 主宠）。</summary>
    [Fact]
    public void EnsurePets_EmptyPets_MigratesLegacySinglePet()
    {
        var settings = new PetSettingsModel
        {
            CurrentMobId = "100100",
            CurrentEntityType = "mob"
        };

        var pets = ConfigService.EnsurePets(settings);

        Assert.Single(pets);
        Assert.Single(settings.Pets);
        Assert.Equal("pet-1", settings.Pets[0].Id);
        Assert.Equal("100100", settings.Pets[0].MobId);
        Assert.True(settings.Pets[0].Primary);
    }

    /// <summary>旧 CurrentMobId = 纸娃娃哨兵 → 迁移后 EntityType 归一化为 "player"。</summary>
    [Fact]
    public void EnsurePets_LegacyPaperdollSentinel_MigratesToPlayer()
    {
        var settings = new PetSettingsModel
        {
            CurrentMobId = PaperdollService.SentinelMobId
        };

        ConfigService.EnsurePets(settings);

        Assert.Single(settings.Pets);
        Assert.Equal("player", settings.Pets[0].EntityType);
        Assert.Equal("__paperdoll__", settings.Pets[0].MobId);
    }

    /// <summary>旧坐标 (0,0) = 从未摆放过 → 迁移后为 int.MinValue（未设置）。</summary>
    [Fact]
    public void EnsurePets_LegacyZeroPosition_TreatedAsUnset()
    {
        var settings = new PetSettingsModel
        {
            CurrentMobId = "100100",
            WindowX = 0,
            WindowY = 0
        };

        ConfigService.EnsurePets(settings);

        Assert.Equal(int.MinValue, settings.Pets[0].WindowX);
        Assert.Equal(int.MinValue, settings.Pets[0].WindowY);
    }

    /// <summary>旧坐标非 (0,0) → 迁移后原样保留。</summary>
    [Fact]
    public void EnsurePets_LegacyNonZeroPosition_Preserved()
    {
        var settings = new PetSettingsModel
        {
            CurrentMobId = "100100",
            WindowX = 300,
            WindowY = 200
        };

        ConfigService.EnsurePets(settings);

        Assert.Equal(300, settings.Pets[0].WindowX);
        Assert.Equal(200, settings.Pets[0].WindowY);
    }

    // ───────────────────────── 2. 主宠唯一性自愈 ─────────────────────────

    /// <summary>手工构造 2 只都 Primary=true → 自愈后只有第一只保持主宠。</summary>
    [Fact]
    public void EnsurePets_TwoPrimaries_OnlyFirstStaysPrimary()
    {
        var settings = new PetSettingsModel();
        settings.Pets.Add(new PetInstanceConfig { Id = "pet-1", MobId = "1", Primary = true });
        settings.Pets.Add(new PetInstanceConfig { Id = "pet-2", MobId = "2", Primary = true });

        ConfigService.EnsurePets(settings);

        Assert.Equal(2, settings.Pets.Count);
        Assert.True(settings.Pets[0].Primary);
        Assert.False(settings.Pets[1].Primary);
        Assert.Single(settings.Pets, p => p.Primary);
    }

    /// <summary>手工构造 2 只都 Primary=false → 自愈后第一只被设为主宠。</summary>
    [Fact]
    public void EnsurePets_NoPrimary_FirstBecomesPrimary()
    {
        var settings = new PetSettingsModel();
        settings.Pets.Add(new PetInstanceConfig { Id = "pet-1", MobId = "1" });
        settings.Pets.Add(new PetInstanceConfig { Id = "pet-2", MobId = "2" });

        ConfigService.EnsurePets(settings);

        Assert.Equal(2, settings.Pets.Count);
        Assert.True(settings.Pets[0].Primary);
        Assert.False(settings.Pets[1].Primary);
        Assert.Single(settings.Pets, p => p.Primary);
    }

    // ───────────────────────── 3. id 分配 / 增删 ─────────────────────────

    /// <summary>已有 pet-1 / pet-2 → NewPetId 返回未占用的 pet-3。</summary>
    [Fact]
    public void NewPetId_ReturnsFirstFreeId()
    {
        var settings = new PetSettingsModel();
        settings.Pets.Add(new PetInstanceConfig { Id = "pet-1", Primary = true });
        settings.Pets.Add(new PetInstanceConfig { Id = "pet-2" });

        Assert.Equal("pet-3", ConfigService.NewPetId(settings));
    }

    /// <summary>AddPet：追加一只（Count+1）、强制 Primary=false、Id 为空时自动分配。</summary>
    [Fact]
    public void AddPet_AppendsWithNewIdAndNotPrimary()
    {
        using var env = new TempConfigEnv();

        var added = env.Service.AddPet(new PetInstanceConfig
        {
            EntityType = "Mob",
            MobId = "200100",
            Primary = true   // 故意传入 true，验证被实现强制置 false
        });

        Assert.Equal("pet-2", added.Id);   // pet-1 是启动迁移出的那只，新 id 顺延
        Assert.False(added.Primary);
        Assert.Equal(2, env.Service.LoadSettings().Pets.Count);
        Assert.False(env.Service.LoadSettings().Pets[1].Primary);
        Assert.Equal("200100", env.NewService().LoadSettings().Pets[1].MobId);
        Assert.False(env.NewService().LoadSettings().Pets[1].Primary);
    }

    /// <summary>RemovePet 边界：仅剩 1 只 → 返回 false 且列表不变。</summary>
    [Fact]
    public void RemovePet_LastPet_ReturnsFalseAndKeepsList()
    {
        using var env = new TempConfigEnv();
        Assert.Single(env.Service.LoadSettings().Pets);

        var removed = env.Service.RemovePet("pet-1");

        Assert.False(removed);
        var after = env.NewService().LoadSettings();
        Assert.Single(after.Pets);
        Assert.Equal("pet-1", after.Pets[0].Id);
        Assert.True(after.Pets[0].Primary);
    }

    /// <summary>RemovePet 主宠继任：2 只（pet-1 主宠）删 pet-1 → true，剩 pet-2 继任主宠。</summary>
    [Fact]
    public void RemovePet_PrimaryRemoved_FirstRemainingSucceeds()
    {
        using var env = new TempConfigEnv();
        env.Service.AddPet(new PetInstanceConfig { EntityType = "Mob", MobId = "200100" });

        var removed = env.Service.RemovePet("pet-1");

        Assert.True(removed);
        var after = env.NewService().LoadSettings();
        Assert.Single(after.Pets);
        Assert.Equal("pet-2", after.Pets[0].Id);
        Assert.True(after.Pets[0].Primary);
    }

    // ───────────────────────── 4. 主宠切换 ─────────────────────────

    /// <summary>SetPrimaryPet：目标成为唯一主宠，其余被置 false。</summary>
    [Fact]
    public void SetPrimaryPet_SwitchesUniquePrimary()
    {
        using var env = new TempConfigEnv();
        env.Service.AddPet(new PetInstanceConfig { EntityType = "Mob", MobId = "200100" });

        var ok = env.Service.SetPrimaryPet("pet-2");

        Assert.True(ok);
        var after = env.NewService().LoadSettings();
        Assert.False(after.Pets[0].Primary);
        Assert.True(after.Pets[1].Primary);
        Assert.Single(after.Pets, p => p.Primary);
    }

    /// <summary>SetPrimaryPet：不存在的 id → 返回 false，原主宠不变。</summary>
    [Fact]
    public void SetPrimaryPet_UnknownId_ReturnsFalse()
    {
        using var env = new TempConfigEnv();
        env.Service.AddPet(new PetInstanceConfig { EntityType = "Mob", MobId = "200100" });

        var ok = env.Service.SetPrimaryPet("pet-404");

        Assert.False(ok);
        var after = env.NewService().LoadSettings();
        Assert.Equal("pet-1", after.Pets.Single(p => p.Primary).Id);
    }

    // ───────────────────────── 5. 单只更新 + 旧字段镜像 ─────────────────────────

    /// <summary>UpdatePet：改 MobId / WindowScale 后能从配置读回（内存 + 磁盘）。</summary>
    [Fact]
    public void UpdatePet_UpdatesFieldsAndPersists()
    {
        using var env = new TempConfigEnv();

        var updated = env.Service.UpdatePet("pet-1", p =>
        {
            p.MobId = "300100";
            p.WindowScale = 1.5;
        });

        Assert.NotNull(updated);
        var loaded = env.Service.LoadSettings();
        Assert.Equal("300100", loaded.Pets[0].MobId);
        Assert.Equal(1.5, loaded.Pets[0].WindowScale);

        var onDisk = env.NewService().LoadSettings();
        Assert.Equal("300100", onDisk.Pets[0].MobId);
        Assert.Equal(1.5, onDisk.Pets[0].WindowScale);
    }

    /// <summary>UpdatePet：不存在的 id → 返回 null。</summary>
    [Fact]
    public void UpdatePet_UnknownId_ReturnsNull()
    {
        using var env = new TempConfigEnv();

        var updated = env.Service.UpdatePet("pet-404", p => { p.MobId = "1"; });

        Assert.Null(updated);
    }

    /// <summary>主宠镜像同步：改主宠 MobId / EntityType → 旧字段 CurrentMobId 更新、CurrentEntityType 小写。</summary>
    [Fact]
    public void UpdatePet_Primary_MirrorsLegacyFields()
    {
        using var env = new TempConfigEnv();

        var updated = env.Service.UpdatePet("pet-1", p =>
        {
            p.MobId = "100100";
            p.EntityType = "Npc";
            p.WindowScale = 2.0;
        });

        Assert.NotNull(updated);
        var loaded = env.Service.LoadSettings();
        Assert.Equal("100100", loaded.CurrentMobId);
        Assert.Equal("npc", loaded.CurrentEntityType);
        Assert.Equal(2.0, loaded.WindowScale);

        var onDisk = env.NewService().LoadSettings();
        Assert.Equal("100100", onDisk.CurrentMobId);
        Assert.Equal("npc", onDisk.CurrentEntityType);
    }

    // ───────────────────────── 6. 合并保存不回滚 Pets ─────────────────────────

    /// <summary>
    /// SaveSettingsKeepingPets：先 AddPet 造出 2 只，再用「Pets 为空的旧快照」（只改全局 MapId）
    /// 保存 → 磁盘上 Pets 仍是 2 只（未被旧快照回滚），且 MapId 已按旧快照更新。
    /// </summary>
    [Fact]
    public void SaveSettingsKeepingPets_DoesNotRollbackPets()
    {
        using var env = new TempConfigEnv();
        env.Service.AddPet(new PetInstanceConfig { EntityType = "Mob", MobId = "200100" });
        Assert.Equal(2, env.Service.LoadSettings().Pets.Count);

        // 模拟设置中心各 tab 持有的旧快照：那时还没有 Pets（空列表），只改了一个全局字段
        var staleSnapshot = new PetSettingsModel
        {
            MapId = "999999999",
            Pets = new List<PetInstanceConfig>()
        };

        env.Service.SaveSettingsKeepingPets(staleSnapshot);

        var onDisk = env.NewService().LoadSettings();
        Assert.Equal(2, onDisk.Pets.Count);              // 没有被旧快照回滚成 0 只
        Assert.Equal("pet-1", onDisk.Pets[0].Id);
        Assert.Equal("pet-2", onDisk.Pets[1].Id);
        Assert.Equal("999999999", onDisk.MapId);         // 非 Pets 字段按 local 覆盖

        Assert.Equal(2, env.Service.LoadSettings().Pets.Count);
    }

    // ───────────────────────── 隔离基础设施 ─────────────────────────

    /// <summary>
    /// 临时配置环境：把真实 ConfigService 的私有配置路径指向 Path.GetTempPath() 下的随机目录，
    /// 测试结束删除该目录（绝不触碰 ~/.minipet 真实用户配置）。
    /// </summary>
    private sealed class TempConfigEnv : IDisposable
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        /// <summary>本次测试专用的临时配置目录。</summary>
        public string Dir { get; }

        /// <summary>被测 ConfigService（已指向临时目录）。</summary>
        public ConfigService Service { get; }

        public TempConfigEnv()
        {
            Dir = Path.Combine(Path.GetTempPath(), "minipet-multipet-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Service = NewService();
        }

        /// <summary>另建一个指向同一临时目录的实例，用于「像下一个进程那样」从磁盘回读校验。</summary>
        public ConfigService NewService()
        {
            var service = new ConfigService();
            Repoint(service, Dir);
            return service;
        }

        /// <summary>反射替换私有 readonly 路径字段 + 清空内存 JSON 缓存；替换失败抛异常（安全闸）。</summary>
        private static void Repoint(ConfigService service, string dir)
        {
            var type = typeof(ConfigService);
            var expectedSettingsPath = Path.Combine(dir, "settings.json");
            var expectedWzConfigPath = Path.Combine(dir, "wz-config.json");

            type.GetField("_settingsPath", PrivateInstance)!.SetValue(service, expectedSettingsPath);
            type.GetField("_wzConfigPath", PrivateInstance)!.SetValue(service, expectedWzConfigPath);
            type.GetField("_settingsJsonCache", PrivateInstance)!.SetValue(service, null);
            type.GetField("_wzConfigJsonCache", PrivateInstance)!.SetValue(service, null);

            // 安全闸：路径没换成功就直接失败，绝不写真实用户配置
            var actual = (string?)type.GetField("_settingsPath", PrivateInstance)!.GetValue(service);
            if (!string.Equals(actual, expectedSettingsPath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"反射替换 _settingsPath 失败（实际 = {actual}），拒绝继续以免污染真实用户配置");
            }
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Dir))
                {
                    Directory.Delete(Dir, true);
                }
            }
            catch (IOException)
            {
                // 临时目录清理失败不影响断言结果
            }
        }
    }
}
