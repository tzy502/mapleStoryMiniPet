using System;
using System.Runtime.InteropServices;
using System.Threading;
using ManagedBass;

namespace MiniPet.Services
{
    /// <summary>
    /// IMusicPlayer 的 ManagedBass 单实现（ADR-0007，双平台同一份代码，仅原生库不同：
    /// macOS libbass.dylib / Windows bass.dll，由 csproj 按构建平台拷贝到输出目录）。
    /// 长生命周期单例服务（≠R2 的一次性 Music 对象），切换曲目走两阶段交换协议：
    /// ①准备段（Prepare）——pin 新 byte[] + 建临时流 + 校验，旧流照常播放不受影响；
    /// ②提交段（Commit，单个 SemaphoreSlim 临界区内原子完成）——移除旧流 END SYNC →
    /// stop/free 旧流 → Unpin 旧 GCHandle → 新流提升为活动流 → 应用音量 →
    /// 安装带代际编号的新 END SYNC → 起播。
    /// 提交段任一 BASS 调用失败 = 终态：新旧流与 pin 确定性释放 + PlaybackError 上抛（不抛异常，绝不崩溃）。
    /// 代际不匹配的迟到 END SYNC 回调直接丢弃（防旧流回调复活）。
    /// Bass.Init 懒初始化（首次 Prepare 时执行一次）。
    /// </summary>
    public sealed class BassMusicPlayer : IMusicPlayer
    {
        // ====== 字段 ======
        // 所有过渡（提交/停止/暂停/恢复/跳转/音量）串行化的单信号量（PLAN 步骤 5 并发纪律）
        private readonly SemaphoreSlim _gate = new(1, 1);
        // 懒初始化守卫（Init 只做一次，不占 _gate——Prepare 高频时不应与 UI 播控互斥）
        private readonly object _initLock = new();
        private bool _initialized;
        private bool _disposed;
        // 活动流三件套（在 _gate 临界区内变更；读侧依赖 int 读写原子性）
        private int _activeHandle;
        private GCHandle _activePin;
        private int _activeSync;
        // 代际编号：每次活动流变化自增，END SYNC 回调闭包捕获安装时编号，比对不一致即迟到回调，丢弃
        private int _generation;
        // 音量缓存（float 保证读写原子；接口侧 double 转换）
        private float _volume = 1f;

        // ====== 事件 ======
        /// <summary>自然播完（⚠️ 从 BASS 回调线程触发，订阅方自行 Dispatcher.UIThread.Post 切回 UI 线程）。</summary>
        public event Action? PlaybackEnded;

        /// <summary>播放错误（⚠️ 可能从调用线程触发；携带可读错误串，订阅方自行切回 UI 线程）。</summary>
        public event Action<string>? PlaybackError;

        // ====== 状态查询 ======
        /// <summary>是否正在播放（活动流存在且处于播放态）。</summary>
        public bool IsPlaying
        {
            get
            {
                int handle = Volatile.Read(ref _activeHandle);
                if (handle == 0) { return false; }
                return Bass.ChannelIsActive(handle) == ManagedBass.PlaybackState.Playing;
            }
        }

        /// <summary>是否暂停（活动流存在且处于暂停态）。</summary>
        public bool IsPaused
        {
            get
            {
                int handle = Volatile.Read(ref _activeHandle);
                if (handle == 0) { return false; }
                return Bass.ChannelIsActive(handle) == ManagedBass.PlaybackState.Paused;
            }
        }

        /// <summary>音量 0.0-1.0（缓存值；当前无活动流时仅更新缓存，下次起播生效）。</summary>
        public double Volume
        {
            get { return _volume; }
            set
            {
                double clamped = Math.Clamp(value, 0.0, 1.0);
                _gate.Wait();
                try
                {
                    _volume = (float)clamped;
                    if (_activeHandle != 0)
                    {
                        Bass.ChannelSetAttribute(_activeHandle, ChannelAttribute.Volume, _volume);
                    }
                }
                finally { _gate.Release(); }
            }
        }

        /// <summary>当前播放位置；Stopped（无活动流）状态允许 null。</summary>
        public TimeSpan? Position
        {
            get
            {
                int handle = Volatile.Read(ref _activeHandle);
                if (handle == 0) { return null; }
                long bytes = Bass.ChannelGetPosition(handle, PositionFlags.Bytes);
                if (bytes < 0) { return null; }
                double seconds = Bass.ChannelBytes2Seconds(handle, bytes);
                if (seconds < 0) { return null; }
                return TimeSpan.FromSeconds(seconds);
            }
        }

        /// <summary>总时长；Stopped（无活动流）状态允许 null。</summary>
        public TimeSpan? Duration
        {
            get
            {
                int handle = Volatile.Read(ref _activeHandle);
                if (handle == 0) { return null; }
                long bytes = Bass.ChannelGetLength(handle, PositionFlags.Bytes);
                if (bytes <= 0) { return null; }
                double seconds = Bass.ChannelBytes2Seconds(handle, bytes);
                if (seconds < 0) { return null; }
                return TimeSpan.FromSeconds(seconds);
            }
        }

        // ====== 两阶段交换协议 ======

        /// <summary>
        /// 准备段：懒初始化 BASS → pin 新 byte[] → 创建临时流。旧活动流完全不受影响。
        /// 失败返回 null 并触发 PlaybackError（错误经事件上报，方法本身不抛异常）。
        /// </summary>
        /// <param name="mp3Bytes">WZ 提取的完整 MP3 字节流（预备期归预备句柄持有，直至提交/丢弃）。</param>
        public IPreparedStream? Prepare(byte[] mp3Bytes)
        {
            if (_disposed)
            {
                RaiseError("播放器已释放，无法准备新流");
                return null;
            }
            if (mp3Bytes == null || mp3Bytes.Length == 0)
            {
                RaiseError("音频数据为空，无法建流");
                return null;
            }
            if (!EnsureInitialized())
            {
                RaiseError($"BASS 初始化失败: {Bass.LastError}");
                return null;
            }

            // 准备段：pin + 建临时流（不动共享状态，无需 _gate）
            GCHandle pin = GCHandle.Alloc(mp3Bytes, GCHandleType.Pinned);
            int handle = Bass.CreateStream(pin.AddrOfPinnedObject(), 0, mp3Bytes.Length, BassFlags.Default);
            if (handle == 0)
            {
                pin.Free();
                RaiseError($"音频建流失败（数据损坏或格式不支持）: {Bass.LastError}");
                return null;
            }
            return new BassPreparedStream(this, handle, pin);
        }

        /// <summary>播放 MP3 字节流 = Prepare + Commit 的便利方法（失败静默返回，错误经 PlaybackError 上报）。</summary>
        public void Play(byte[] mp3Bytes)
        {
            IPreparedStream? prepared = Prepare(mp3Bytes);
            if (prepared == null) { return; }
            if (!prepared.Commit())
            {
                // Commit 失败时资源已由提交段终态释放；再 Dispose 属幂等无操作
                prepared.Dispose();
            }
        }

        /// <summary>暂停当前播放（无活动流时无操作）。</summary>
        public void Pause()
        {
            _gate.Wait();
            try
            {
                if (_activeHandle != 0)
                {
                    Bass.ChannelPause(_activeHandle);
                }
            }
            finally { _gate.Release(); }
        }

        /// <summary>恢复暂停的播放（无活动流时无操作；BASS 对播放中流重复 Play 无害，restart=false 不从头）。</summary>
        public void Resume()
        {
            _gate.Wait();
            try
            {
                if (_activeHandle != 0)
                {
                    Bass.ChannelPlay(_activeHandle, false);
                }
            }
            finally { _gate.Release(); }
        }

        /// <summary>停止并释放当前活动流（迟到 END SYNC 回调按代际丢弃）。</summary>
        public void Stop()
        {
            _gate.Wait();
            try
            {
                ReleaseActiveUnsafe();
            }
            finally { _gate.Release(); }
        }

        /// <summary>跳转播放位置（clamp 至 [0, Duration)；无活动流时无操作）。</summary>
        public void Seek(TimeSpan pos)
        {
            _gate.Wait();
            try
            {
                int handle = _activeHandle;
                if (handle == 0) { return; }
                long totalBytes = Bass.ChannelGetLength(handle, PositionFlags.Bytes);
                if (totalBytes <= 0) { return; }
                double totalSeconds = Bass.ChannelBytes2Seconds(handle, totalBytes);
                if (totalSeconds <= 0) { return; }
                double clamped = Math.Clamp(pos.TotalSeconds, 0.0, Math.Max(0.0, totalSeconds - 0.05));
                long targetBytes = Bass.ChannelSeconds2Bytes(handle, clamped);
                if (targetBytes < 0) { return; }
                Bass.ChannelSetPosition(handle, targetBytes, PositionFlags.Bytes);
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// 释放全部资源：活动流/END SYNC/pin + BASS 设备（Bass.Free）。进程退出时由 DI 容器调用；
        /// 之后的播放调用安全返回（触发 PlaybackError）。
        /// </summary>
        public void Dispose()
        {
            _gate.Wait();
            try
            {
                if (_disposed) { return; }
                _disposed = true;
                ReleaseActiveUnsafe();
                if (_initialized)
                {
                    Bass.Free();
                    _initialized = false;
                }
            }
            finally { _gate.Release(); }
        }

        // ====== private ======

        /// <summary>Bass.Init 懒初始化（首次 Prepare 时一次；失败不缓存——下次 Prepare 重试）。
        /// BASS 语义：同设备重复 Init 返回 false + BASS_ERROR_ALREADY，视为设备已就绪（闸门 demo 实测）。</summary>
        private bool EnsureInitialized()
        {
            lock (_initLock)
            {
                if (_initialized) { return true; }
                if (Bass.Init(-1, 44100, DeviceInitFlags.Default, IntPtr.Zero))
                {
                    _initialized = true;
                    return true;
                }
                if (Bass.LastError == Errors.Already)
                {
                    _initialized = true;
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// 提交段（PLAN 步骤 5 两阶段交换协议第二步）：_gate 临界区内原子完成——
        /// 移除旧流 END SYNC → free 旧流 → Unpin 旧 pin → 新流提升 → 应用音量 →
        /// 安装带代际编号的新 END SYNC → 起播。
        /// 任一 BASS 调用失败 = 终态：新旧流与 pin 确定性释放 + PlaybackError 上抛，返回 false。
        /// </summary>
        private bool CommitPrepared(BassPreparedStream prepared)
        {
            _gate.Wait();
            try
            {
                if (_disposed)
                {
                    prepared.DiscardUnsafe();
                    RaiseError("播放器已释放，提交被拒绝");
                    return false;
                }
                // 原子抢占预备流（防与并发 Dispose 竞争资源所有权；抢占后资源归本方法处置）
                if (!prepared.TryConsume())
                {
                    return false;
                }

                // 第一步：释放旧流（移除 END SYNC → free 流 → unpin 数据）
                if (_activeHandle != 0)
                {
                    int oldSync = _activeSync;
                    if (oldSync != 0 && !Bass.ChannelRemoveSync(_activeHandle, oldSync))
                    {
                        return FailCommit(prepared, "移除旧流结束回调失败");
                    }
                    if (!Bass.StreamFree(_activeHandle))
                    {
                        return FailCommit(prepared, "释放旧流失败");
                    }
                    if (_activePin.IsAllocated)
                    {
                        _activePin.Free();
                    }
                    _activeHandle = 0;
                    _activeSync = 0;
                }

                // 第二步：新流提升为活动流
                _activeHandle = prepared.Handle;
                _activePin = prepared.Pin;

                // 第三步：应用音量
                if (!Bass.ChannelSetAttribute(_activeHandle, ChannelAttribute.Volume, _volume))
                {
                    return FailCommit(prepared, "设置音量失败");
                }

                // 第四步：安装带代际编号的新 END SYNC（回调闭包捕获编号，比对不一致即迟到回调丢弃）
                int generation = ++_generation;
                int newSync = Bass.ChannelSetSync(_activeHandle, SyncFlags.End, 0,
                    (syncHandle, channel, data, user) =>
                    {
                        // ⚠️ BASS 回调线程：禁止调用 BASS API（死锁），只比对代际后上抛事件
                        if (generation == Volatile.Read(ref _generation))
                        {
                            PlaybackEnded?.Invoke();
                        }
                    });
                if (newSync == 0)
                {
                    return FailCommit(prepared, "安装结束回调失败");
                }
                _activeSync = newSync;

                // 第五步：起播
                if (!Bass.ChannelPlay(_activeHandle, false))
                {
                    return FailCommit(prepared, "起播失败");
                }
                return true;
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// 提交段失败终态：新旧流与 pin 确定性释放（不留半开资源）+ 代际失效 + PlaybackError 上抛。
        /// 只在 _gate 临界区内调用。
        /// </summary>
        private bool FailCommit(BassPreparedStream prepared, string stage)
        {
            string detail = $"{stage}: {Bass.LastError}";
            // 新流若已提升为活动流，其资源随 ReleaseActiveUnsafe 一并释放，勿重复 free
            bool transferred = _activeHandle != 0 && _activeHandle == prepared.Handle;
            ReleaseActiveUnsafe();
            if (!transferred)
            {
                prepared.DiscardUnsafe();
            }
            RaiseError($"切换播放失败（{detail}），已停止");
            return false;
        }

        /// <summary>丢弃预备流（不持 _gate；与 CommitPrepared 经 TryConsume/DiscardUnsafe 的状态锁互斥）。</summary>
        private void DiscardPrepared(BassPreparedStream prepared)
        {
            if (prepared.TryConsume())
            {
                prepared.DiscardUnsafe();
            }
        }

        /// <summary>
        /// 释放当前活动流全套资源（移除 END SYNC → free 流 → unpin 数据 → 代际自增使迟到回调失效）。
        /// 只在 _gate 临界区内调用；释放失败（流已失效等）静默吞掉并继续清理（Stop/终态语义不因 BASS 报错上抛）。
        /// </summary>
        private void ReleaseActiveUnsafe()
        {
            if (_activeHandle != 0)
            {
                int oldSync = _activeSync;
                if (oldSync != 0)
                {
                    Bass.ChannelRemoveSync(_activeHandle, oldSync);
                }
                Bass.StreamFree(_activeHandle);
                _activeHandle = 0;
                _activeSync = 0;
            }
            if (_activePin.IsAllocated)
            {
                _activePin.Free();
            }
            // 代际自增：任何在途旧流 END SYNC 回调到达时编号不匹配，直接丢弃
            Interlocked.Increment(ref _generation);
        }

        /// <summary>触发 PlaybackError（调用线程上抛；订阅方负责切 UI 线程）。</summary>
        private void RaiseError(string message)
        {
            PlaybackError?.Invoke(message);
        }

        /// <summary>
        /// 预备流句柄实现（两阶段交换协议第一步产物）：持临时流句柄与数据 pin，
        /// 状态一次性转换（Prepared → Consumed），Commit/Dispose 谁先抢到谁处置资源。
        /// </summary>
        private sealed class BassPreparedStream : IPreparedStream
        {
            private readonly BassMusicPlayer _owner;
            // 状态锁：串行化 Commit（经 owner 提交段）与 Dispose 的资源所有权竞争
            private readonly object _stateLock = new();
            private bool _consumed;

            /// <summary>临时流 BASS 句柄（提交成功后归 owner 活动流字段管理）。</summary>
            internal int Handle { get; }

            /// <summary>音频数据 pin（提交成功后归 owner 活动流字段管理）。</summary>
            internal GCHandle Pin { get; }

            internal BassPreparedStream(BassMusicPlayer owner, int handle, GCHandle pin)
            {
                _owner = owner;
                Handle = handle;
                Pin = pin;
            }

            /// <summary>校验流可用：状态仍为 Prepared 且能取到有效时长。可重复调用。</summary>
            public bool Validate()
            {
                lock (_stateLock)
                {
                    if (_consumed) { return false; }
                    long bytes = Bass.ChannelGetLength(Handle, PositionFlags.Bytes);
                    if (bytes <= 0) { return false; }
                    return Bass.ChannelBytes2Seconds(Handle, bytes) > 0;
                }
            }

            /// <summary>提交：把本流提升为活动流并起播（转 owner 提交段原子完成）。</summary>
            public bool Commit()
            {
                return _owner.CommitPrepared(this);
            }

            /// <summary>丢弃临时流（当前播放不受影响）；已提交/已丢弃时为幂等无操作。</summary>
            public void Dispose()
            {
                _owner.DiscardPrepared(this);
            }

            /// <summary>原子抢占：Prepared → Consumed。返回 false = 已被 Commit/Dispose 抢先。</summary>
            internal bool TryConsume()
            {
                lock (_stateLock)
                {
                    if (_consumed) { return false; }
                    _consumed = true;
                    return true;
                }
            }

            /// <summary>
            /// 释放临时流资源（free 流 + unpin）。调用前提：已 TryConsume 成功（资源所有权已在调用方）。
            /// 名带 Unsafe 指不自行抢状态锁——持锁调用会与 Validate/竞争方死锁，由调用方编排。
            /// </summary>
            internal void DiscardUnsafe()
            {
                Bass.StreamFree(Handle);
                if (Pin.IsAllocated)
                {
                    Pin.Free();
                }
            }
        }
    }
}
