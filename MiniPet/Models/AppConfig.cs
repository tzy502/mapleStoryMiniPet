using System;
using System.Collections.Generic;

namespace MiniPet.Models
{
    /// <summary>
    /// 宠物设置
    /// </summary>
    public class PetSettings
    {
        public string WzLibPath { get; set; } = string.Empty;
        public string BaseWzPath { get; set; } = string.Empty;
        public int CurrentMobId { get; set; }
        public string CurrentEntityType { get; set; } = string.Empty;
        public int MapId { get; set; }
        public int BalloonId { get; set; }
        public string DefaultAnim { get; set; } = string.Empty;
        public string Theme { get; set; } = string.Empty;
        public string Personality { get; set; } = string.Empty;
        public List<FavoriteItem> Favorites { get; set; } = new List<FavoriteItem>();
        public List<RecentItem> RecentItems { get; set; } = new List<RecentItem>();
        public int WindowX { get; set; }
        public int WindowY { get; set; }
        public double WindowScale { get; set; }
        public HermesConfig? HermesConfig { get; set; }
    }

    /// <summary>
    /// 收藏项
    /// </summary>
    public class FavoriteItem
    {
        public string Type { get; set; } = string.Empty;
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public DateTime AddedAt { get; set; }
    }

    /// <summary>
    /// 最近使用项
    /// </summary>
    public class RecentItem
    {
        public string Type { get; set; } = string.Empty;
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public DateTime LastUsedAt { get; set; }
    }

    /// <summary>
    /// WZ配置
    /// </summary>
    public class WzConfig
    {
        public string WzLibPath { get; set; } = string.Empty;
        public string BaseWzPath { get; set; } = string.Empty;
    }

    /// <summary>
    /// Hermes配置
    /// </summary>
    public class HermesConfig
    {
        public bool Enabled { get; set; }
        public string WebSocketUrl { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public bool AutoReconnect { get; set; }
        public int ReconnectDelayMs { get; set; }
    }

    /// <summary>
    /// 连接状态
    /// </summary>
    public enum ConnectionStatus
    {
        Connected,
        Disconnected,
        Reconnecting,
        Error
    }
}