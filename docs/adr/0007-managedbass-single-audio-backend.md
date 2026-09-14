# ADR-0007: 音乐播放后端采用 ManagedBass 单实现

- **Status**: Accepted
- **Date**: 2026-08-27
- **Related**: [ADR-0001](0001-three-window-layered-architecture.md)（分层先例）、`docs/ai/software-design.md` §11.4（本 ADR 修订其实现选型结论）

## Context

V0.2.0 音乐模块（F15-F17）需要一个从内存 MP3 byte[] 播放的后端。设计文档 §11.4 原结论为双实现：macOS 用 AVFoundation（手写 objc_msgSend）、Windows 用 NAudio。

决策时点的实测事实：

- R2 参考渲染器（`maplestory-wiki-backend/WzComparerR2C#/WzComparerR2.MapRender/Music.cs`）已用 **ManagedBass 3.1.1** 验证了与需求完全同构的能力面：`Bass.CreateStream(pinnedPtr, 0, len)` 内存建流 → Play/Pause/Stop/Volume/IsPaused，逐项对应 IMusicPlayer 接口。
- BASS 库本身跨平台（Windows/macOS 官方均有原生二进制），同一份 C# 封装代码在两个平台运行，仅原生动态库不同。
- 项目 `Utils/MacNative.cs` 现有 objc_msgSend 仅 IntPtr+int 重载；AVFoundation 路线需新增对象返回、字符串选择器等多个重载，调试成本最高且无法离机验证。
- NAudio 引入后在本机无 Windows 实机可测，等于交付一个从未运行过的实现。
- BASS 为闭源免费库（非商用免费）；MiniPet 是个人非商用项目。

## Decision

**用户拍板（grill 会话，2026-08-27）：双平台交付 + 参考 R2 现成代码 → 单一 ManagedBass 实现覆盖两个平台。**

1. 只写一个 `IMusicPlayer` 实现：`BassMusicPlayer`，代码模式照搬 R2 `Music.cs`（GCHandle pin byte[] → CreateStream），macOS 上完成全部实测验收。
2. 放弃 AVFoundation 手写 objc 路线与 NAudio 双实现路线。设计 §11.4 的"按 OperatingSystem 注入不同实现"不再需要——注入点保留（接口不变），但两平台指向同一个类。
3. 原生二进制分发（bass.dll / libbass.dylib）随输出目录拷贝，具体机制实现期实测敲定；编译期两边都必须过 0 错误。
4. Windows 本轮不做实机听感验收：代码路径与 macOS 完全一致、编译通过即为交付标准，实机验证留待有 Windows 环境时执行。

## Consequences

- 引入闭源第三方原生依赖（此前项目原生依赖为零）；若未来转商用需购买 BASS 授权或整体替换实现——替换成本被 IMusicPlayer 接口隔离在一个类内。
- 换取：零 objc 手写桥接、单一代码路径两平台一致、Seek/Position/Duration 能力由 BASS 内建而非自研。

## Amendment (2026-08-27, Codex review round 3)

IMusicPlayer 在 §11.4 原签名之上扩展**预备流能力**：`Prepare(byte[]) → IPreparedStream`（可校验、`Commit()` 提升为活动流 / `Dispose()` 丢弃），同步 `Play(byte[])` 成为 Prepare+Commit 便利方法。原因：场景联动要求「新流验证通过前旧播放入毫发无损」，单条 void Play 无法表达建好未提交的中间态。
