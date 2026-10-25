using System;
using System.Collections.Generic;

namespace MiniPet.Models
{
    /// <summary>
    /// 地图信息
    /// </summary>
    public class MapInfo
    {
        public string Id { get; set; } = string.Empty;
        public MapLayer[] Layers { get; set; } = new MapLayer[8];
        public List<MapBack> Backs { get; set; } = new List<MapBack>();
        public List<MapFoothold> Footholds { get; set; } = new List<MapFoothold>();
        // life/portal 在 mapRoot 解析（扁平列表），渲染时按 Java handler 顺序（life 在层后、portal 在 front back 后）。
        public List<MapLife> Lifes { get; set; } = new List<MapLife>();
        public List<MapPortal> Portals { get; set; } = new List<MapPortal>();
        /// <summary>梯子 / 绳索（F18 行走攀爬用，见 LadderRope）。</summary>
        public List<LadderRope> Ropes { get; set; } = new List<LadderRope>();
        public int MinX { get; set; }
        public int MinY { get; set; }
        public int MaxX { get; set; }
        public int MaxY { get; set; }
        public int VRLeft { get; set; }
        public int VRTop { get; set; }
        public int VRRight { get; set; }
        public int VRBottom { get; set; }

        public MapInfo()
        {
            for (int i = 0; i < Layers.Length; i++)
            {
                Layers[i] = new MapLayer();
            }
        }
    }

    /// <summary>
    /// 地图层
    /// </summary>
    public class MapLayer
    {
        public List<MapTile> Tiles { get; set; } = new List<MapTile>();
        public List<MapObj> Objs { get; set; } = new List<MapObj>();
    }

    /// <summary>
    /// 地图瓦片
    /// </summary>
    public class MapTile
    {
        public int Id { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Z { get; set; }
        public Sprite Resource { get; set; } = new Sprite();
    }

    /// <summary>
    /// 地图对象
    /// </summary>
    public class MapObj
    {
        public int Id { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Z { get; set; }
        public bool FlipX { get; set; }
        public FrameAnimate Resource { get; set; } = new FrameAnimate();
    }

    /// <summary>
    /// 地图背景
    /// </summary>
    public class MapBack
    {
        public int Id { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Cx { get; set; }
        public int Cy { get; set; }
        public int Rx { get; set; }
        public int Ry { get; set; }
        public int Alpha { get; set; }
        public bool FlipX { get; set; }
        public bool Front { get; set; }
        public int Ani { get; set; }
        public int SpineNo { get; set; }
        public int Type { get; set; }
        public int ScreenMode { get; set; }
        public Sprite Resource { get; set; } = new Sprite();
    }

    /// <summary>
    /// 地面/平台线段（foothold）— y1/y2 为平台顶面世界 y
    /// Layer：foothold 树第一层索引（WZ foothold/{l0}/{l1}/{l2} 的 l0；0=地面层，1+ = 平台/屋顶），
    /// 供 GetGroundY 按地面层取落地 y（M7）。
    /// </summary>
    public class MapFoothold
    {
        public int Id { get; set; }
        public int Layer { get; set; }
        public int X1 { get; set; }
        public int Y1 { get; set; }
        public int X2 { get; set; }
        public int Y2 { get; set; }
        public int Prev { get; set; }
        public int Next { get; set; }
    }

    /// <summary>
    /// 地图生命（NPC/Mob）— 字段对齐 WzComparerR2 LifeItem.LoadFromNode。
    /// 渲染定位用 (X, Cy)（Cy 为脚底接地 y，缺失回退 Y）；资源取首动作帧 0。
    /// </summary>
    public class MapLife
    {
        public int Id { get; set; }
        public string Type { get; set; } = "n"; // "m"=Mob, "n"=Npc
        public int X { get; set; }
        public int Y { get; set; }
        public int Cy { get; set; }
        public int Fh { get; set; }
        public bool Flip { get; set; }
        public bool Hide { get; set; }
        /// <summary>
        /// 渲染 z（WZ `life/{i}/z`）——**参与层内 (z, id) 排序**，对齐参考实现
        /// （mapRederWeb `index.ts:595` `compositeZIndex(maplife.z, maplife.id)`）。
        /// 之前项目没有该字段 → life 只能画在所有 obj/tile 之上，遮挡关系与游戏不符。
        /// </summary>
        public int Z { get; set; }
        public Sprite Resource { get; set; } = new Sprite();
    }

    /// <summary>
    /// 地图传送门 — 字段对齐 WzComparerR2 PortalItem.LoadFromNode。
    /// 渲染定位用 (X, Y)；资源取 game 动画帧 0（Map/MapHelper.img/portal/game/{type}/{img}）。
    /// </summary>
    public class MapPortal
    {
        public int Id { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Type { get; set; } // pt
        public string PName { get; set; } = string.Empty;
        public int Image { get; set; }
        public Sprite Resource { get; set; } = new Sprite();
        /// <summary>目标地图 id（tm；空 / 999999999 / -1 = 无目标，不能跨图）。F18 选传送门用。</summary>
        public string Tm { get; set; } = string.Empty;
        /// <summary>目标传送门名（tn）。</summary>
        public string Tn { get; set; } = string.Empty;
        /// <summary>是否可跨图（tm 有效）。</summary>
        public bool HasTarget => !string.IsNullOrEmpty(Tm) && Tm != "999999999" && Tm != "-1";
    }

    /// <summary>
    /// 梯子 / 绳索 —— WZ `Map/Map/MapX/{mapId}.img/ladderRope/{i}`：x / y1 / y2 / l。
    /// 对齐 sdlMS game_ladderrope（l=1 梯子，0=绳）。
    /// </summary>
    public class LadderRope
    {
        public int Id { get; set; }
        public int X { get; set; }
        public int Y1 { get; set; }
        public int Y2 { get; set; }
        public bool IsLadder { get; set; }
        /// <summary>上端 y（较小者）。</summary>
        public int Top => Math.Min(Y1, Y2);
        /// <summary>下端 y（较大者）。</summary>
        public int Bottom => Math.Max(Y1, Y2);
    }
}