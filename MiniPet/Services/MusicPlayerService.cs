using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MiniPet.Models;

namespace MiniPet.Services
{
    /// <summary>
    /// 音乐播放服务（设计 §11.3 + PLAN Rev 6 步骤 5/6 全部协议 + V0.2.1 本地源/随机跨节点）：
    /// 播放控制 / 状态 / 场景联动调度。
    /// - 换曲一律走两阶段交换协议：音频提取（TryExtractBytes 统一入口：本地曲目读文件、
    ///   WZ 曲目走 ExtractSound）→ IMusicPlayer.Prepare 预备流（失败保持原播放不动）
    ///   → Commit 提交（成功才更新状态并发 CurrentChanged）；
    /// - 随机跨节点（V0.2.1）：Random 模式 Next/Prev 不再局限队列快照——从目录全库快照
    ///   （WZ+本地）均匀抽曲并把队列整体替换为单元素（沿用场景联动口径），Stopped 且无当前曲
    ///   也是可用入口直接开播；Prev 另由小型播放历史栈（容量 50，锁内操作）保证能回到上一曲；
    ///   Sequential/RepeatOne 队列数学完全不变；
    /// - 持久化：LastTrackKey 立即落盘 + PlayHistory move-to-front 去重截断 20（同一 Update 事务）；
    ///   Volume 800ms 防抖；PlaybackMode / SceneLinkEnabled 立即落盘；
    /// - 并发统一口径：服务层 SemaphoreSlim（_gate）串行换曲编排 + 代际标签丢弃过期回调——
    ///   操作代际只有**换曲类操作** bump（PlayAsync/StopAsync/NextAsync/PrevAsync/自然播完自动续播/
    ///   场景联动提交）；Pause/Resume/Seek/SetVolume/SetPlayMode 不换曲、不影响代际；
    /// - PlaybackEnded 来自 BASS 回调线程：只捕获操作代际投递轻量后台命令，执行前复核代际
    ///   （期间发生过用户播放操作则丢弃防双跳），提取与 JSON 落盘绝不在回调线程内联；
    /// - 恢复语义 = 只预选不出声：RestoreLastTrackAsync 静默匹配 LastTrackKey，绝不触发 CurrentChanged。
    /// </summary>
    public class MusicPlayerService
    {
        private readonly WzService _wz;
        private readonly MusicCatalogService _catalog;
        private readonly IMusicPlayer _player;
        private readonly ConfigService _config;

        // 轻量状态/代际字段的原子读改写锁（锁序恒为 _gate → _stateLock，绝不逆序）
        private readonly object _stateLock = new();
        // 换曲编排串行化（服务层并发统一口径；场景联动只把「提交段」放进来，预检在门外）
        private readonly SemaphoreSlim _gate = new(1, 1);

        // ====== 播放状态 ======
        private MusicTrack? _current;
        // 队列不可变快照：PlayAsync 时深拷贝（MusicTrack 为 record 天然不可变，列表级拷贝即等效深拷贝）
        private List<MusicTrack> _queue = new();
        private int _queueIndex = -1;
        private PlaybackState _state = PlaybackState.Stopped;
        private int _volume = 70;
        private PlaybackMode _playMode = PlaybackMode.Sequential;
        private bool _sceneLinkEnabled;
        // 恢复预选的上次曲目（供 UI 静默选中，绝不触发 CurrentChanged）
        private MusicTrack? _restoredLastTrack;
        // Volume 800ms 防抖定时器（每帧滑动重建，仅 Cancel 不 Dispose——Dispose 与 Task.Delay 注册有竞态）
        private CancellationTokenSource? _volumeDebounce;

        // ====== 随机跨节点历史栈（V0.2.1） ======
        // Prev 可回退的历史条目数上限：超限移除最旧，避免长会话无限增长
        private const int RandomHistoryCapacity = 50;
        // 播放历史：Random 模式换曲成功后压入换曲前的当前曲；Prev 在锁内弹出最近一条 ≠ 当前曲者
        private readonly List<MusicTrack> _randomHistory = new();

        // ====== 代际标签（过期快照/回调据此丢弃） ======
        // 操作代际：只有换曲类操作 bump（见类注释）；入队/快照时捕获，执行前复核
        private int _opGen;
        // 地图代际：每次 OnMapChanged 自增——过期地图事件不得覆盖新选择/用户操作
        private int _mapGen;
        // WZ 代际：订阅 WzReloaded 自增——预检期间 WZ 重载则放弃提交
        private int _wzGen;

        /// <summary>当前曲目变化（含场景联动换曲）。⚠️ 可能从后台线程触发，订阅方自行 Dispatcher.UIThread.Post 到 UI 线程。</summary>
        public event Action<MusicTrack?>? CurrentChanged;

        /// <summary>播放状态变化（Stopped/Playing/Paused）。⚠️ 可能从后台线程触发，订阅方自行派发到 UI 线程。</summary>
        public event Action<PlaybackState>? StateChanged;

        /// <summary>音量变化（0-100）。⚠️ 可能在任意线程触发，订阅方自行派发到 UI 线程。</summary>
        public event Action<int>? VolumeChanged;

        /// <summary>播放链路错误（携带错误串；stderr 已记录，绝不崩溃不弹模态，音乐 tab 状态栏显示）。⚠️ 可能从 BASS 回调线程触发。</summary>
        public event Action<string>? PlaybackError;

        public MusicPlayerService(WzService wz, MusicCatalogService catalog, IMusicPlayer player, ConfigService config)
        {
            _wz = wz;
            _catalog = catalog;
            _player = player;
            _config = config;
            // 启动从配置恢复音量/模式/联动开关（不自动播放——恢复语义 = 只预选不出声）
            MusicConfig music;
            try
            {
                music = config.LoadSettings().Music ?? new MusicConfig();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Music] 读取音乐配置失败，用默认值: {ex.Message}");
                music = new MusicConfig();
            }
            _volume = Math.Clamp(music.Volume, 0, 100);
            _playMode = music.PlaybackMode;
            _sceneLinkEnabled = music.SceneLinkEnabled;
            player.Volume = _volume / 100.0;
            // IMusicPlayer 事件（可能来自 BASS 回调线程）
            player.PlaybackEnded += OnPlaybackEnded;
            player.PlaybackError += OnPlayerPlaybackError;
            // WZ 重载代际（场景联动四元组重验用；参数 = 新 WZ 代际号）
            wz.WzReloaded += OnWzReloaded;
        }

        /// <summary>
        /// 曲目**自然播完**（区别于用户 Stop / Next / Prev；运行于后台工作线程，订阅者需自行切 UI 线程）。
        /// F18 行走模式据此「立即停走 + 0.5s 淡入淡出切换下一张地图」。
        /// </summary>
        public event Action<MusicTrack?>? TrackEnded;

        /// <summary>当前曲目（未播放为 null）。</summary>
        public MusicTrack? Current
        {
            get { lock (_stateLock) { return _current; } }
        }

        /// <summary>播放状态。</summary>
        public PlaybackState State
        {
            get { lock (_stateLock) { return _state; } }
        }

        /// <summary>当前播放位置（无活动流为 null；UI 进度条轮询用，转发 IMusicPlayer）。</summary>
        public TimeSpan? Position => _player.Position;

        /// <summary>当前曲目总时长（无活动流为 null；UI 进度条轮询用，转发 IMusicPlayer）。</summary>
        public TimeSpan? Duration => _player.Duration;

        /// <summary>音量（0-100）。</summary>
        public int Volume
        {
            get { lock (_stateLock) { return _volume; } }
        }

        /// <summary>播放模式（顺序/单曲循环/随机）。</summary>
        public PlaybackMode Mode
        {
            get { lock (_stateLock) { return _playMode; } }
        }

        /// <summary>场景联动开关（F17，默认关）。setter 立即持久化。</summary>
        public bool SceneLinkEnabled
        {
            get { lock (_stateLock) { return _sceneLinkEnabled; } }
            set
            {
                lock (_stateLock) { _sceneLinkEnabled = value; }
                try
                {
                    _config.Update(s => { s.Music.SceneLinkEnabled = value; });
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Music] 联动开关持久化失败: {ex.Message}");
                }
            }
        }

        /// <summary>恢复的上次曲目（供 UI 静默预选，绝不触发 CurrentChanged）；未恢复/找不到为 null。</summary>
        public MusicTrack? RestoredLastTrack
        {
            get { lock (_stateLock) { return _restoredLastTrack; } }
        }

        /// <summary>
        /// 播放指定曲目：队列深拷贝为不可变快照 + 校验目标曲目在其中（不在则 ArgumentException）；
        /// 两阶段交换（提取 → Prepare 预检 → Commit 提交），准备失败保持原播放不动 + PlaybackError 上抛；
        /// 提交成功后更新状态并 CurrentChanged；LastTrackKey 立即持久化 + PlayHistory 同事务写入。
        /// </summary>
        public async Task PlayAsync(MusicTrack track, List<MusicTrack> queue)
        {
            if (track == null)
            {
                return;
            }
            var snapshot = queue != null ? new List<MusicTrack>(queue) : new List<MusicTrack>();
            int index = snapshot.FindIndex(t => string.Equals(t.Key, track.Key, StringComparison.Ordinal));
            if (index < 0)
            {
                throw new ArgumentException($"目标曲目不在播放队列中: {track.Key}", nameof(track));
            }
            // 换曲类用户操作：立即 bump 操作代际（预检中的场景联动据此丢弃）
            Interlocked.Increment(ref _opGen);
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var (error, oldStreamAlive) = await SwitchToAsync(track).ConfigureAwait(false);
                if (error != null)
                {
                    ApplyFailure(MusicDecisions.DecideFailureOutcome(oldStreamAlive, State), error);
                    return;
                }
                lock (_stateLock)
                {
                    _queue = snapshot;
                    _queueIndex = index;
                    _current = track;
                    _state = PlaybackState.Playing;
                }
                PersistNowPlaying(track);
                CurrentChanged?.Invoke(track);
                StateChanged?.Invoke(PlaybackState.Playing);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>暂停（仅 Playing 态生效；不 bump 操作代际——不换曲）。</summary>
        public async Task PauseAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (State != PlaybackState.Playing)
                {
                    return;
                }
                try
                {
                    _player.Pause();
                }
                catch (Exception ex)
                {
                    RaiseError($"暂停失败: {ex.Message}");
                    return;
                }
                SetState(PlaybackState.Paused);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>继续（仅 Paused 态生效；不 bump 操作代际——不换曲）。</summary>
        public async Task ResumeAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (State != PlaybackState.Paused)
                {
                    return;
                }
                try
                {
                    _player.Resume();
                }
                catch (Exception ex)
                {
                    RaiseError($"继续播放失败: {ex.Message}");
                    return;
                }
                SetState(PlaybackState.Playing);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>停止（换曲类操作：bump 操作代际，废弃预检中的场景联动/自动续播）。</summary>
        public async Task StopAsync()
        {
            Interlocked.Increment(ref _opGen);
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                try
                {
                    _player.Stop();
                }
                catch (Exception ex)
                {
                    RaiseError($"停止失败: {ex.Message}");
                }
                SetState(PlaybackState.Stopped);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// 进度条跳转：clamp 到 [0, Duration)；Playing/Paused 两态均可；Stopped 无活动流为 no-op。
        /// 不 bump 操作代际（非换曲操作）。
        /// </summary>
        public Task SeekAsync(TimeSpan position)
        {
            if (State == PlaybackState.Stopped)
            {
                return Task.CompletedTask;
            }
            try
            {
                TimeSpan? duration = _player.Duration;
                if (duration.HasValue && duration.Value > TimeSpan.Zero)
                {
                    if (position < TimeSpan.Zero)
                    {
                        position = TimeSpan.Zero;
                    }
                    if (position >= duration.Value)
                    {
                        // 上界取 Duration - 1ms（clamp 到 [0, Duration) 开区间语义，避免跳到末尾触发播完）
                        position = duration.Value - TimeSpan.FromMilliseconds(1);
                    }
                }
                else if (position < TimeSpan.Zero)
                {
                    position = TimeSpan.Zero;
                }
                _player.Seek(position);
            }
            catch (Exception ex)
            {
                RaiseError($"进度跳转失败: {ex.Message}");
            }
            return Task.CompletedTask;
        }

        /// <summary>下一首（按 MusicDecisions.NextIndex 推进；换曲类操作 bump 操作代际）。</summary>
        public async Task NextAsync()
        {
            Interlocked.Increment(ref _opGen);
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // 用户主动 Next：活动流仍在播（准备失败可保持原播放不动）
                await AdvanceCoreAsync(1, oldStreamAlive: true).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>上一首（对称逻辑：Sequential -1 越界回卷队尾；Random 就换一首随机；换曲类操作 bump 操作代际）。</summary>
        public async Task PrevAsync()
        {
            Interlocked.Increment(ref _opGen);
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await AdvanceCoreAsync(-1, oldStreamAlive: true).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// 设置音量（0-100 clamp）：立即生效播放器 + VolumeChanged 事件；持久化 800ms 防抖
        /// （尽力而为，接受极端快退丢最后一次滑动的窗口——PLAN 已声明风险）。
        /// 不 bump 操作代际（非换曲操作）。
        /// </summary>
        public void SetVolume(int volume)
        {
            if (volume < 0)
            {
                volume = 0;
            }
            if (volume > 100)
            {
                volume = 100;
            }
            lock (_stateLock)
            {
                _volume = volume;
            }
            _player.Volume = volume / 100.0;
            VolumeChanged?.Invoke(volume);
            ScheduleVolumePersist(volume);
        }

        /// <summary>设置播放模式（立即持久化；不 bump 操作代际——不换曲，只影响后续推进策略）。</summary>
        public void SetPlayMode(PlaybackMode mode)
        {
            lock (_stateLock)
            {
                _playMode = mode;
            }
            try
            {
                _config.Update(s => { s.Music.PlaybackMode = mode; });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Music] 模式持久化失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 场景联动入口（F17）：fire-and-forget 后台任务（UI 同步代码不等待；try-catch 全包 + stderr 日志），
        /// 返回的 Task 不抛任何异常。快照四元组 =（地图代际[每次调用 bump] / 操作代际 / 启动时读的
        /// SceneLinkEnabled / WZ 状态），链路：联动关直接 return → 解析 bgm → null 保持当前不断曲 →
        /// 同曲跨图完全 no-op → 提取 + Prepare 预检 → 提交前重验四元组（任一变化丢弃预备流保持现状）→
        /// 提交：队列替换为场景曲目单元素 + 状态更新 + CurrentChanged。
        /// </summary>
        public Task OnMapChanged(string mapId)
        {
            int mapGen;
            lock (_stateLock)
            {
                mapGen = ++_mapGen;
            }
            return Task.Run(async () =>
            {
                try
                {
                    await SceneLinkCoreAsync(mapId, mapGen).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // 后台任务异常必捕获记录，绝不外泄
                    Console.Error.WriteLine($"[Music] OnMapChanged({mapId}) 失败: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 启动恢复（目录就绪时调用，如打开音乐 tab）：按 LastTrackKey **精确匹配**（非子串 Search）
        /// 找到曲目供 UI 预选中。恢复过程绝不触发 CurrentChanged（静默预选：不出启动气泡、不自动出声）；
        /// 目录没就绪/找不到 → 静默返回（结果经 RestoredLastTrack 属性供 UI 拉取）。
        /// </summary>
        public async Task RestoreLastTrackAsync()
        {
            try
            {
                string? key;
                try
                {
                    key = _config.LoadSettings().Music?.LastTrackKey;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Music] 读取 LastTrackKey 失败: {ex.Message}");
                    return;
                }
                if (string.IsNullOrEmpty(key))
                {
                    return;
                }
                if (_catalog.GetCatalogSnapshot() == null)
                {
                    // 目录未就绪：等待首次构建（WZ 未加载/过期异常在下方 catch 静默吞掉）
                    await _catalog.GetCatalogAsync().ConfigureAwait(false);
                }
                MusicTrack? track = TryFindTrack(key);
                if (track != null)
                {
                    lock (_stateLock)
                    {
                        _restoredLastTrack = track;
                    }
                    Console.WriteLine($"[Music] 上次曲目恢复预选: {track.Key}");
                }
            }
            catch (Exception ex)
            {
                // 恢复是尽力而为：任何失败（含目录过期异常）静默返回
                Console.Error.WriteLine($"[Music] 恢复上次曲目失败: {ex.Message}");
            }
        }

        /// <summary>按 Key（"BgmXX.img/Track" 或 "__LOCAL__/相对路径"）精确匹配曲目（Ordinal，非子串搜索）；
        /// V0.2.1 起在全库快照（WZ + 本地）上查找；全库未就绪/找不到 → null。</summary>
        public MusicTrack? TryFindTrack(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }
            var snapshot = _catalog.GetLibrarySnapshot();
            if (snapshot == null)
            {
                return null;
            }
            foreach (var t in snapshot)
            {
                if (string.Equals(t.Key, key, StringComparison.Ordinal))
                {
                    return t;
                }
            }
            return null;
        }

        // ====== private ======

        /// <summary>
        /// 场景联动核心（运行于后台任务，绝不占 UI/BASS 线程）：
        /// 第一步 快照四元组并检查联动开关 → 第二步 解析地图 bgm → 第三步 同曲 no-op 判断 →
        /// 第四步 预检（ExtractSound + Prepare，均不持 _gate）→ 第五步 持 _gate 重验四元组后提交。
        /// 任一步失败/复核不符 → 丢弃预备流保持现状（对齐「无可用 BGM 保持当前不断曲」）。
        /// </summary>
        private async Task SceneLinkCoreAsync(string mapId, int mapGenSnapshot)
        {
            // 第一步：快照四元组（地图代际已由 OnMapChanged bump 并传入）
            int opGenSnapshot;
            int wzGenSnapshot;
            bool sceneOn;
            lock (_stateLock)
            {
                opGenSnapshot = _opGen;
                wzGenSnapshot = _wzGen;
                sceneOn = _sceneLinkEnabled;
            }
            // 联动关闭：直接 return（不换歌）
            if (!sceneOn)
            {
                return;
            }
            // 第二步：解析 bgm（按需单查，不预遍历）
            MusicTrackRef? trackRef = await _catalog.GetSceneTrackAsync(mapId).ConfigureAwait(false);
            if (trackRef == null)
            {
                // 无 BGM / WZ 未加载 → 保持当前播放不断曲
                return;
            }
            MusicTrack sceneTrack = ResolveSceneTrack(trackRef);
            // 第三步：场景 Key == 当前播放 Key → 完全 no-op（同曲跨图不重启）
            lock (_stateLock)
            {
                if (_current != null && string.Equals(_current.Key, sceneTrack.Key, StringComparison.Ordinal))
                {
                    return;
                }
            }
            // 第四步：预检——音频提取（后台线程；统一走 TryExtractBytes，本地/WZ 分支同源防漂移）
            byte[]? bytes;
            try
            {
                bytes = await Task.Run(() => TryExtractBytes(sceneTrack)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Music] 场景联动提取失败({sceneTrack.Key}): {ex.Message}");
                bytes = null;
            }
            if (bytes == null)
            {
                // 预检失败保持现状
                return;
            }
            // 第四步：预检——两阶段交换的准备阶段（打开并校验第二临时流，旧流不受影响）
            IPreparedStream? prepared = null;
            try
            {
                prepared = _player.Prepare(bytes);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Music] 场景联动预备流失败({sceneTrack.Key}): {ex.Message}");
                try { prepared?.Dispose(); } catch { /* 丢弃失败忽略 */ }
                return;
            }
            if (prepared == null)
            {
                return;
            }
            // 第五步：提交段（持 _gate，与用户操作串行化）
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // 提交前完整重验四元组：地图代际 / 操作代际 / WZ 代际 / SceneLinkEnabled 开关值
                // （用户期间关了联动/切了歌/WZ 重载/又切了地图 → 丢弃预备流保持现状）
                bool unchanged;
                lock (_stateLock)
                {
                    unchanged = mapGenSnapshot == _mapGen
                                && opGenSnapshot == _opGen
                                && wzGenSnapshot == _wzGen
                                && _sceneLinkEnabled;
                }
                var current = Current;
                if (!unchanged || (current != null && string.Equals(current.Key, sceneTrack.Key, StringComparison.Ordinal)))
                {
                    try { prepared.Dispose(); } catch { /* 丢弃失败忽略 */ }
                    return;
                }
                try
                {
                    // Commit 失败不抛异常（返回 false；新旧资源已由 player 确定性释放并触发 PlaybackError）
                    if (!prepared.Commit())
                    {
                        Console.Error.WriteLine($"[Music] 场景联动提交失败({sceneTrack.Key})");
                        // 提交段失败 = 终态（旧流已被 player 确定性释放）：Stopped + PlaybackError
                        ApplyFailure(MusicDecisions.DecideFailureOutcome(false, State), $"场景联动播放失败: {sceneTrack.Key}");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    try { prepared.Dispose(); } catch { /* 丢弃失败忽略 */ }
                    Console.Error.WriteLine($"[Music] 场景联动提交失败({sceneTrack.Key}): {ex.Message}");
                    // 提交段失败 = 终态（旧流已被 player 确定性释放）：Stopped + PlaybackError
                    ApplyFailure(MusicDecisions.DecideFailureOutcome(false, State), $"场景联动播放失败: {sceneTrack.Key}: {ex.Message}");
                    return;
                }
                // 场景联动提交也是换曲类操作：bump 操作代际（废弃其他过期快照）
                Interlocked.Increment(ref _opGen);
                lock (_stateLock)
                {
                    // 队列整体替换为场景曲目的单元素队列
                    _queue = new List<MusicTrack> { sceneTrack };
                    _queueIndex = 0;
                    _current = sceneTrack;
                    _state = PlaybackState.Playing;
                }
                PersistNowPlaying(sceneTrack);
                CurrentChanged?.Invoke(sceneTrack);
                StateChanged?.Invoke(PlaybackState.Playing);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// 队列推进核心（Next/Prev/自动续播共用，调用方已持 _gate）：
        /// V0.2.1 起 Random 模式改走 AdvanceRandomCoreAsync（跨节点全库抽取）；Sequential/RepeatOne
        /// 维持原队列数学完全不变——按 MusicDecisions.NextIndex/PrevIndex 计算目标 → 两阶段交换 →
        /// 成功更新状态发事件；失败按 MusicDecisions.DecideFailureOutcome 落地
        /// （自然播完 oldStreamAlive=false → 终态 Stopped）。
        /// </summary>
        private async Task AdvanceCoreAsync(int direction, bool oldStreamAlive)
        {
            PlaybackMode mode;
            PlaybackState state;
            lock (_stateLock)
            {
                mode = _playMode;
                state = _state;
            }
            // 随机跨节点：不局限队列快照，从全库抽目标（Stopped 且 Current==null 也是可用入口）
            if (mode == PlaybackMode.Random)
            {
                await AdvanceRandomCoreAsync(direction, oldStreamAlive).ConfigureAwait(false);
                return;
            }
            MusicTrack? current;
            List<MusicTrack> queue;
            int index;
            lock (_stateLock)
            {
                current = _current;
                queue = _queue;
                index = _queueIndex;
            }
            if (queue.Count == 0)
            {
                // 无队列可推进：自然播完 → Stopped 收场；用户 Next → 无事可做保持现状
                if (!oldStreamAlive)
                {
                    SetState(PlaybackState.Stopped);
                }
                return;
            }
            int next = direction >= 0
                ? MusicDecisions.NextIndex(mode, queue.Count, index)
                : MusicDecisions.PrevIndex(mode, queue.Count, index);
            if (next < 0)
            {
                return;
            }
            MusicTrack target = queue[next];
            var (error, oldAliveAfterSwitch) = await SwitchToAsync(target).ConfigureAwait(false);
            if (error != null)
            {
                // 准备失败且旧流仍活 → 保持原播放；提交失败/自然播完 → 终态 Stopped
                ApplyFailure(MusicDecisions.DecideFailureOutcome(oldStreamAlive && oldAliveAfterSwitch, state), error);
                return;
            }
            lock (_stateLock)
            {
                _current = target;
                _queueIndex = next;
                _state = PlaybackState.Playing;
            }
            PersistNowPlaying(target);
            CurrentChanged?.Invoke(target);
            StateChanged?.Invoke(PlaybackState.Playing);
        }

        /// <summary>
        /// 随机跨节点推进（V0.2.1，Random 模式专用，调用方已持 _gate）：
        /// 第一步 候选池 = 目录服务全库快照（WZ + 本地合并；目录未就绪退化当前队列快照）；
        /// 第二步 选目标——Prev 优先在锁内弹播放历史栈（容量 50，弹出即等于当前曲的条目直接丢弃），
        /// 栈空或 Next 一律 PickRandomAcross 全库均匀抽（exclude = 当前曲在全库中的位置；
        /// Current==null 的 Stopped 入口 / Key 不在全库时 exclude 失效仅靠有限重抽避同曲）；
        /// 第三步 两阶段交换（失败链路同队列口径：DecideFailureOutcome 保持现状或终态 Stopped）；
        /// 第四步 提交——Next 把换曲前的当前曲压入历史栈（与队列替换同临界区原子），
        /// _queue 整体替换为 [该曲] 单元素队列（沿用场景联动的队列替换口径），
        /// 走既有 PersistNowPlaying / CurrentChanged / StateChanged 链路。
        /// </summary>
        private async Task AdvanceRandomCoreAsync(int direction, bool oldStreamAlive)
        {
            // 第一步：候选池（GetLibrarySnapshot 返回拷贝；未就绪时退化为当前队列快照）
            List<MusicTrack>? pool = _catalog.GetLibrarySnapshot();
            lock (_stateLock)
            {
                pool ??= _queue;
            }
            MusicTrack? current = Current;

            // 第二步：选目标（Prev 优先历史栈回退）
            MusicTrack? target = null;
            if (direction < 0)
            {
                lock (_stateLock)
                {
                    while (_randomHistory.Count > 0)
                    {
                        MusicTrack candidate = _randomHistory[^1];
                        _randomHistory.RemoveAt(_randomHistory.Count - 1);
                        if (candidate.Key != current?.Key)
                        {
                            target = candidate;
                            break;
                        }
                        // 弹出条目即当前曲 → 直接丢弃不再复活（A → Prev 不重复播 A）
                    }
                }
            }
            if (target == null)
            {
                if (pool.Count == 0)
                {
                    // 池空（全库与队列均不可用）：自然播完落 Stopped；用户 Next/Prev 无事可做保持现状
                    if (!oldStreamAlive)
                    {
                        SetState(PlaybackState.Stopped);
                    }
                    return;
                }
                // exclude 取当前曲在全库中的位置；Current==null（Stopped 入口）/Key 未命中 → -1 不排除
                int excludeIndex = current == null
                    ? -1
                    : pool.FindIndex(t => string.Equals(t.Key, current.Key, StringComparison.Ordinal));
                var rng = new Random();
                int pick = MusicDecisions.PickRandomAcross(pool.Count, excludeIndex, rng);
                MusicTrack picked = pool[pick];
                // 防御：exclude 失效（Key 不在全库）时可能抽中同曲，有限次重抽尽力避开
                int retries = 0;
                while (pool.Count > 1
                       && current != null
                       && string.Equals(picked.Key, current.Key, StringComparison.Ordinal)
                       && retries < 8)
                {
                    pick = MusicDecisions.PickRandomAcross(pool.Count, excludeIndex, rng);
                    picked = pool[pick];
                    retries++;
                }
                target = picked;
            }

            // 第三步：两阶段交换（用户操作旧流仍活 / 自然播完结终——由调用方传入 oldStreamAlive 口径）
            var (error, aliveAfter) = await SwitchToAsync(target).ConfigureAwait(false);
            if (error != null)
            {
                ApplyFailure(MusicDecisions.DecideFailureOutcome(oldStreamAlive && aliveAfter, State), error);
                return;
            }

            // 第四步：提交（历史压栈 + 单元素队列替换同一临界区原子完成）
            lock (_stateLock)
            {
                if (direction > 0 && current != null)
                {
                    _randomHistory.Add(current);
                    if (_randomHistory.Count > RandomHistoryCapacity)
                    {
                        _randomHistory.RemoveRange(0, _randomHistory.Count - RandomHistoryCapacity);
                    }
                }
                _queue = new List<MusicTrack> { target };
                _queueIndex = 0;
                _current = target;
                _state = PlaybackState.Playing;
            }
            PersistNowPlaying(target);
            CurrentChanged?.Invoke(target);
            StateChanged?.Invoke(PlaybackState.Playing);
        }

        /// <summary>
        /// 音频字节提取统一入口（V0.2.1，用户换曲/自动续播与场景联动预检共用，防两处漂移）：
        /// - 本地曲目（Img=__LOCAL__）：从目录服务 LocalRoot + Track 相对路径（'/' 分隔）还原平台路径
        ///   读文件字节；根未启用 / 文件缺失 / 读盘异常一律返回 null → 上层走既有失败链路
        ///   （保持当前播放 + PlaybackError 上抛）；
        /// - 其余（WZ 曲目）：原 ExtractSound 分支不变（Sound/ 前缀与正斜杠 Replace 坑在 WzService 内部处理）。
        /// ⚠️ 必须在 Task.Run 后台线程调用（本地大文件读盘与 WZ 提取均不得进入 UI/BASS 线程）。
        /// </summary>
        private byte[]? TryExtractBytes(MusicTrack track)
        {
            if (string.Equals(track.Img, MusicCatalogService.LocalImgTag, StringComparison.Ordinal))
            {
                try
                {
                    string? root = _catalog.LocalRoot;
                    if (string.IsNullOrWhiteSpace(root))
                    {
                        Console.Error.WriteLine($"[Music] 本地音频读取失败({track.Key}): 本地源未启用");
                        return null;
                    }
                    // Track 入库口径为 '/' 分隔的相对路径（BuildLocalCore 生成）→ 反向还原平台分隔符
                    string fullPath = Path.Combine(root, track.Track.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(fullPath))
                    {
                        Console.Error.WriteLine($"[Music] 本地音频读取失败({track.Key}): 文件不存在 {fullPath}");
                        return null;
                    }
                    return File.ReadAllBytes(fullPath);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Music] 本地音频读取异常({track.Key}): {ex.Message}");
                    return null;
                }
            }
            try
            {
                return _wz.ExtractSound(track.Img, track.Track);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Music] 提取音频异常({track.Key}): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 两阶段换曲核心（用户播放/自动续播共用，调用方已持 _gate）：
        /// 第一步 WZ 提取（Task.Run 后台线程，绝不占 UI/BASS 线程）→ 第二步 Prepare 预备流
        /// （准备阶段失败只丢弃临时流，活动流分毫未动）→ Commit 提交（player 内部原子完成：
        /// 移除旧 END SYNC → stop/free 旧流 → 新流提升为活动流 → 应用音量 → 装新 END SYNC → 起播）。
        /// 返回 (null, true) = 成功；(错误串, 旧流是否仍可播) = 失败。
        /// </summary>
        private async Task<(string? Error, bool OldStreamAlive)> SwitchToAsync(MusicTrack track)
        {
            byte[]? bytes;
            try
            {
                bytes = await Task.Run(() => TryExtractBytes(track)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Music] 提取音频异常({track.Key}): {ex.Message}");
                bytes = null;
            }
            if (bytes == null)
            {
                // 提取失败：准备阶段失败，未动活动流
                return ($"读取音频失败: {track.Key}", true);
            }
            IPreparedStream? prepared = null;
            try
            {
                prepared = _player.Prepare(bytes);
                if (prepared == null)
                {
                    // 建流失败：只丢弃临时资源，活动流不动
                    return ($"音频流准备失败: {track.Key}", true);
                }
                // Commit 失败不抛异常（返回 false；新旧资源已由 player 确定性释放并触发 PlaybackError）
                if (!prepared.Commit())
                {
                    Console.Error.WriteLine($"[Music] 播放提交失败({track.Key})");
                    // 提交段失败 = 终态：新旧流与 pin 已被 player 确定性释放，「保持当前播放」不再可行
                    return ($"播放失败: {track.Key}", false);
                }
                return (null, true);
            }
            catch (Exception ex)
            {
                try { prepared?.Dispose(); } catch { /* 丢弃失败忽略 */ }
                Console.Error.WriteLine($"[Music] 播放提交失败({track.Key}): {ex.Message}");
                // 提交段失败 = 终态：新旧流与 pin 已被 player 确定性释放，「保持当前播放」不再可行
                return ($"播放失败: {track.Key}: {ex.Message}", false);
            }
        }

        /// <summary>
        /// 自然播完回调（IMusicPlayer 事件，来自 BASS 回调线程）：
        /// 只捕获操作代际并投递轻量后台命令（Task.Run）；WZ 提取/JSON 落盘/流切换编排
        /// 绝不在回调线程内联（不冻结音频回调，也不阻塞 UI）。
        /// </summary>
        private void OnPlaybackEnded()
        {
            int gen = Volatile.Read(ref _opGen);
            _ = Task.Run(() => HandleNaturalEndAsync(gen));
        }

        /// <summary>
        /// 自然播完的自动续播（运行于后台工作线程）：执行前复核操作代际——入队后发生过用户播放操作
        /// 则丢弃（防双跳/复活）；RepeatOne 模式下 NextIndex 返回原索引 = 重播当前，重备失败同样
        /// Stopped 终态收场，无重试循环。
        /// </summary>
        private async Task HandleNaturalEndAsync(int gen)
        {
            try
            {
                if (gen != Volatile.Read(ref _opGen))
                {
                    // 期间发生过用户播放操作：迟到回调直接丢弃
                    return;
                }
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (gen != Volatile.Read(ref _opGen))
                    {
                        // 拿到编排锁后再复核一次
                        return;
                    }
                    // 自然播完确认（代际未被用户操作取代）→ 通知订阅者（F18 行走模式：停走 + 切下一张地图）
                    MusicTrack? endedTrack = Current;
                    try { TrackEnded?.Invoke(endedTrack); }
                    catch (Exception ex) { Console.Error.WriteLine($"[Music] TrackEnded 订阅者异常: {ex.Message}"); }
                    // 自动续播也是换曲类操作：bump 操作代际
                    Interlocked.Increment(ref _opGen);
                    // 自然播完：旧流已终结——后续任何失败都是终态（oldStreamAlive=false）
                    await AdvanceCoreAsync(1, oldStreamAlive: false).ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Music] 自动续播失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 按决策函数落地失败结果：状态有变化则 StateChanged，并上抛 PlaybackError
        /// （stderr 记录；绝不崩溃不弹模态）。
        /// </summary>
        private void ApplyFailure((PlaybackState NextState, bool RaiseError) outcome, string message)
        {
            if (!outcome.RaiseError)
            {
                return;
            }
            PlaybackState before = State;
            if (outcome.NextState != before)
            {
                lock (_stateLock)
                {
                    _state = outcome.NextState;
                }
                StateChanged?.Invoke(outcome.NextState);
            }
            RaiseError(message);
        }

        /// <summary>上抛播放链路错误（stderr 记录 + 服务层 PlaybackError 事件）。</summary>
        private void RaiseError(string message)
        {
            Console.Error.WriteLine($"[Music] {message}");
            PlaybackError?.Invoke(message);
        }

        /// <summary>更新播放状态并广播 StateChanged（调用方已持 _gate 或无并发窗口）。</summary>
        private void SetState(PlaybackState next)
        {
            lock (_stateLock)
            {
                _state = next;
            }
            StateChanged?.Invoke(next);
        }

        /// <summary>
        /// 播放新曲立即持久化：LastTrackKey 落盘 + PlayHistory move-to-front 去重截断 20，
        /// 在 ConfigService.Update 的同一事务内完成（其序列化锁内「读→改 Music 子对象→写」整事务，
        /// 并与 UI 线程的整对象回存串行化——见 PLAN 步骤 8）。
        /// </summary>
        private void PersistNowPlaying(MusicTrack track)
        {
            try
            {
                _config.Update(s =>
                {
                    var music = s.Music ?? new MusicConfig();
                    music.LastTrackKey = track.Key;
                    // move-to-front 去重：先移除旧位置再插到队首
                    music.PlayHistory.Remove(track.Key);
                    music.PlayHistory.Insert(0, track.Key);
                    // 截断 20（与 MusicConfig.PlayHistory 字段注释的 schema 契约一致）
                    if (music.PlayHistory.Count > 20)
                    {
                        music.PlayHistory.RemoveRange(20, music.PlayHistory.Count - 20);
                    }
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Music] 播放历史持久化失败: {ex.Message}");
            }
        }

        /// <summary>
        /// Volume 800ms 防抖持久化：新滑动取消旧定时器（仅 Cancel 不 Dispose——Dispose 与
        /// Task.Delay 的取消注册有竞态，宁可交给 GC 也不冒 ObjectDisposedException）。
        /// </summary>
        private void ScheduleVolumePersist(int volume)
        {
            var cts = new CancellationTokenSource();
            var previous = Interlocked.Exchange(ref _volumeDebounce, cts);
            if (previous != null)
            {
                try { previous.Cancel(); } catch (Exception) { /* 忽略旧定时器取消失败 */ }
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(800, cts.Token).ConfigureAwait(false);
                    _config.Update(s => { s.Music.Volume = volume; });
                }
                catch (OperationCanceledException)
                {
                    // 防抖窗口内新滑动覆盖：本次落盘放弃（尽力而为）
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Music] 音量持久化失败: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 场景曲目定位：目录就绪则按 Key 精确取回带元数据的曲目（Ms/Channels/Frequency 真实值）；
        /// 否则 Ms=0 兜底构造（场景曲目元数据缺口 = Ms 填 0，Display 省略时长，
        /// 真实时长以 IMusicPlayer.Duration 打开流后报告的值为准）。
        /// </summary>
        private MusicTrack ResolveSceneTrack(MusicTrackRef trackRef)
        {
            var snapshot = _catalog.GetCatalogSnapshot();
            if (snapshot != null)
            {
                string key = $"{trackRef.Img}/{trackRef.Track}";
                foreach (var t in snapshot)
                {
                    if (string.Equals(t.Key, key, StringComparison.Ordinal))
                    {
                        return t;
                    }
                }
            }
            return new MusicTrack(trackRef.Img, trackRef.Track, 0, 0, 0);
        }

        /// <summary>WZ 重载事件（参数 = 新 WZ 代际号）：bump 本服务 WZ 代际，预检中的场景联动据此放弃提交。</summary>
        private void OnWzReloaded(int generation)
        {
            Interlocked.Increment(ref _wzGen);
        }

        /// <summary>IMusicPlayer 错误事件转发（可能来自 BASS 回调线程）——stderr 记录 + 服务层 PlaybackError 上抛。</summary>
        private void OnPlayerPlaybackError(string message)
        {
            RaiseError(message);
        }
    }
}
