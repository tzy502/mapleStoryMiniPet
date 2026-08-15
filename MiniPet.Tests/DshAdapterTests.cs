using System;
using System.Text.Json;
using MiniPet.Services;
using MiniPet.Services.Adapters;

namespace MiniPet.Tests;

/// <summary>
/// DshAdapter 白盒单元测试（RESEARCH-dsh-http-bridge.md P2）。
/// 直接调 internal 纯函数（MapSessionEventState / MapChunkState / MapTurnEndState / ToolResultIsError /
/// ExtractChunkText / ExtractAssistantText），帧 JSON 全部来自 2026-08-15 本机实测抓包校准。
/// 验证：
///   1) 事件 → 7 态映射（turn/start→Run、reasoning-delta→Review、tool/result isError→Failed、
///      turn/end completed→Wave、error→Failed、aborted→Idle、approval→Waiting）
///   2) 对话文本提取（text-delta 增量 / assistant/message 终稿排除 reasoning 块）
///   3) 未知事件不映射（null）
/// 数据全部构造，不连真实 DSH、不碰 DispatcherTimer。
/// </summary>
public class DshAdapterTests
{
    /// <summary>解析 JSON 字符串为独立 JsonElement（Clone 脱离文档生命周期）。</summary>
    private static JsonElement El(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // ── 事件 → 7 态映射（实测帧校准） ──

    [Fact]
    public void TurnStart_映射_Run()
    {
        var st = DshAdapter.MapSessionEventState("turn/start", El("{\"turn\":1}"));
        Assert.Equal(AgentState.Run, st);
    }

    [Fact]
    public void StepStart_和_ToolCall_和_UserMessage_映射_Run()
    {
        Assert.Equal(AgentState.Run, DshAdapter.MapSessionEventState("step/start", El("{\"turn\":1,\"step\":2}")));
        Assert.Equal(AgentState.Run, DshAdapter.MapSessionEventState("tool/call", El("{\"turn\":1,\"step\":2,\"callId\":\"c1\",\"name\":\"bash\",\"arguments\":\"{}\"}")));
        Assert.Equal(AgentState.Run, DshAdapter.MapSessionEventState("user/message", El("{\"id\":\"m1\",\"role\":\"user\",\"content\":[],\"source\":{\"kind\":\"user\"}}")));
    }

    [Fact]
    public void ReasoningDelta_映射_Review()
    {
        // 实测帧：assistant/chunk reasoning-delta（思考中）。
        var st = DshAdapter.MapSessionEventState("assistant/chunk", El("{\"turn\":1,\"step\":16,\"chunk\":{\"type\":\"reasoning-delta\",\"index\":0,\"text\":\" deleted\"}}"));
        Assert.Equal(AgentState.Review, st);
    }

    [Fact]
    public void TextDelta_映射_Run_且提取增量文本()
    {
        var data = El("{\"turn\":1,\"step\":17,\"chunk\":{\"type\":\"text-delta\",\"index\":1,\"text\":\"hello\"}}");
        Assert.Equal(AgentState.Run, DshAdapter.MapSessionEventState("assistant/chunk", data));
        Assert.Equal("hello", DshAdapter.ExtractChunkText(data));
    }

    [Fact]
    public void Chunk_非增量类型_不映射()
    {
        // block-start / block-end / usage / finish 不改变态（text 为空）。
        Assert.Null(DshAdapter.MapChunkState(El("{\"chunk\":{\"type\":\"block-start\",\"index\":0,\"blockType\":\"text\"}}")));
        Assert.Null(DshAdapter.MapChunkState(El("{\"chunk\":{\"type\":\"block-end\",\"index\":0,\"block\":{\"type\":\"text\",\"text\":\"x\"}}}")));
        Assert.Null(DshAdapter.MapChunkState(El("{\"chunk\":{\"type\":\"finish\",\"reason\":\"stop\"}}")));
        Assert.Equal(string.Empty, DshAdapter.ExtractChunkText(El("{\"chunk\":{\"type\":\"reasoning-delta\",\"text\":\"x\"}}")));
    }

    [Fact]
    public void ToolResult_成功_映射_Run()
    {
        // 实测帧：tool/result 无 error、content[0] 无 isError。
        var data = El("{\"turn\":1,\"step\":26,\"message\":{\"source\":{\"kind\":\"tool\",\"callId\":\"c1\"},\"content\":[{\"type\":\"tool-result\",\"toolCallId\":\"c1\",\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]}]}}");
        Assert.False(DshAdapter.ToolResultIsError(data));
        Assert.Equal(AgentState.Run, DshAdapter.MapSessionEventState("tool/result", data));
    }

    [Fact]
    public void ToolResult_失败_映射_Failed()
    {
        // isError=true（content[0] 内嵌）。
        var data = El("{\"turn\":1,\"step\":26,\"message\":{\"source\":{\"kind\":\"tool\",\"callId\":\"c1\"},\"content\":[{\"type\":\"tool-result\",\"toolCallId\":\"c1\",\"content\":[{\"type\":\"text\",\"text\":\"boom\"}],\"isError\":true}]}}");
        Assert.True(DshAdapter.ToolResultIsError(data));
        Assert.Equal(AgentState.Failed, DshAdapter.MapSessionEventState("tool/result", data));
    }

    [Fact]
    public void ToolResult_顶层error字段_失败()
    {
        var data = El("{\"turn\":1,\"step\":26,\"message\":{\"source\":{\"kind\":\"tool\",\"callId\":\"c1\"},\"content\":[{\"type\":\"tool-result\",\"toolCallId\":\"c1\",\"content\":[{\"type\":\"text\",\"text\":\"x\"}]}]},\"error\":{\"name\":\"E\",\"code\":\"X\"}}");
        Assert.True(DshAdapter.ToolResultIsError(data));
        Assert.Equal(AgentState.Failed, DshAdapter.MapSessionEventState("tool/result", data));
    }

    [Fact]
    public void TurnEnd_Completed_映射_Wave()
    {
        var data = El("{\"turn\":1,\"reason\":{\"kind\":\"completed\"}}");
        Assert.Equal("completed", DshAdapter.MapTurnEndKind(data));
        Assert.Equal(AgentState.Wave, DshAdapter.MapSessionEventState("turn/end", data));
    }

    [Fact]
    public void TurnEnd_MaxTokens_映射_Wave()
    {
        var data = El("{\"turn\":1,\"reason\":{\"kind\":\"max-tokens\"}}");
        Assert.Equal(AgentState.Wave, DshAdapter.MapSessionEventState("turn/end", data));
    }

    [Fact]
    public void TurnEnd_Error_映射_Failed()
    {
        var data = El("{\"turn\":1,\"reason\":{\"kind\":\"error\",\"error\":{\"message\":\"boom\",\"code\":\"UNKNOWN\"}}}");
        Assert.Equal("error", DshAdapter.MapTurnEndKind(data));
        Assert.Equal(AgentState.Failed, DshAdapter.MapSessionEventState("turn/end", data));
    }

    [Fact]
    public void TurnEnd_Aborted_映射_Idle()
    {
        var data = El("{\"turn\":1,\"reason\":{\"kind\":\"aborted\"}}");
        Assert.Equal("aborted", DshAdapter.MapTurnEndKind(data));
        Assert.Equal(AgentState.Idle, DshAdapter.MapSessionEventState("turn/end", data));
    }

    [Fact]
    public void TurnEnd_无reason字段_不崩溃且Kind空()
    {
        var data = El("{\"turn\":1}");
        Assert.Equal(string.Empty, DshAdapter.MapTurnEndKind(data));
        Assert.Equal(AgentState.Idle, DshAdapter.MapSessionEventState("turn/end", data));
    }

    [Fact]
    public void Approval_映射_Waiting_和_Run()
    {
        Assert.Equal(AgentState.Waiting, DshAdapter.MapSessionEventState("approval/asked", El("{\"id\":\"a1\"}")));
        Assert.Equal(AgentState.Run, DshAdapter.MapSessionEventState("approval/decided", El("{\"id\":\"a1\"}")));
    }

    [Fact]
    public void 未知事件_不映射_null()
    {
        // assistant/message / step/end / todo/write 等不改变态。
        Assert.Null(DshAdapter.MapSessionEventState("assistant/message", El("{\"turn\":1,\"step\":1}")));
        Assert.Null(DshAdapter.MapSessionEventState("step/end", El("{\"turn\":1,\"step\":1}")));
        Assert.Null(DshAdapter.MapSessionEventState("todo/write", El("{\"todos\":[]}")));
        Assert.Null(DshAdapter.MapSessionEventState("unknown/event", El("{}")));
    }

    // ── 对话文本提取（隐私：只取 text 块，reasoning 不进回复） ──

    [Fact]
    public void AssistantMessage_提取_text块_排除reasoning()
    {
        // 实测帧形态：assistant/message content 含 reasoning + text 块。
        var data = El("{\"turn\":1,\"step\":26,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"reasoning\",\"text\":\"思考过程\"},{\"type\":\"text\",\"text\":\"第一段\"},{\"type\":\"text\",\"text\":\"第二段\"}]}}");
        Assert.Equal("第一段\n第二段", DshAdapter.ExtractAssistantText(data));
    }

    [Fact]
    public void AssistantMessage_无text块_返回空()
    {
        var data = El("{\"turn\":1,\"step\":26,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"reasoning\",\"text\":\"思考过程\"}]}}");
        Assert.Equal(string.Empty, DshAdapter.ExtractAssistantText(data));
    }

    [Fact]
    public void 坏数据_不崩溃()
    {
        // 非对象 / 缺字段 → 安全空值（turn/start 不查 data，恒 Run）。
        Assert.Equal(AgentState.Run, DshAdapter.MapSessionEventState("turn/start", El("[]")));
        Assert.Null(DshAdapter.MapSessionEventState("unknown/event", El("[]")));
        Assert.Null(DshAdapter.MapChunkState(El("[]")));
        Assert.Equal(string.Empty, DshAdapter.ExtractChunkText(El("{}")));
        Assert.Equal(string.Empty, DshAdapter.ExtractAssistantText(El("{}")));
        Assert.False(DshAdapter.ToolResultIsError(El("{}")));
    }
}
