using System;
using System.Collections.Generic;
using MiniPet.Services;

namespace MiniPet.Models
{
    /// <summary>
    /// 单只桌宠的实例配置（V0.1.3 多桌宠）：一只宠物一份，实体 / 外观 / 位置 / 缩放各自独立。
    /// 旧版单值字段（CurrentMobId / CurrentEntityType / CurrentPaperdoll / WindowScale）保留为
    /// 主宠的兼容镜像：首次启动由 <see cref="Services.ConfigService.EnsurePets"/> 迁移成 Pets[0]，
    /// 主宠的这些字段变化时同步写回旧字段（旧版本/旧配置读取方仍能用）。
    /// </summary>
    public class PetInstanceConfig
    {
        /// <summary>稳定标识（"pet-1"…），跨启动不变；宠物列表 / 事件按它定位。</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>实体类型：Mob / Npc / player（player = 纸娃娃，MobId 为哨兵）。</summary>
        public string EntityType { get; set; } = "Mob";

        /// <summary>实体 id（Mob/Npc 为 WZ id）；纸娃娃时为 <see cref="Services.PaperdollService.SentinelMobId"/>。</summary>
        public string MobId { get; set; } = string.Empty;

        /// <summary>
        /// 显示名（V0.1.3）：纸娃娃用预设名（如「菲伦」），Mob/Npc 可用 WZ 名；
        /// 为空时列表回退「WZ 名 [id]」/ 纯 id。用于区分多只同类桌宠（否则全是「纸娃娃」）。
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>纸娃娃外观（EntityType=player 时有效）。</summary>
        public CharacterAppearance? Paperdoll { get; set; }

        /// <summary>窗口左上角屏幕坐标；<see cref="int.MinValue"/> = 未设置（首次启动按序号错开摆放）。</summary>
        public int WindowX { get; set; } = int.MinValue;

        public int WindowY { get; set; } = int.MinValue;

        /// <summary>该只宠物的渲染缩放（&lt;=0 = 用 1.0）。</summary>
        public double WindowScale { get; set; } = 1.0;

        /// <summary>主宠：挂地图背景 + 响应 Agent 状态特效；同一时刻有且仅有一只（由 ConfigService 保证）。</summary>
        public bool Primary { get; set; }
    }

    /// <summary>
    /// 宠物设置
    /// </summary>
    public class PetSettings
    {
        /// <summary>
        /// 桌宠实例列表（顺序 = 创建顺序，第一只默认主宠）。空 = 未迁移；
        /// ConfigService.LoadSettings 会从下面的单值字段迁移出第一只（不落盘，首次保存时写入）。
        /// </summary>
        public List<PetInstanceConfig> Pets { get; set; } = new List<PetInstanceConfig>();

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
        /// <summary>行走模式显示地图背景（胶水 2026-09-16：加开关可操作；关 = 只有人物在桌面走，无地图窗口）。默认开。</summary>
        public bool WalkShowMapBackground { get; set; } = true;
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

        // ── 表情开关（V0.2.x M2，见 docs/ai/PLAN-表情功能.md §4.3 / §4.4）──

        /// <summary>
        /// 自动眨眼（默认开）：待机时每 3~6s 随机播一次 blink（行走/攀爬中仍眨眼）。
        /// 对应右键菜单「表情 → 自动眨眼」勾选项，经 ConfigService.SetAutoBlink 落盘；
        /// 旧配置无该字段时反序列化保留此默认值 true。
        /// </summary>
        public bool AutoBlink { get; set; } = true;

        /// <summary>
        /// 随机表情（默认关）：待机时每 20~45s 从友好表情池随机播一个（行走/拖拽/Agent Run 中抑制）。
        /// 对应右键菜单「表情 → 随机表情」勾选项，经 ConfigService.SetRandomExpression 落盘。
        /// </summary>
        public bool RandomExpression { get; set; }

        // 纸娃娃当前搭配（CurrentMobId == "__paperdoll__" 时启动恢复）
        public CharacterAppearance? CurrentPaperdoll { get; set; }
        // 纸娃娃预设（搭配存档：预设名 → 外观）
        public Dictionary<string, CharacterAppearance> SavedPaperdolls { get; set; } = new Dictionary<string, CharacterAppearance>();
        // Agent 接入（Hermes / Claude Code / Codex 状态订阅 + 通用特效层）
        public AgentIntegrationConfig AgentIntegration { get; set; } = new AgentIntegrationConfig();
        /// 音乐模块设置（V0.2.0）：旧配置无 music 字段时反序列化安全落默认值 new MusicConfig()。
        public MusicConfig Music { get; set; } = new();
    }

    /// <summary>
    /// Agent 接入配置（PLAN-agent-integration §7）：
    /// 各 adapter 开关、轮询/静默/staleness 阈值、特效开关、WZ 庆祝节点路径（可手填覆盖）。
    /// 序列化沿用 PetSettings 的 CamelCase 策略（JsonNamingPolicy.CamelCase）。
    /// </summary>
    public class AgentIntegrationConfig
    {
        // Agent 集成总开关：false = 整块不加载（Aggregator / EffectLayer / 各 adapter 全部跳过创建）
        public bool Enabled { get; set; } = true;
        // adapter 开关（默认仅 Hermes 开）
        public bool HermesEnabled { get; set; } = true;
        public bool CCEnabled { get; set; }
        public bool CodexEnabled { get; set; }
        // 通用特效层（Jump 庆祝 / Failed 弹窗）
        public bool EffectEnabled { get; set; }

        // ── DSH（DeepSeek Harness）接入（RESEARCH-dsh-http-bridge.md §4.5）──

        /// <summary>DSH 接入开关（本机 loopback 免鉴权，仅 127.0.0.1；默认开——桌宠默认监控 DSH 执行状态，
        /// DSH 未运行时 adapter 静默退避重连，不刷屏不打扰）。</summary>
        public bool DshEnabled { get; set; } = true;

        /// <summary>DSH Host（默认 127.0.0.1，锁本机 loopback，不做跨机访问）。</summary>
        public string DshHost { get; set; } = "127.0.0.1";

        /// <summary>DSH Web 端口（默认 3080，dsh --profile web）。0 = 默认 3080。</summary>
        public int DshPort { get; set; } = 3080;

        /// <summary>DSH 会话工作区 cwd（可选；空 → MiniPet 进程当前目录）。</summary>
        public string DshWorkspace { get; set; } = string.Empty;

        // Hermes 接入 profile（默认空=默认 profile；state.db 路径按 ~/.hermes/[profiles/<p>/]state.db 解析，根尊重 HERMES_HOME）
        public string? HermesProfile { get; set; }

        // 多 profile 监控（2026-08-15）：分号分割，如 "default;work-dba;work-java"。空 = 仅 HermesProfile 单值。
        // 兼容：HermesProfiles 优先；两者都空 = 默认 profile。
        public string? HermesProfiles { get; set; }

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

        // ── Hermes api_server 连接（PLAN Rev3：MiniPet = Hermes 客户端 surface）──

        /// <summary>Hermes gateway/api_server 端口。0 = 自动发现（默认 8642）。</summary>
        public int HermesPort { get; set; }

        /// <summary>api_server 鉴权 API_SERVER_KEY（Bearer）。= 本地凭证，仅本机。</summary>
        public string HermesApiServerKey { get; set; } = string.Empty;

        /// <summary>AgentState → 动画反馈映射（用户可在设置页自定义）。null/缺省 → 用默认（见 <see cref="DefaultAgentStateFeedback"/>）。</summary>
        public Dictionary<AgentState, AgentStateFeedback>? AgentStateFeedback { get; set; }

        /// <summary>默认 7 态映射（对齐原 EffectLayerService switch）。</summary>
        public static Dictionary<AgentState, AgentStateFeedback> DefaultAgentStateFeedback()
        {
            return new Dictionary<AgentState, AgentStateFeedback>
            {
                [AgentState.Run] = new AgentStateFeedback { Action = "move" },
                [AgentState.Review] = new AgentStateFeedback { Balloon = "思考中…" },
                [AgentState.Wave] = new AgentStateFeedback { Balloon = "做完了 撒花 🎉" },
                [AgentState.Jump] = new AgentStateFeedback { Balloon = "搞定!" },
                [AgentState.Failed] = new AgentStateFeedback { Balloon = "出错了…" },
                [AgentState.Waiting] = new AgentStateFeedback { Balloon = "等你?" },
                [AgentState.Idle] = new AgentStateFeedback(),
            };
        }
    }

    /// <summary>
    /// 单个 AgentState 的反馈配置：Action（实体动作名，经 IActionDriver 播放）+ Balloon（气泡文本，可选）。
    /// </summary>
    public class AgentStateFeedback
    {
        /// <summary>实体动作名（如 "move" / "skill1"）；空 → 不切实体动作。</summary>
        public string? Action { get; set; }

        /// <summary>气泡文本；空 → 不弹气泡。</summary>
        public string? Balloon { get; set; }
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