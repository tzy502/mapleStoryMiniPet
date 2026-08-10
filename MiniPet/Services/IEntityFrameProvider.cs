using System.Collections.Generic;
using SkiaSharp;

namespace MiniPet.Services;

/// <summary>
/// 实体帧提供者（ADR-0005）：AnimService 的帧内容来源抽象。
/// Mob/NPC 走精灵图条裁剪（现有路径）；纸娃娃走实时合成（PaperdollService）。
/// 当 AnimService 当前 mobId 等于纸娃娃哨兵且注册了 provider 时，Tick 帧由 provider 合成。
/// </summary>
public interface IEntityFrameProvider
{
    /// <summary>该动作是否存在（帧数 &gt; 0）。</summary>
    bool HasAction(string action);

    /// <summary>该动作帧数。</summary>
    int GetFrameCount(string action);

    /// <summary>某帧延迟（毫秒）。</summary>
    int GetFrameDelay(string action, int frame);

    /// <summary>可用动作列表（空闲随机等）。</summary>
    List<string> GetAvailableActions();

    /// <summary>把请求动作解析为实际播放动作（stand→stand1/stand2 武器变体、move→walk1、fly→jump 等）。</summary>
    string ResolveAction(string requested);

    /// <summary>
    /// 合成某帧。返回帧位图 + 公共 origin + 帧尺寸（union canvas 公共锚点，窗口稳定）。
    /// 返回 Frame 为 null 表示无帧（调用方应回退）。
    /// </summary>
    (SKBitmap? Frame, int OriginX, int OriginY, int FrameW, int FrameH) RenderFrame(string action, int frame);
}
