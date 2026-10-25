using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using ManagedBass;
using MiniPet.Services;
using WzComparerR2.WzLib;

namespace AudioDemo
{
    /// <summary>
    /// BASS Darwin arm64 闸门 demo（PLAN Rev 6 步骤 5 集成闸门）。
    /// 逐项验证并打印 PASS/FAIL（每项一行）：
    /// a) Bass.Init 成功；b) WZ 提取真 MP3；c) 内存 byte[] pin 建流；
    /// d) 播放中 Position 前进；e) Seek 生效；f) 音量属性；g) END SYNC 自然播完回调；
    /// h) 两阶段换流；i) 损坏数据建流失败 + LastError 可读。
    /// 之后用正式类 BassMusicPlayer 做封装冒烟（链接编译自检的运行时部分）。
    /// 全程音量 0.05 避免打扰。
    /// </summary>
    internal static class Program
    {
        // 验证音量（任务要求拉低避免吵到用户）
        private const float VerifyVolume = 0.05f;
        // WZ 数据根目录（SSD 客户端；Sound 数据在 Sound/ 子目录，经 Base.wz 全树挂载访问）
        private const string WzDataDir = "/Volumes/SSD/mxd/mxd/Data";

        private static int _passCount;
        private static int _failCount;

        private static int Main()
        {
            Console.WriteLine($"=== BASS Darwin arm64 闸门 demo（进程架构: {RuntimeInformation.ProcessArchitecture}）===");
            Console.WriteLine($"BASS 版本: {Bass.Version}");

            // ====== a) Bass.Init ======
            bool initOk = Bass.Init(-1, 44100, DeviceInitFlags.Default, IntPtr.Zero);
            Report("a", "Bass.Init(-1, 44100) 成功", initOk, $"LastError={Bass.LastError}");
            if (!initOk)
            {
                // Init 都不过：后续全部无法验证，直接收场（选型危机证据）
                Console.WriteLine("!!! BASS 初始化失败，闸门失败收场");
                return Finish();
            }

            // ====== b) WZ 提取真 MP3 ======
            byte[]? mp3 = ExtractSleepyWood();
            Report("b", "WZ 提取 Bgm00.img/SleepyWood MP3 字节流", mp3 != null && mp3.Length > 0,
                mp3 == null ? "提取失败" : $"{mp3.Length} 字节, 头4字节={mp3[0]:X2} {mp3[1]:X2} {mp3[2]:X2} {mp3[3]:X2}");
            if (mp3 == null || mp3.Length == 0)
            {
                Console.WriteLine("!!! WZ 提取失败，c-i 全部无法验证，收场");
                return Finish();
            }

            // ====== c) 内存 pin 建流 ======
            GCHandle pin = GCHandle.Alloc(mp3, GCHandleType.Pinned);
            int streamA = Bass.CreateStream(pin.AddrOfPinnedObject(), 0, mp3.Length, BassFlags.Default);
            Report("c", "CreateStream(pin(byte[])) 成功", streamA != 0, $"LastError={Bass.LastError}");
            if (streamA == 0)
            {
                pin.Free();
                return Finish();
            }

            // 音量拉低再播
            Bass.ChannelSetAttribute(streamA, ChannelAttribute.Volume, VerifyVolume);

            // ====== d) Position 随时间前进 ======
            bool dOk = false;
            string dInfo = "";
            if (Bass.ChannelPlay(streamA, false))
            {
                Thread.Sleep(1200);
                double pos1 = GetSeconds(streamA);
                Thread.Sleep(1200);
                double pos2 = GetSeconds(streamA);
                dOk = pos2 > pos1 && pos1 >= 0;
                dInfo = $"pos1={pos1:F2}s pos2={pos2:F2}s";
            }
            else
            {
                dInfo = $"ChannelPlay 失败: {Bass.LastError}";
            }
            Report("d", "播放中 Position 随时间前进", dOk, dInfo);

            // ====== e) Seek 生效 ======
            long totalBytes = Bass.ChannelGetLength(streamA, PositionFlags.Bytes);
            double totalSec = Bass.ChannelBytes2Seconds(streamA, totalBytes);
            double target = totalSec / 2;
            bool eOk = false;
            string eInfo = $"duration={totalSec:F2}s";
            if (totalSec > 0)
            {
                long targetBytes = Bass.ChannelSeconds2Bytes(streamA, target);
                if (Bass.ChannelSetPosition(streamA, targetBytes, PositionFlags.Bytes))
                {
                    Thread.Sleep(150);
                    double actual = GetSeconds(streamA);
                    eOk = Math.Abs(actual - target) < 1.5;
                    eInfo += $" seek→{target:F2}s 实测={actual:F2}s";
                }
                else
                {
                    eInfo += $" ChannelSetPosition 失败: {Bass.LastError}";
                }
            }
            Report("e", "Seek 跳转生效", eOk, eInfo);

            // ====== f) 音量属性 ======
            bool fOk = Bass.ChannelSetAttribute(streamA, ChannelAttribute.Volume, VerifyVolume);
            float volRead = -1f;
            if (fOk)
            {
                fOk = Bass.ChannelGetAttribute(streamA, ChannelAttribute.Volume, out volRead)
                      && Math.Abs(volRead - VerifyVolume) < 0.01f;
            }
            Report("f", "ChannelSetAttribute 音量写读一致", fOk, $"写入={VerifyVolume} 读回={volRead:F3}");

            // ====== g) END SYNC 自然播完回调（seek 到接近末尾加速验证）=====
            bool gFired = false;
            var gDone = new ManualResetEventSlim(false);
            int syncG = Bass.ChannelSetSync(streamA, SyncFlags.End, 0,
                (h, c, d, u) =>
                {
                    gFired = true;
                    gDone.Set();
                });
            bool gOk = syncG != 0;
            string gInfo = gOk ? "" : $"ChannelSetSync 失败: {Bass.LastError}";
            if (gOk)
            {
                long nearEnd = Bass.ChannelSeconds2Bytes(streamA, Math.Max(0, totalSec - 0.8));
                Bass.ChannelSetPosition(streamA, nearEnd, PositionFlags.Bytes);
                gOk = gDone.Wait(8000) && gFired;
                gInfo = gFired ? "回调已触发" : "8 秒内未触发";
            }
            Report("g", "END SYNC 自然播完回调触发", gOk, gInfo);

            // ====== h) 两阶段换流：建第二个流 → free 第一个 → 播第二个 ======
            int streamB = Bass.CreateStream(pin.AddrOfPinnedObject(), 0, mp3.Length, BassFlags.Default);
            bool hOk = streamB != 0;
            string hInfo = hOk ? "" : $"第二流创建失败: {Bass.LastError}";
            if (hOk)
            {
                Bass.ChannelSetAttribute(streamB, ChannelAttribute.Volume, VerifyVolume);
                bool freeA = Bass.StreamFree(streamA);
                bool playB = Bass.ChannelPlay(streamB, false);
                hOk = freeA && playB;
                hInfo = $"free(旧)={freeA} play(新)={playB} 新流活跃={Bass.ChannelIsActive(streamB) == ManagedBass.PlaybackState.Playing}";
            }
            Report("h", "两阶段换流（建新→free旧→播新）", hOk, hInfo);

            // ====== i) 损坏数据建流失败 + LastError 可读 ======
            byte[] garbage = new byte[4096];
            new Random(42).NextBytes(garbage);
            GCHandle garbagePin = GCHandle.Alloc(garbage, GCHandleType.Pinned);
            int badStream = Bass.CreateStream(garbagePin.AddrOfPinnedObject(), 0, garbage.Length, BassFlags.Default);
            garbagePin.Free();
            bool iOk = badStream == 0;
            string iInfo = "";
            if (iOk)
            {
                Errors err = Bass.LastError;
                iOk = err != Errors.Unknown && Enum.IsDefined(err);
                iInfo = $"返回 0, LastError={err}";
            }
            else
            {
                iInfo = "损坏数据意外建流成功";
                Bass.StreamFree(badStream);
            }
            Report("i", "损坏数据建流失败且 LastError 可读", iOk, iInfo);

            // 清理裸 API 段资源，再进入封装冒烟
            Bass.StreamFree(streamB);
            pin.Free();

            // ====== 封装冒烟：正式类 BassMusicPlayer（链接编译自检的运行时部分）======
            RunPlayerSmokeTest(mp3);

            Bass.Free();
            return Finish();
        }

        // ====== private ======

        /// <summary>用正式类 BassMusicPlayer 冒烟：Play / Duration / Seek / 两阶段 Prepare+Commit 换流 / 错误事件 / Stop。</summary>
        private static void RunPlayerSmokeTest(byte[] mp3)
        {
            Console.WriteLine("--- 封装冒烟: BassMusicPlayer（正式类，两阶段交换协议）---");
            using (var player = new BassMusicPlayer())
            {
                player.Volume = VerifyVolume;
                string? lastError = null;
                player.PlaybackError += msg =>
                {
                    lastError = msg;
                    Console.WriteLine($"    [PlaybackError 事件] {msg}");
                };
                int endedCount = 0;
                var ended = new ManualResetEventSlim(false);
                player.PlaybackEnded += () =>
                {
                    Interlocked.Increment(ref endedCount);
                    ended.Set();
                };

                // 播放 + Duration/Position
                player.Play(mp3);
                Console.WriteLine($"    Play 后: IsPlaying={player.IsPlaying} Duration={player.Duration:hh\\:mm\\:ss\\.f}");
                Thread.Sleep(1500);
                Console.WriteLine($"    1.5s 后 Position={player.Position:hh\\:mm\\:ss\\.f}");

                // Seek 中点
                if (player.Duration is TimeSpan dur)
                {
                    player.Seek(TimeSpan.FromMilliseconds(dur.TotalMilliseconds / 2));
                    Thread.Sleep(150);
                    Console.WriteLine($"    Seek 中点后 Position={player.Position:hh\\:mm\\:ss\\.f}（目标 {dur.TotalMilliseconds / 2:F0}ms）");
                }

                // 两阶段换流（Prepare → 旧流仍在播 → Commit 原子替换）
                bool oldStillPlayingBeforeCommit = player.IsPlaying;
                IPreparedStream? prepared = player.Prepare(mp3);
                bool validateOk = prepared != null && prepared.Validate();
                bool commitOk = prepared != null && prepared.Commit();
                Console.WriteLine($"    两阶段换流: Prepare 成功={prepared != null} Validate={validateOk} 旧流提交前仍在播={oldStillPlayingBeforeCommit} Commit={commitOk} 新流播放={player.IsPlaying}");

                // 自然播完事件（seek 到接近末尾加速）
                if (player.Duration is TimeSpan dur2)
                {
                    player.Seek(TimeSpan.FromMilliseconds(Math.Max(0, dur2.TotalMilliseconds - 800)));
                    bool fired = ended.Wait(8000);
                    Console.WriteLine($"    自然播完事件: {fired}（计数 {endedCount}）");
                }

                // 损坏数据 Prepare 失败 + 错误事件
                byte[] garbage = new byte[2048];
                lastError = null;
                IPreparedStream? bad = player.Prepare(garbage);
                Console.WriteLine($"    损坏数据 Prepare: 返回 null={bad == null} PlaybackError 事件={(lastError != null ? "已触发" : "未触发")}");

                // Stop → Position/Duration 为 null
                player.Stop();
                Console.WriteLine($"    Stop 后: IsPlaying={player.IsPlaying} Position={player.Position?.ToString() ?? "null"} Duration={player.Duration?.ToString() ?? "null"}");
            }
            Console.WriteLine("--- 封装冒烟结束（Dispose 已清理全部流/pin/sync）---");
        }

        /// <summary>从 WZ 提取 Bgm00.img/SleepyWood（照抄 WzService.LoadWz + 设计 §11.7 探针路径），写 /tmp/a1-test.mp3 备用。</summary>
        private static byte[]? ExtractSleepyWood()
        {
            try
            {
                // 照抄 WzService.LoadWz：自动探测 Base 子目录（LoadWzFolder 经 Base.wz 挂载全树含 Sound 节点）
                var wzFolder = WzDataDir;
                var baseSubDir = System.IO.Path.Combine(WzDataDir, "Base");
                if (System.IO.Directory.Exists(baseSubDir) && System.IO.File.Exists(System.IO.Path.Combine(baseSubDir, "Base.wz")))
                {
                    wzFolder = baseSubDir;
                }
                var wzs = new Wz_Structure();
                Wz_Structure.DefaultAutoDetectExtFiles = true;
                Wz_Structure.DefaultImgCheckDisabled = true;
                wzs.LoadWzFolder(wzFolder, ref wzs.WzNode, true);
                Console.WriteLine($"    [WZ] 加载 {wzFolder} 完成, imgs: {wzs.img_number}");
                // ⚠️ 路径必须带 Sound/ 前缀（探针实证：BgmXX.img 挂在 Sound 节点下）
                var imgNode = wzs.WzNode?.FindNodeByPath("Sound\\Bgm00.img", true);
                if (imgNode == null) { Console.Error.WriteLine("    [WZ] Sound/Bgm00.img 节点未找到"); return null; }
                var img = imgNode.GetValue<Wz_Image>();
                if (img == null || !img.TryExtract()) { Console.Error.WriteLine("    [WZ] Bgm00.img 提取失败"); return null; }
                // ⚠️ 子节点必须读 img.Node.Nodes（外层 node.Nodes 恒 0）
                Wz_Node? trackNode = null;
                foreach (Wz_Node child in img.Node.Nodes)
                {
                    if (child.Text == "SleepyWood") { trackNode = child; break; }
                }
                var sound = trackNode?.GetValue<Wz_Sound>();
                if (sound == null) { Console.Error.WriteLine("    [WZ] SleepyWood 声音节点未找到"); return null; }
                byte[] data = sound.ExtractSound();
                if (data.Length > 0)
                {
                    System.IO.File.WriteAllBytes("/tmp/a1-test.mp3", data);
                    Console.WriteLine("    [WZ] 已写 /tmp/a1-test.mp3");
                }
                return data;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"    [WZ] 提取异常: {ex.Message}");
                return null;
            }
        }

        /// <summary>取流当前位置（秒），失败返回 -1。</summary>
        private static double GetSeconds(int handle)
        {
            long bytes = Bass.ChannelGetPosition(handle, PositionFlags.Bytes);
            if (bytes < 0) { return -1; }
            return Bass.ChannelBytes2Seconds(handle, bytes);
        }

        /// <summary>打印单项结果（每项一行）并计数。</summary>
        private static void Report(string id, string name, bool ok, string detail)
        {
            if (ok)
            {
                _passCount++;
            }
            else
            {
                _failCount++;
            }
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} [{id}] {name} — {detail}");
        }

        /// <summary>收场打印与退出码（0=全过，1=有失败）。</summary>
        private static int Finish()
        {
            Console.WriteLine($"=== 闸门结果: {_passCount} PASS / {_failCount} FAIL ===");
            return _failCount == 0 ? 0 : 1;
        }
    }
}
