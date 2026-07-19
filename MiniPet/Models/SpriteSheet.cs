using System.Collections.Generic;

namespace MiniPet.Models
{
    /// <summary>
    /// 精灵图基础模型
    /// </summary>
    public class Sprite
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int OriginX { get; set; }
        public int OriginY { get; set; }
        public int Z { get; set; }
        public string ResourceUrl { get; set; } = string.Empty;
    }

    /// <summary>
    /// 帧数据
    /// </summary>
    public class Frame : Sprite
    {
        public int Delay { get; set; }
        public int A0 { get; set; }
        public int A1 { get; set; }
    }

    /// <summary>
    /// 帧动画
    /// </summary>
    public class FrameAnimate
    {
        public List<Frame> Frames { get; set; } = new List<Frame>();
    }
}