using System.Collections.Generic;

namespace MiniPet.Models
{
    /// <summary>
    /// 音乐模块设置（V0.2.0，F15-F17）：挂 PetSettings.Music，沿用 PetSettings 的
    /// CamelCase JSON 序列化。旧配置文件无 music 字段时，反序列化落在属性初始化器的默认值上
    ///（System.Text.Json 不触碰 JSON 里不存在的属性），安全兼容。
    /// </summary>
    public class MusicConfig
    {
        /// <summary>音量（0-100，默认 70）。</summary>
        public int Volume { get; set; } = 70;

        /// <summary>播放模式（顺序 / 单曲循环 / 随机），默认顺序。</summary>
        public PlaybackMode PlaybackMode { get; set; } = PlaybackMode.Sequential;

        /// <summary>上次播放曲目 Key（"Bgm00.img/SleepyWood"），启动恢复预选用；从未播放过为 null。</summary>
        public string? LastTrackKey { get; set; }

        /// <summary>场景联动开关（F17：切地图背景时自动改播该地图 BGM），默认关。</summary>
        public bool SceneLinkEnabled { get; set; }

        /// <summary>
        /// 本地音频库根目录（V0.2.1）；空串 = 未启用本地源。UI 选择器的路径决策归 UI 层
        /// （默认建议 ~/Music），数据层只负责持久化承载与目录服务内存态的联动。
        /// </summary>
        public string LocalMusicPath { get; set; } = "";

        /// <summary>迷你悬浮控制条开关（V0.2.1），默认开。展示位置/样式归 UI 层，数据层只存。</summary>
        public bool MiniBarEnabled { get; set; } = true;

        /// <summary>
        /// 最近播放曲目 Key 列表。写入契约：move-to-front 去重，上限 20 条
        /// （由写入方在 ConfigService.Update 事务内维护，本字段只承载持久化状态）。
        /// </summary>
        public List<string> PlayHistory { get; set; } = new();
    }
}
