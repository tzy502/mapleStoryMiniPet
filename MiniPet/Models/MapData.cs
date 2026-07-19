using System.Collections.Generic;

namespace MiniPet.Models
{
    /// <summary>
    /// 地图信息
    /// </summary>
    public class MapInfo
    {
        public int Id { get; set; }
        public MapLayer[] Layers { get; set; } = new MapLayer[8];
        public List<MapBack> Backs { get; set; } = new List<MapBack>();
        public int MinX { get; set; }
        public int MinY { get; set; }
        public int MaxX { get; set; }
        public int MaxY { get; set; }

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
        public bool Ani { get; set; }
        public int Type { get; set; }
        public object Resource { get; set; } = new object();
    }
}