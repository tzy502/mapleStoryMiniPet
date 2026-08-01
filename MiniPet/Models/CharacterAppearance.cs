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
        public EarType Ear { get; set; } = EarType.Human;
        public ItemInfo Hair { get; set; } = new ItemInfo();
        public ItemInfo Face { get; set; } = new ItemInfo();
        public ItemInfo? Cap { get; set; }
        public ItemInfo? Cape { get; set; }
        public ItemInfo? Coat { get; set; }
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

    /// <summary>耳朵类型（人类/精灵/高等精灵等，互斥渲染；默认人类）</summary>
    public enum EarType { Human, Elf, Lef, HighLef }

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