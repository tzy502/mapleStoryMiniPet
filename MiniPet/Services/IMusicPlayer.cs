using System;

namespace MiniPet.Services
{
    /// <summary>
    /// 预备流句柄（两阶段交换协议第一步的产物）：Prepare 建好并校验过、尚未提升为活动流的音频流。
    /// 用于「新流验证通过前旧播放毫发无损」的换曲场景（场景联动预检）。
    /// 一次性：Commit 成功或 Dispose 丢弃后句柄即失效，不可复用。
    /// </summary>
    public interface IPreparedStream : IDisposable
    {
        /// <summary>
        /// 校验流可用（如能取到有效 Duration）。已提交/已丢弃/校验失败返回 false。
        /// 可多次调用，不影响当前播放。
        /// </summary>
        bool Validate();

        /// <summary>
        /// 提交：把预备流原子提升为活动流并起播（内部单个信号量临界区内完成旧流释放与新流起播）。
        /// 失败（提交段任一 BASS 调用失败）返回 false，新旧资源确定性释放、触发 PlaybackError。
        /// </summary>
        bool Commit();
    }

    /// <summary>
    /// 音频播放抽象（ADR-0007：ManagedBass 单实现双平台，实现见 BassMusicPlayer）。
    /// ⚠️ PlaybackEnded / PlaybackError 可能从 BASS 回调线程触发，订阅方需自行 Dispatcher.UIThread.Post 切回 UI 线程。
    /// </summary>
    public interface IMusicPlayer : IDisposable
    {
        /// <summary>是否正在播放（活动流存在且处于播放态）。</summary>
        bool IsPlaying { get; }

        /// <summary>是否暂停（活动流存在且处于暂停态）。</summary>
        bool IsPaused { get; }

        /// <summary>音量 0.0-1.0（实现层缓存 + 应用到当前活动流）。</summary>
        double Volume { get; set; }

        /// <summary>当前播放位置；Stopped（无活动流）状态允许 null。</summary>
        TimeSpan? Position { get; }

        /// <summary>总时长；Stopped（无活动流）状态允许 null。</summary>
        TimeSpan? Duration { get; }

        /// <summary>
        /// 两阶段交换协议第一步：pin 音频数据 → 创建临时流并校验。旧播放完全不受影响。
        /// 失败（数据损坏/解码失败/设备未初始化）返回 null 并触发 PlaybackError，活动流分毫未动。
        /// </summary>
        /// <param name="mp3Bytes">WZ 提取的完整 MP3 字节流（调用后归播放器持有直至流释放）。</param>
        IPreparedStream? Prepare(byte[] mp3Bytes);

        /// <summary>播放 MP3 字节流（Prepare + Commit 的便利方法，失败静默返回——错误经 PlaybackError 事件上报）。</summary>
        void Play(byte[] mp3Bytes);

        /// <summary>暂停当前播放（无活动流时无操作）。</summary>
        void Pause();

        /// <summary>恢复暂停的播放（无活动流/未暂停时无操作）。</summary>
        void Resume();

        /// <summary>停止并释放当前活动流（之后 Position/Duration 为 null）。</summary>
        void Stop();

        /// <summary>跳转播放位置（无活动流时无操作；超出时长时由实现 clamp）。</summary>
        void Seek(TimeSpan pos);

        /// <summary>自然播完回调（⚠️ 可能从 BASS 回调线程触发，订阅方自行切换 UI 线程）。</summary>
        event Action? PlaybackEnded;

        /// <summary>播放错误回调（携带可读错误串；⚠️ 可能从 BASS 回调线程或调用线程触发）。</summary>
        event Action<string>? PlaybackError;
    }
}
