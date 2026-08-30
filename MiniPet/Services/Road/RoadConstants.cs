namespace MiniPet.Services.Road
{
    /// <summary>
    /// F18 无限之路参数总表（算法文档 §9 权威，禁止散落魔法数）。
    /// 数据实证：/tmp/wmsound 探针（2026-08-27，射手村 100000000）。
    /// </summary>
    public static class RoadConstants
    {
        /// <summary>柱宽（全库 401 图采样 90px 间距无一例外）。</summary>
        public const int ColW = 90;
        /// <summary>滚动速度（固定常速；2026-08-29 用户实测 60 太慢，100 匹配 walk1 步频观感）。</summary>
        public const double ScrollSpeed = 100.0;
        /// <summary>台阶判定阈值（高差 > 此值触发 jump）。</summary>
        public const int StepThresh = 40;
        /// <summary>台阶模板高度差范围。</summary>
        public const int StepMin = 40;
        public const int StepMax = 200;
        /// <summary>每柱上台阶概率。</summary>
        public const double PStep = 0.12;
        /// <summary>连续同向台阶游程上限（≥ 强制反向）。</summary>
        public const int RunLimit = 3;
        /// <summary>屏幕中带上下保护边距（禁止继续上行/下行）。</summary>
        public const int BandMargin = 120;
        /// <summary>平地柱表面容差。</summary>
        public const int FlatTol = 4;
        /// <summary>防重复队列长度。</summary>
        public const int RecentN = 8;
        /// <summary>前方预生成柱数。</summary>
        public const int PregenCols = 12;
        /// <summary>obj 站地判定容差。</summary>
        public const int ObjGroundTol = 60;
        /// <summary>装饰带 obj 高度上限（> 站地带且 ≤ 此值，水平在柱 ±45px 内也绑柱）。</summary>
        public const int ObjDecorBand = 250;
        /// <summary>装饰带 obj 水平锚定半径。</summary>
        public const int ObjDecorHalfW = 45;
        /// <summary>后景 obj 距地面阈值（dy < -此值且宽 >300 归视差带）。</summary>
        public const int ObjFarThresh = 200;
        /// <summary>后景 obj 宽度阈值。</summary>
        public const int ObjFarMinW = 300;
        /// <summary>NPC 每柱生成概率（1/10）。</summary>
        public const double NpcP = 1.0 / 10;
        /// <summary>同屏 NPC 上限。</summary>
        public const int NpcMaxScreen = 3;
        /// <summary>NPC 生成冷却（柱数）。</summary>
        public const int NpcCooldown = 6;
        /// <summary>怪物每柱生成概率（1/6）。</summary>
        public const double MobP = 1.0 / 6;
        /// <summary>同屏怪物上限。</summary>
        public const int MobMaxScreen = 2;
        /// <summary>怪物生成冷却（柱数）。</summary>
        public const int MobCooldown = 8;
        /// <summary>实体生成防冲突半径（柱 x± 此值内无站地 obj/其他实体）。</summary>
        public const int EntityClearRadius = 60;
        /// <summary>攻击触发距离（怪物世界 x 距人物世界 x）。</summary>
        public const int AttackRange = 70;
        /// <summary>攻击动作命中帧位置（进度比例，触发怪 hit1）。</summary>
        public const double HitFrameRatio = 0.5;
        /// <summary>die1 播完停帧展示时长。</summary>
        public const int DieHoldMs = 500;
        /// <summary>死亡淡出时长。</summary>
        public const int DieFadeMs = 300;
        /// <summary>hit1 受击时长（WZ 帧数据实读为准，此为编排兜底）。</summary>
        public const int HitFallbackMs = 300;
        /// <summary>台阶跳跃时长。</summary>
        public const int JumpMs = 300;
        /// <summary>地图切换淡入淡出时长。</summary>
        public const int CrossfadeMs = 500;
        /// <summary>台阶表面插值带宽（仅影响人物脚 y）。</summary>
        public const int SurfLerpW = 30;
        /// <summary>柱位图 LRU 上限。</summary>
        public const int BscCacheMax = 128;
        /// <summary>柱位图固定源宽（px，= ColW）。</summary>
        public const int ColumnBmpW = 90;
        /// <summary>柱位图固定源高（px，与窗口高无关，blit 时缩放）。</summary>
        public const int ColumnBmpH = 900;
        /// <summary>柱左缘出屏回收余量（scrollX - 柱右缘 > 此值 → 回收）。</summary>
        public const int RecycleMargin = 200;
        /// <summary>有效柱池下限（低于此模式3 不可用）。</summary>
        public const int MinPoolColumns = 5;
        /// <summary>正常档合并池主干柱总数下限（≥20 = 正常档洗牌防重开启；&lt;20 = 降级档 recent 关闭——FINAL §0 Usable 唯一口径）。</summary>
        public const int NormalPoolColumns = 20;
        /// <summary>主题池候选试提取上限（FINAL §三：逐个 tryExtract ≤40 试，收集全部合格包非首个即停）。</summary>
        public const int MaxResolveTries = 40;
        /// <summary>初始平地柱数量范围（铺满窗口+前方）。</summary>
        public const int InitFillMin = 6;
        public const int InitFillMax = 10;
        /// <summary>初始滚动速度下窗口兜底宽（窗口未就绪时预估用）。</summary>
        public const int FallbackWindowW = 800;
        /// <summary>人物贴地校准像素（b3fd 实测：公式方向 正=下移——PetFollower:147 feetScreenY−petH+Calib；纸娃娃帧底 vs 草线 2px → +2）。</summary>
        public const int GroundCalibPx = 2;
        /// <summary>人物（纸娃娃）并入实体管线时的 LifeId 哨兵（渲染器 DrawEntities 经 EntityFrameResolver 分流走 PaperdollService.RenderFrame）。</summary>
        public const string PaperdollLifeId = "__paperdoll__";
        /// <summary>路属实体条带缓存 LRU 上限（条；RoadState 暴露命中/淘汰计数）。</summary>
        public const int StripCacheMax = 64;
        // ── v13 提取器常量（权威 = docs/ai/algorithm-infinite-road-FINAL.md §一/§二/§0） ──
        /// <summary>链判定相邻列 x 差下限（全库 401 图采样 90px 间距 ±2）。</summary>
        public const int ChainGapMin = 88;
        /// <summary>链判定相邻列 x 差上限。</summary>
        public const int ChainGapMax = 92;
        /// <summary>链缺口同链 x 差上限（∈(92,272] 同链克隆补齐；> 此值 = 链边界如实断开——红线⑪）。</summary>
        public const int ChainGapFillMax = 272;
        /// <summary>链内相邻列 colTop 容差。</summary>
        public const int ChainTopTol = 8;
        /// <summary>组并入容差（与组代表单层判定——红线⑮禁链式传递）。</summary>
        public const int GroupJoinTol = 8;
        /// <summary>life.Fh 权威归属容差（实测系统差 6~15px——W8/W9）。</summary>
        public const int LifeFhTol = 20;
        /// <summary>foothold 加分长度资格下限（逐根累加按此归一；无 cap——红线⑬⑭）。</summary>
        public const int FootholdMinLen = 270;
        /// <summary>主干组连续率下限（作用域 = 主干组柱集；&lt;0.6 整图 Usable 拒绝）。</summary>
        public const double MainContinuityMin = 0.6;
        /// <summary>块宽软上限（柱；obj 右缘对齐优先可推宽——红线⑧禁硬切 obj）。</summary>
        public const int BlockSoftMaxCols = 8;
        /// <summary>obj 保守区间右缘修正余量（块边界右移至 obj 右缘+10）。</summary>
        public const int BlockEdgeMargin = 10;
        /// <summary>Decor 前层 obj z 阈值（WZ z ≥ 此值 = 源图前层语义，画人物后遮挡——Entities 之后）。</summary>
        public const int DecorFrontZ = 8;
        /// <summary>Decor 路级缓存 LRU 上限（键 mapId；红线⑥禁无上限）。</summary>
        public const int DecorCacheMax = 16;
        /// <summary>五城镇回退 mapId（town=1 已终验：射手/密林/勇士/废都/林中）。</summary>
        public static readonly string[] TownFallbackMaps =
        {
            "100000000", "101000000", "102000000", "103000000", "105000000"
        };
    }
}
