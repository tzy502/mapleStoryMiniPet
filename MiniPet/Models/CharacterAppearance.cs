using System.Collections.Generic;

namespace MiniPet.Models
{
    /// <summary>
    /// 纸娃娃外观
    /// </summary>
    public class CharacterAppearance
    {
        public int Gender { get; set; }
        public int Skin { get; set; }
        /// <summary>皮肤 = body item id（2000–2999，head=body+10000）。0 时按 Gender 回落 2000/2001。</summary>
        public int BodyId { get; set; }
        /// <summary>耳朵类型 = head WZ canvas 名（humanEar/ear/lefEar/highlefEar，对齐 MapleSalon2 CharacterEarType）。</summary>
        public string Ear { get; set; } = "humanEar";
        public ItemInfo Hair { get; set; } = new ItemInfo();
        public ItemInfo Face { get; set; } = new ItemInfo();
        public ItemInfo? Cap { get; set; }
        public ItemInfo? Cape { get; set; }
        public ItemInfo? Coat { get; set; }
        /// <summary>105xxxx 长袍/套服（与 Coat/Pants 冲突，选穿互斥）。</summary>
        public ItemInfo? Overall { get; set; }
        public ItemInfo? Pants { get; set; }
        public ItemInfo? Shoes { get; set; }
        public ItemInfo? Weapon { get; set; }
        public ItemInfo? Shield { get; set; }
        public ItemInfo? Glove { get; set; }
        public ItemInfo? Earring { get; set; }
        public MountInfo? Mount { get; set; }
        public ChairInfo? Chair { get; set; }
        public int DyeHue { get; set; }
        public bool DyeEnabled { get; set; }
    }

    /// <summary>
    /// 装备信息
    /// </summary>
    public class ItemInfo
    {
        public string Id { get; set; } = string.Empty;
        public int EnableEffect { get; set; }
        public bool Visible { get; set; }
        public int Dye { get; set; }
        public int Hue { get; set; }
        public int Saturation { get; set; }
        public int Brightness { get; set; }
    }

    /// <summary>
    /// 骑乘信息
    /// </summary>
    public class MountInfo
    {
        public string Id { get; set; } = string.Empty;
        public Dictionary<string, FrameAnimate> Actions { get; set; } = new Dictionary<string, FrameAnimate>();
        public string SitAction { get; set; } = string.Empty;
        public bool HideBody { get; set; }
        public bool HideWeapon { get; set; }
        public bool HideCape { get; set; }
    }

    /// <summary>
    /// 椅子信息
    /// </summary>
    public class ChairInfo
    {
        public string Id { get; set; } = string.Empty;
        public bool IsCash { get; set; }
        public int BodyRelMove { get; set; }
        public string SitAction { get; set; } = string.Empty;
        public string SitEmotion { get; set; } = string.Empty;
        public bool HideBody { get; set; }
        public bool HideWeapon { get; set; }
        public bool HideCape { get; set; }
    }
}