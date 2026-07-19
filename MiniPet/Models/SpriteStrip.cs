using System.Collections.Generic;

namespace MiniPet.Models
{
    /// <summary>
    /// 精灵图条
    /// </summary>
    public class SpriteStrip
    {
        public string Action { get; set; } = string.Empty;
        public int FrameWidth { get; set; }
        public int FrameHeight { get; set; }
        public int TotalWidth { get; set; }
        public int FrameCount { get; set; }
        public int OriginX { get; set; }
        public int OriginY { get; set; }
    }

    /// <summary>
    /// 帧数据
    /// </summary>
    public class FrameData
    {
        public int Index { get; set; }
        public int Delay { get; set; }
        public int A0 { get; set; }
        public int A1 { get; set; }
    }

    /// <summary>
    /// 宠物配置
    /// </summary>
    public class PetConfig
    {
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public Dictionary<string, SpriteStrip> Sprites { get; set; } = new Dictionary<string, SpriteStrip>();
        public int? OriginX { get; set; }
        public int? OriginY { get; set; }
    }

    /// <summary>
    /// 动画定义
    /// </summary>
    public class AnimationDef
    {
        public string Name { get; set; } = string.Empty;
        public bool IsLoop { get; set; }
        public string? Fallback { get; set; }
        public List<string> DefaultOrder { get; set; } = new List<string>();
    }
}