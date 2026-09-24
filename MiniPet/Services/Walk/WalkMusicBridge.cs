using System;
using System.Threading;
using Avalonia.Threading;
using MiniPet.Models;

namespace MiniPet.Services.Walk
{
    /// <summary>
    /// 音乐 ↔ 行走模式桥（F18 §1「触发与生命周期」；行走侧物理对齐 sdlMS
    /// `character_logic_system` / `physic::walk`，本类只负责把「BGM 播放态」翻译成行走状态机调用）：
    ///   · 播放（含切歌换曲 / 场景联动换曲）→ WalkModeService.Start(曲目 key)；
    ///   · 曲目播完后的自动下一首 → 先 OnTrackFinished()（停步 + 淡出淡入切下一张地图）再 Start(新 key)；
    ///   · Stop（不含 Pause）→ Stop()；Pause → Pause()；Resume → Resume()；
    ///   · 静音轨 Bgm00/Silence 与无法归一化的曲目 → 不进入行走模式。
    /// ⚠️ MusicPlayerService 的事件可能来自 BASS 回调线程（见其类注释「订阅方自行 Dispatcher.UIThread.Post」）：
    ///   ① 事件现场只做「读状态 + 决策 + 快照」，不直接触碰行走状态机；
    ///   ② 实际调用统一 Dispatcher.UIThread.Post 切回 UI 线程（与 SettingsWindow.Music / MusicMiniBarWindow /
    ///      PetWindow 的既有订阅口径一致），投递顺序即事件到达顺序；
    ///   ③ 现场与投递体两侧都 try/catch，回调绝不外抛异常。
    /// </summary>
    public sealed class WalkMusicBridge : IDisposable
    {
        // 事件委托必须持有字段引用才能在 Dispose 里退订（匿名 lambda 无法退订）
        private readonly Action<MusicTrack?> _onCurrentChanged;
        private readonly Action<MusicTrack?> _onTrackEnded;
        private readonly Action<PlaybackState> _onStateChanged;

        private readonly WalkModeService _walk;
        private readonly MusicPlayerService _music;

        // 桥自身状态：事件可能来自任意线程，读改写统一用 _lock 串行化（绝不与行走侧持锁交叉调用）
        private readonly object _lock = new();
        // 行走模式是否已由本桥启动（Pause 期间保持 true——暂停是行走会话内的子状态；Stop/静音轨才置 false）
        private bool _walking;
        // 最近一次见到的曲目 key（归一化后，形如 "Bgm00/FloralLife"；null = 静音轨/无法归一化）
        private string? _currentKey;
        // 最近一次见到的播放状态：用于区分 Resume（Paused→Playing）与首播/换曲（Stopped→Playing）
        private PlaybackState _lastState = PlaybackState.Stopped;
        // Dispose 标记（Interlocked 保证幂等；事件回调里复查避免投递体在退订后仍调用已销毁的行走服务）
        private int _disposed;

        /// <summary>「播放音乐时加载地图」开关（false = 只听音乐，完全不加载地图/不进入行走模式）。null = 恒启用。</summary>
        private readonly Func<bool>? _isEnabled;

        public WalkMusicBridge(WalkModeService walk, MusicPlayerService player, Func<bool>? isEnabled = null)
        {
            _walk = walk ?? throw new ArgumentNullException(nameof(walk));
            _music = player ?? throw new ArgumentNullException(nameof(player));
            _isEnabled = isEnabled;
            _onCurrentChanged = OnCurrentChanged;
            _onTrackEnded = OnTrackEnded;
            _onStateChanged = OnStateChanged;
            // 订阅真实事件：CurrentChanged（换曲/首播，携带 MusicTrack?）与 StateChanged（Stopped/Playing/Paused）
            _music.CurrentChanged += _onCurrentChanged;
            _music.StateChanged += _onStateChanged;
            _music.TrackEnded += _onTrackEnded;   // 自然播完（区别于用户 Stop/Next）
        }

        /// <summary>退订全部事件（幂等，可重复调用；退订后迟到的回调只记日志不再驱动行走）。</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            try
            {
                _music.CurrentChanged -= _onCurrentChanged;
                _music.StateChanged -= _onStateChanged;
                _music.TrackEnded -= _onTrackEnded;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WalkMusicBridge] 退订音乐事件失败: {ex.Message}");
            }
        }

        // ====== 事件回调（可能来自 BASS 回调线程：只决策 + 投递，不抛异常） ======

        /// <summary>
        /// 曲目变化（用户点播 / 自动下一首 / RepeatOne 重播 / 场景联动换曲）：
        /// 首个曲目 → Start；已在行走中再收到曲目变化 → 视为「上一曲结束」走 OnTrackFinished + Start(新 key)。
        /// </summary>
        private void OnCurrentChanged(MusicTrack? track)
        {
            try
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }
                // 「播放音乐时加载地图」关闭 → 只听音乐：正在行走则退出，且不再进入（不触发任何 LoadMap）
                if (_isEnabled != null && !_isEnabled())
                {
                    bool walkingNow;
                    lock (_lock)
                    {
                        walkingNow = _walking;
                        _walking = false;
                        _currentKey = null;
                    }
                    if (walkingNow)
                    {
                        Console.Error.WriteLine("[WalkMusicBridge] 「播放音乐时加载地图」已关闭 → 停止行走（只听音乐）");
                        Post("开关关闭退出行走", () => _walk.Stop());
                    }
                    return;
                }
                string? key = TrackKeyOf(track);
                if (key == null)
                {
                    // 静音轨 Bgm00/Silence（WalkPathPlanner.TrackKeyOf 归一化为 null）或无斜杠/空串：
                    // 不进入行走模式；若此刻正在行走（如场景联动切到静音轨）则退出，避免停在无 BGM 的地图。
                    bool walking;
                    lock (_lock)
                    {
                        walking = _walking;
                        _walking = false;
                        _currentKey = null;
                    }
                    if (walking)
                    {
                        Console.Error.WriteLine($"[WalkMusicBridge] 静音/无效曲目({track?.Key ?? "null"})，退出行走模式");
                        Post("静音轨退出行走", () => _walk.Stop());
                    }
                    return;
                }
                // 防御：CurrentChanged 由服务在「状态已置 Playing」之后发出（PlayAsync / 自动续播 / 场景联动提交），
                // 非播放态不启动行走（避免恢复预选/异常路径误触发）。
                if (_music.State != PlaybackState.Playing)
                {
                    Console.Error.WriteLine($"[WalkMusicBridge] 曲目变化但播放态为 {_music.State}，不进入行走模式: {key}");
                    return;
                }
                string? prevKey;
                bool wasWalking;
                lock (_lock)
                {
                    prevKey = _currentKey;
                    wasWalking = _walking;
                    _walking = true;
                    _currentKey = key;
                }
                if (!wasWalking)
                {
                    // 首播（或 Stop 后重新播放）：直接进入行走模式
                    Post($"开始行走({key})", () => _walk.Start(key));
                    return;
                }
                // 换曲（自然播完自动下一首 / RepeatOne 重播同曲 / 用户切歌）：以新曲目 key 重新开始。
                // 对齐 PLAN-F18 §1「歌曲播放完 → 人物立即停止 → 切下一张地图 → 继续走」——
                // 其中「立即停止」由 TrackEnded → OnTrackFinished() 负责（见 OnTrackEnded），此处只管开走。
                Post($"换曲继续行走({prevKey} → {key})", () => _walk.Start(key));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WalkMusicBridge] CurrentChanged 处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 曲目**自然播完**（MusicPlayerService.TrackEnded，后台线程；区别于用户 Stop / Next）：
        /// 立即停走并置 Crossing → 随后服务的自动续播会发 CurrentChanged → Start(新 key) 完成切图。
        /// </summary>
        private void OnTrackEnded(MusicTrack? track)
        {
            try
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }
                bool walking;
                string key;
                lock (_lock)
                {
                    walking = _walking;
                    key = _currentKey ?? "";
                }
                if (!walking)
                {
                    return;
                }
                Post($"曲目播完({key})→停走待切图", () => _walk.OnTrackFinished());
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WalkMusicBridge] TrackEnded 处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 播放状态变化：Paused → 人物暂停；Paused→Playing（Resume，服务不换曲故不发 CurrentChanged）→ 继续；
        /// Stopped → 退出行走模式（用户 Stop；⚠️ 自然播完且无后继曲目时服务同样落 Stopped，两者无独立事件可区分）。
        /// </summary>
        private void OnStateChanged(PlaybackState state)
        {
            try
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }
                PlaybackState prev;
                lock (_lock)
                {
                    prev = _lastState;
                    _lastState = state;
                }
                if (prev == state)
                {
                    // 同态重复事件去重（Stopped→Stopped / Playing→Playing）
                    return;
                }
                switch (state)
                {
                    case PlaybackState.Paused:
                    {
                        // Pause：保持行走会话（_walking 不动），仅让人物停住
                        bool walking;
                        lock (_lock)
                        {
                            walking = _walking;
                        }
                        if (walking)
                        {
                            Post("暂停行走", () => _walk.Pause());
                        }
                        break;
                    }
                    case PlaybackState.Playing:
                    {
                        // 只处理 Paused→Playing（继续）；Stopped→Playing 的启动由 CurrentChanged 分支负责
                        //（服务先发 CurrentChanged 再发 StateChanged(Playing)，见 PlayAsync）
                        if (prev != PlaybackState.Paused)
                        {
                            break;
                        }
                        bool walking;
                        lock (_lock)
                        {
                            walking = _walking;
                        }
                        if (walking)
                        {
                            Post("继续行走", () => _walk.Resume());
                        }
                        break;
                    }
                    case PlaybackState.Stopped:
                    default:
                    {
                        // Stop（用户停止，不含暂停）→ 退出行走模式
                        bool walking;
                        lock (_lock)
                        {
                            walking = _walking;
                            _walking = false;
                            _currentKey = null;
                        }
                        if (walking)
                        {
                            Post("停止行走", () => _walk.Stop());
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WalkMusicBridge] StateChanged 处理失败: {ex.Message}");
            }
        }

        // ====== private ======

        /// <summary>
        /// 曲目 → 行走模式用 key：MusicTrack.Img/Track（"Bgm00.img/FloralLife"，本地曲目 Img="__LOCAL__"）
        /// 经 WalkPathPlanner.TrackKeyOf 归一化为 "Bgm00/FloralLife"（去 .img 后缀，与地图 info/bgm 同口径）；
        /// 静音轨 Bgm00/Silence 与无斜杠/空串（MusicDecisions.NormalizeBgm 返回 null）→ null。
        /// ⚠️ MusicTrack 的 Key 带 .img（"Bgm00.img/Silence"），而 MusicDecisions.IsIgnoredBgm 只认
        /// 归一键 "Bgm00/Silence"，故必须**归一化后再判一次**静音轨。
        /// </summary>
        private static string? TrackKeyOf(MusicTrack? track)
        {
            if (track == null)
            {
                return null;
            }
            string? key = WalkPathPlanner.TrackKeyOf($"{track.Img}/{track.Track}");
            if (key != null && MusicDecisions.IsIgnoredBgm(key))
            {
                return null;
            }
            return key;
        }

        /// <summary>投递到 UI 线程执行行走调用（现场与投递体两侧都 try/catch，任何异常只记 stderr 不外抛）。</summary>
        private void Post(string what, Action action)
        {
            try
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (Volatile.Read(ref _disposed) != 0)
                        {
                            return;
                        }
                        action();
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[WalkMusicBridge] {what} 执行失败: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WalkMusicBridge] {what} 投递 UI 线程失败: {ex.Message}");
            }
        }
    }
}
