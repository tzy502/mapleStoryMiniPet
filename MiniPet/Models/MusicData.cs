using System;

namespace MiniPet.Models
{
    /// <summary>
    /// 一首曲目（音乐模块 V0.2.0，F15-F17）：对应 Sound/BgmXX.img 下的一个 Wz_Sound 子节点。
    /// </summary>
    /// <param name="Img">WZ 分类文件名，如 "Bgm00.img"（音乐库目录枚举自 Sound/Bgm*.img）。</param>
    /// <param name="Track">曲名，如 "SleepyWood"（= img 内子节点 Text）。</param>
    /// <param name="Ms">时长毫秒（WZ 元数据）；场景曲目无元数据时填 0（Display 省略时长后缀）。</param>
    /// <param name="Channels">声道数。</param>
    /// <param name="Frequency">采样率 Hz。</param>
    public record MusicTrack(
        string Img,
        string Track,
        int Ms,
        int Channels,
        int Frequency
    )
    {
        /// <summary>
        /// 列表显示名 = "Track (mm:ss)"；Ms&lt;=0（场景曲目无时长元数据）时省略时长后缀，只显示曲名。
        /// </summary>
        public string Display => Ms > 0
            ? $"{Track} ({TimeSpan.FromMilliseconds(Ms):mm\\:ss})"
            : Track;

        /// <summary>全局唯一标识 = "Img/Track"（如 "Bgm00.img/SleepyWood"），持久化 LastTrackKey / PlayHistory 用。</summary>
        public string Key => $"{Img}/{Track}";
    }

    /// <summary>
    /// 场景曲目引用（mapId → 曲目）：由地图 bgm 字段指向（如 "Bgm58/xxx" 归一化为 Img="Bgm58.img"）。
    /// 只有归属信息（分类+曲名），没有时长等元数据——时长以播放器实际打开流后报告的值为准。
    /// </summary>
    /// <param name="Img">WZ 分类文件名，如 "Bgm58.img"。</param>
    /// <param name="Track">曲名。</param>
    public record MusicTrackRef(string Img, string Track);

    /// <summary>播放器状态（音乐模块 V0.2.0）。</summary>
    public enum PlaybackState
    {
        /// <summary>停止（无活动流）。</summary>
        Stopped,
        /// <summary>播放中。</summary>
        Playing,
        /// <summary>暂停（流保留，可恢复）。</summary>
        Paused
    }

    /// <summary>
    /// 播放模式（队列耗尽后的行为策略）。
    /// 命名刻意避开动画系统 AnimService 已有的 PlayMode（Loop/OneShot）——两者语义无关，见 CONTEXT.md 术语表。
    /// </summary>
    public enum PlaybackMode
    {
        /// <summary>顺序播放（队列尾回卷到队首，持续循环）。</summary>
        Sequential,
        /// <summary>单曲循环。</summary>
        RepeatOne,
        /// <summary>随机播放。</summary>
        Random
    }
}
