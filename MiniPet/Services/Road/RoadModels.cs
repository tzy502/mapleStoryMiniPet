using System;
using System.Collections.Generic;
using System.Linq;

namespace MiniPet.Services.Road
{
    // ═══════════════════════════════════════════
    // F18 无限之路 数据契约（集成者预置，各 agent 并行开发的公共地基；
    // 字段语义以 docs/ai/algorithm-infinite-road.md §0/§2/§3 为权威）
    // ═══════════════════════════════════════════

    /// <summary>
    /// 柱内一块 tile（对齐权威渲染语义：Dx/Dy = 该块「可见左上角」相对柱可见左缘/可见表面的像素偏移——
    /// 提取期已按 sprite origin 换算并剔除解码失败块，渲染期直接 (Dx,Dy) 贴图，不再碰 origin）。
    /// Ts = 该块所属 tileset（含 .img），图层混 tileset 时逐块生效。
    /// Z/Id = 源 WZ 值原样保留（Z = Map/Tile/{tS}.img/{u}/{no}/z 精灵帧 z，对齐 MapService.ParseTiles；
    /// Id = 源图层内序）——柱位图内 tile 与 obj 按 compositeZIndex(Z, Id)（Z 主 Id 次）混排绘制
    /// （权威同层混合排序口径：MapService.RenderViewport「(Z0,Z1) 升序」/ PIXI index.ts compositeZIndex(z, id)）。
    /// </summary>
    public sealed record RoadTile(int U, int No, int Dx, int Dy, string? Ts = null, int Z = 0, int Id = 0);

    /// <summary>
    /// 绑定到段的站地装饰（权威渲染锚点语义 2026-08-29：WZ obj (x,y) 即脚底锚点——MapRenderHandler.cs:505「obj 锚点 Y = 接地位置」）。
    /// Dx = o.X − 段首柱x（锚点水平相对段首），Dy = o.Y − 段表面（锚点垂直相对段表面）；
    /// 均**不含 origin**——渲染层自减（权威画法 drawImage(im, x−ox, y−oy)，MapRenderHandler.cs:929）。
    /// 渲染约定：画在 (段世界x + Dx − ox, 段世界SurfaceY + Dy − oy)。
    /// Z = 源 WZ obj z 原值（MapObj.Z）——柱位图内 obj 相对 tile 的遮挡按 Z（Z 低先画=后面、Z 高后画=前面）；
    /// 同 Z 按模板收集序（层序→层内序）稳定兜底。RoadObj 契约不带 Id（任务口径：obj 仅存 Z；Id 次键取模板序）。
    /// 仅「锚点落地」件入此（|o.Y − 段表面| ≤ 60；大块筛选：悬空装饰/浮台件丢弃）；
    /// .skel 骨骼 obj 一律排除——MapService 无骨骼渲染器，提取期计数诊断。
    /// </summary>
    public sealed record RoadObj(int Dx, int Dy, string OS, string L0, string L1, string L2, int Z = 0);

    /// <summary>
    /// 柱模板：从源图提取的可复用柱。SurfaceH = 模板内相对表面高度（入路时换算为世界 surfaceY）；
    /// BscPad = 底块为填充类可续堆；斜坡柱用 SlantDy 表达（null = 平地柱）。
    /// </summary>
    public sealed class ColumnTemplate
    {
        public int Id { get; init; }                  // 模板编号（池内唯一；位图缓存键=mapId+id+surfaceY）
        public string SourceMapId { get; init; } = "";     // 来源源图 id（跨图主题池重定基用）
        public int SourceSurfaceY { get; init; }      // 来源源图主干层表面 y（入带重定基基准）
        public int WidthCols { get; init; } = 1;      // 模板宽（柱数）：≥2 = 源图连续「路段切片」整体搬运（F18-5 obj 相对位置保真）
        public int SurfaceH { get; set; }             // 表面相对高度（源图坐标系，越小越高；提取期统一重定基到主表面——无限平地口径；重定基时 tiles/objs Dy 同步 += surfOrig−dominant 保段内容世界位置不变）
        public List<RoadTile> Tiles { get; init; } = new();  // builder 填充；随包发布后视为冻结（不突变）
        public List<RoadObj> Objs { get; init; } = new();    // 同上；渲染侧只读遍历
        public bool BscPad { get; init; }             // 底块 bsc 系 → 位图可循环堆到底
        public (int DyLeft, int DyRight)? SlantDy { get; init; } // 斜坡柱左右缘相对高度差；null = 平地
        public int Weight => Objs.Count > 0 ? 15 : 10; // 抽柱权重（绑定装饰 ×1.5，整数化 10/15）
    }

    /// <summary>台阶模板：相邻柱对/三柱组整体搬运（高差 ∈ (40,200]，x 差 90px）。</summary>
    public sealed class StepTemplate
    {
        public int Id { get; init; }
        public bool Up { get; init; }                 // 上行 / 下行
        public List<ColumnTemplate> Columns { get; init; } = new(); // 按路序（len=2 或 3）
        public int StartSurfaceH { get; init; }       // 起点柱表面（匹配 currentSurfaceY 用）
        public int EndSurfaceH { get; init; }         // 终点柱表面
    }

    /// <summary>后景视差 obj（远景剪影带，0.5× 慢速；.skel 排除；X/Y 为源图世界坐标，经 SourceAnchor 归一进路空间）。</summary>
    public sealed record FarObjEntry(int X, int Y, string OS, string L0, string L1, string L2, int W);

    /// <summary>源图 life 候选（NPC type=n / 怪 type=m；Dy = 相对柱表面）。</summary>
    public sealed record LifeCandidate(bool IsMob, string Id, int Dy);

    /// <summary>back 视差不可变记录（提取期从 MapBack 全字段克隆——MapBack/Sprite 公共 setter 会穿透冻结契约；渲染侧只读本记录）。</summary>
    public sealed record RoadBack(
        int X, int Y, int Rx, int Ry, int Cx, int Cy, int TileMode,
        string BS, string Type, string No, int Dx, int Dy,
        bool Front, int Alpha, bool ScreenMode, int SpineAni, int Fh);

    /// <summary>源图素材包（RoadMaterialExtractor 一次性产出；不可变，换源图整体替换）。</summary>
    public sealed class RoadMaterialPack
    {
        public string MapId { get; init; } = "";
        /// <summary>合并池贡献图 id 列表（主题池 ResolveMultiAsync 产出；单图包 = [MapId]——Sky/锚点取首包，可观测用）。</summary>
        public List<string> PoolMapIds { get; init; } = new();
        public string TileSetName { get; init; } = "";   // info/tS（含 .img）
        public List<ColumnTemplate> FlatColumns { get; init; } = new(); // 平地柱（含斜坡柱独立池见 SlantColumns）
        public List<ColumnTemplate> SlantColumns { get; init; } = new();
        public List<StepTemplate> Steps { get; init; } = new();
        public List<FarObjEntry> FarObjs { get; init; } = new();
        public List<LifeCandidate> LifeCandidates { get; init; } = new();
        public List<global::MiniPet.Models.MapBack> Backs { get; init; } = new();
        public int MainSurfaceH { get; init; }         // 主走廊表面（柱池频次最高者）
        public int AnchorX { get; init; }              // 源图坐标锚：路世界 x=0 对应的源图 x（back/远obj 归一基准）
        public int AnchorY { get; init; }              // 源图坐标锚：路 initialSurfaceY 对应的源图表面 y
        public double ContinuityRatio { get; init; }   // 连续率：相邻柱缘差 90±2 的比例（<0.6 判不可用——垂直塔图过滤）
        /// <summary>主干柱总数 = Σ max(1, WidthCols)（缺口不计——红线⑫口径，与生成器档位判定一致）。</summary>
        public int MainColumnTotal => FlatColumns.Sum(t => Math.Max(1, t.WidthCols));
        /// <summary>Usable（FINAL §0 唯一口径）：主干柱总数 ≥20 = 正常档；
        /// &lt;20 仍可激活（降级档素材照进——仲裁裁决链），柱=0 才彻底不可用。</summary>
        public bool Usable => MainColumnTotal >= RoadConstants.NormalPoolColumns;
        /// <summary>入合并池质量下限（FINAL：每图贡献 ≥5 柱才入合并池）。</summary>
        public bool MeetsMergeMin => MainColumnTotal >= RoadConstants.MinPoolColumns;
    }

    /// <summary>激活柱（入路即不可变；WorldX 为左缘世界坐标，覆盖 [WorldX, WorldX+90)）。</summary>
    public sealed class RoadColumn
    {
        public required ColumnTemplate Template { get; init; }
        public double WorldX { get; init; }            // 左缘（覆盖 [WorldX, WorldX+90×WidthCols)）
        public int WidthCols { get; init; } = 1;       // 模板宽（柱数）
        public int SurfaceY { get; init; }             // 世界坐标表面 y（段内各 90px 子区表面一致：段内高差≤40 由贴地直吸）
        public bool IsStepLerp { get; init; }          // 台阶过渡柱（表面函数走 30px 插值分支）
        public int StepFromY { get; init; }            // 台阶插值起点 y（IsStepLerp 时有效）
        public int StepToY { get; init; }              // 台阶插值终点 y
    }

    /// <summary>单条可视路的完整渲染载荷（不可变；Current=当前路，crossfade 期间 Incoming=进场路）。</summary>
    public sealed record RoadRenderRoad(
        int BuildGeneration,                           // 素材构建代际（柱位图缓存键组成）
        string MapId,
        string TileSetName,                            // info/tS（含 .img）
        IReadOnlyList<RoadColumn> Columns,             // 激活柱（发布时深拷贝）
        IReadOnlyList<RoadEntityState> Entities,       // 实体（含帧号，见 RoadEntityState）
        IReadOnlyList<FarObjEntry> FarObjs,            // 远景剪影带（源图坐标，经 SourceAnchor 进路空间）
        IReadOnlyList<RoadDecor> Decors,               // 非主干组贴回装饰（块相对坐标→激活时换算世界偏移）
        double ScrollOriginX,                          // 本路自己的 scroll 原点（crossfade 双原点）
        int InitialSurfaceY,                           // 垂直映射基准（62% 锚归一化用）
        int SourceAnchorX,                             // 坐标锚：路世界 x=0 对应源图世界 x（back/远obj 归一化基准）
        int SourceAnchorY);                            // 垂直锚：路 initialSurfaceY 对应源图表面 y

    /// <summary>
    /// 不可变帧快照（每逻辑 tick 发布一次并合并触发渲染 kick；渲染侧只读）：
    /// Current 路 + 可选 Incoming 进场路（至多一条，更新的换歌取消上一条）+ crossfade 代际/alpha + 动画 delta。
    /// 跨线程唯一交互面——渲染器不得读控制器/素材任何可变态。
    /// </summary>
    /// <summary>固定天空层（拍板 #3）：首次激活 pack 的 back 冻结副本 + 自有锚点/相机；换歌不变。</summary>
    public sealed record RoadSky(
        IReadOnlyList<RoadBack> BacksNonFront,
        IReadOnlyList<RoadBack> BacksFront,
        int SourceAnchorX, int SourceAnchorY,          // 首次激活 pack 锚点（冻结）
        int AnchorSurfaceY,                            // 首次激活路 InitialSurfaceY（天空垂直视差 camY 恒定——禁用当前路值）
        double CamX);                                  // 天空水平相机：控制器 delta 传播；换歌不重置

    public sealed record RoadFrame(
        RoadPhase Phase,
        bool IsPaused,
        RoadSky? Sky,                                  // null = 未激活/Building（渲染器跳过天空）；Sky 不参与 crossfade
        RoadRenderRoad Current,
        RoadRenderRoad? Incoming,
        int CrossfadeGeneration,
        double CrossfadeAlpha,                         // 0=纯 Current，1=已切 Incoming
        double RoadClockMs,                            // 单调路时钟（一切流逝型视觉效果——back 自动滚动 viewTimeMs/远obj——一律取此绝对值，禁止对渲染到的快照累加 delta）
        double AnimDeltaMs);

    /// <summary>模式3 运行态（可观测性锚，UI 状态栏/测试断言用）。</summary>
    public sealed class RoadState
    {
        public RoadPhase Phase { get; set; } = RoadPhase.Inactive;
        public string SelectedMapId { get; set; } = "";
        public string FallbackReason { get; set; } = "";
        public int BuildGeneration { get; set; }
        public int ColumnCount { get; set; }
        public int SkelExcluded { get; set; }
        public string LastError { get; set; } = "";
        public bool CombatDisabled { get; set; }          // Mob/NPC 宠物（非纸娃娃）时战斗关闭
        public int WeaponActionUnavailable { get; set; }  // 因武器无帧被取消的遭遇计数
        public double ScanProgress { get; set; }          // 0~1 共享索引扫描进度
        public bool ScanComplete { get; set; }
        public int EntityCacheCount { get; set; }         // 路属条带缓存当前条数
        public int EntityCacheHits { get; set; }
        public int EntityCacheMisses { get; set; }
        public int EntityCacheEvictions { get; set; }
        public bool DegradedPool { get; set; }            // 降级档标记（FINAL W17：合并池主干柱 <20 → 防重关、路可用）
    }

    /// <summary>crossfade 进度态（old 路与新进场路的混合参数；进场路至多一条——更新的换歌取消上一条）。</summary>
    public sealed record RoadCrossfadeState(double Alpha, bool IncomingIsNew);

    /// <summary>非主干组贴回装饰（背景视觉；不可走）；画在柱后、实体前；前层 obj → Entities 后。</summary>
    public sealed record RoadDecor(int Dx, int Dy, bool IsObj,
        string? Ts, int U, int No,
        string? OS, string? L0, string? L1, string? L2);

    /// <summary>路上实体渲染态（NPC/怪；战斗导演产生，随帧快照发布；MapId = 生成时所属源图——换歌不断代后旧实体按自己的图解析帧）。</summary>
    public sealed record RoadEntityState(bool IsMob, string LifeId, string MapId, double WorldX, int FeetY, string Action, int FrameIndex, double Alpha, bool FacingLeft);

    /// <summary>模式3 生命周期相位（单向 Building→Entering→Running→Exiting→Inactive；**暂停不是相位**——IsPaused/RoadClockScale 叠加标志，任何相位可暂停可恢复）。</summary>
    public enum RoadPhase { Inactive, Building, Entering, Running, Exiting }

    // ── 运行时接口（跨 agent 消费契约；实现方：Generator/Renderer/Director） ──

    /// <summary>表面函数提供者（PetFollower/CombatDirector 消费；算法 §3.4）。</summary>
    public interface IRoadSurfaceProvider
    {
        /// <summary>世界 x 处地面顶面 y；无覆盖（理论不该发生）返回 null（调用方保持上一帧）。</summary>
        double? SurfaceY(double worldX);
        /// <summary>当前窗口左缘世界 x（scrollX）。</summary>
        double ScrollX { get; }
    }

    /// <summary>柱序列同步快照查询（仅 PetWindow 逻辑 tick 内消费；生成推进只发生在 EndTick，无跨线程事件）。</summary>
    public interface IRoadColumnSource
    {
        /// <summary>激活柱只读视图（窗口内 + 前方预生成）。</summary>
        IReadOnlyList<RoadColumn> ActiveColumns { get; }
    }
}
