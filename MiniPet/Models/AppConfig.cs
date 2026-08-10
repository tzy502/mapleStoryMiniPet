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
        public string CurrentMobId { get; set; } = string.Empty;
        public string CurrentEntityType { get; set; } = string.Empty;
        // 默认地图：射手村（100000000）。crop 全零 → CropFull 归一化为整图。
        public string MapId { get; set; } = "100000000";
        // 背景模式：1=静态整图（BackgroundWindow），2=摄像机（MapViewportWindow，左键拖动相机 + 滚轮缩放 + back 视差实时）
        public int MapMode { get; set; } = 1;
        // 摄像机镜头大小（显示窗口尺寸，默认 800×600；设置中心可调）
        public int ViewportW { get; set; } = 800;
        public int ViewportH { get; set; } = 600;
        public int CropX { get; set; }
        public int CropY { get; set; }
        public int CropW { get; set; }
        public int CropH { get; set; }
        public List<MapHistoryEntry> MapHistory { get; set; } = new List<MapHistoryEntry>();
        public string BalloonId { get; set; } = string.Empty;
        public List<string> BalloonHistory { get; set; } = new List<string>();
        public string DefaultAnim { get; set; } = string.Empty;
        public string Theme { get; set; } = string.Empty;
        public string Personality { get; set; } = string.Empty;
        public List<FavoriteItem> Favorites { get; set; } = new List<FavoriteItem>();
        public List<RecentItem> RecentItems { get; set; } = new List<RecentItem>();
        public int WindowX { get; set; }
        public int WindowY { get; set; }
        public double WindowScale { get; set; }
        public HermesConfig? HermesConfig { get; set; }
        // 地图分层显隐（默认全开，Foothold 默认关）。RenderCore 按位掩码跳过对应层。
        public bool MapShowBack { get; set; } = true;
        public bool MapShowTile { get; set; } = true;
        public bool MapShowObj { get; set; } = true;
        public bool MapShowLife { get; set; } = true;
        public bool MapShowPortal { get; set; } = true;
        public bool MapShowFoothold { get; set; }
        // 纸娃娃当前搭配（CurrentMobId == "__paperdoll__" 时启动恢复）
        public CharacterAppearance? CurrentPaperdoll { get; set; }
        // 纸娃娃预设（搭配存档：预设名 → 外观）
        public Dictionary<string, CharacterAppearance> SavedPaperdolls { get; set; } = new Dictionary<string, CharacterAppearance>();
        // Agent 接入（Hermes / Claude Code / Codex 状态订阅 + 通用特效层）
        public AgentIntegrationConfig AgentIntegration { get; set; } = new AgentIntegrationConfig();
    }

    /// <summary>
    /// Agent 接入配置（PLAN-agent-integration §7）：
    /// 各 adapter 开关、轮询/静默/staleness 阈值、特效开关、WZ 庆祝节点路径（可手填覆盖）。
    /// 序列化沿用 PetSettings 的 CamelCase 策略（JsonNamingPolicy.CamelCase）。
    /// </summary>
    public class AgentIntegrationConfig
    {
        // adapter 开关（默认仅 Hermes 开）
        public bool HermesEnabled { get; set; } = true;
        public bool CCEnabled { get; set; }
        public bool CodexEnabled { get; set; }
        // 通用特效层（Jump 庆祝 / Failed 弹窗）
        public bool EffectEnabled { get; set; }

        // Hermes state.db 只读轮询周期（秒）
        public int HermesPollIntervalSeconds { get; set; } = 5;
        // Claude Code 完成态静默窗（秒，append debounce-reset）
        public int CCSilenceSeconds { get; set; } = 20;
        // Jump 一次性庆祝时长（秒）
        public int JumpDisplaySeconds { get; set; } = 3;
        // 源超此秒数无事件 → 降 Idle 并清 Failed/Run/Review
        public int StalenessSeconds { get; set; } = 30;

        // WZ 庆祝动画节点路径（手填覆盖；null/空 → 自动探测 Effect/、UI/Basic emotion、Item/）
        public string? WzCelebrationNodePath { get; set; }
    }

    /// <summary>
    /// 收藏项
    /// </summary>
    public class FavoriteItem
    {
        public string Type { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public DateTime AddedAt { get; set; }
    }

    /// <summary>
    /// 最近使用项
    /// </summary>
    public class RecentItem
    {
        public string Type { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public DateTime LastUsedAt { get; set; }
    }

    /// <summary>
    /// 地图历史项（带各自 crop，供菜单「最近使用」一键恢复显示区域）。
    /// 旧配置的 List&lt;string&gt; 由 MapHistoryEntryConverter 兼容读入（crop 全零 → CropFull 归一化为整图）。
    /// </summary>
    public class MapHistoryEntry
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int CropX { get; set; }
        public int CropY { get; set; }
        public int CropW { get; set; }
        public int CropH { get; set; }
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