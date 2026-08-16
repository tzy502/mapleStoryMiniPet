using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MiniPet;
using MiniPet.Models;
using MiniPet.Services;
using MiniPet.Services.Adapters;

namespace MiniPet.CrashSelfTest;

/// <summary>
/// DSH（DeepSeek Harness）连接验证探针（StartupObject 模式，与 IconProbe 同款）：
///   1) 构造 DshAdapter 连 ws://127.0.0.1:3080/api/events.mux，订阅 4 秒收帧，
///      确认收到 mux 帧（session/subscribed / session/event / session/projection 等）并统计帧类型；
///   2) HTTP RPC session.list —— 打印会话数 + 首个会话 id；
///   3) HTTP RPC session.create（sessionId=minipet_dsh_selftest，独立会话，不碰用户会话）——
///      成功打印会话 id，失败打印原因；
///   4) （可选 --prompt）session.prompt 无害文本 "ping" → 打印 accepted（验证对话 RPC 链路；
///      默认不发，避免消耗模型 token）。
/// 退出码：0 = 全部通过；1 = 任一阶段失败。
/// 用法（StartupObject 模式）：
///   dotnet run --project MiniPet.CrashSelfTest -p:UsedAvaloniaProducts= -p:StartupObject=MiniPet.CrashSelfTest.DshProbe [--prompt]
/// </summary>
public static class DshProbe
{
    private const string BaseUrl = "http://127.0.0.1:3080";          // 锁本机 loopback（免鉴权）
    private const string WsUrl = "ws://127.0.0.1:3080/api/events.mux";
    private const string SelfTestSessionId = "minipet_dsh_selftest"; // 独立验证会话，绝不碰用户会话

    private static readonly HttpClient s_http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

    public static int Main(string[] args)
    {
        // DshAdapter 内部用 DispatcherTimer + PublishOnUIThread，需 Avalonia Dispatcher（同 IconProbe 初始化）。
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        var exitCode = 0;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                exitCode = await RunAsync(args).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"=== DshProbe 失败：{ex.GetType().Name}: {ex.Message}");
                exitCode = 1;
            }
            Environment.Exit(exitCode);
        });
        Dispatcher.UIThread.MainLoop(CancellationToken.None);
        return exitCode; // 不会走到（Environment.Exit）
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var wantPrompt = args.Contains("--prompt", StringComparer.Ordinal);
        var failures = 0;
        Console.WriteLine("[DshProbe] DSH 连接验证开始（本机 127.0.0.1:3080）");

        // ── 阶段 1：DshAdapter 连 mux，订阅 4 秒收帧 ──
        Console.WriteLine($"[DshProbe] 阶段 1：DshAdapter 连接 {WsUrl}，订阅 4 秒收帧…");
        var frameTypes = new ConcurrentDictionary<string, int>();
        var connected = false;
        var adapter = new DshAdapter(new EventBus(), new AgentIntegrationConfig());
        adapter.ConnStatusCallback += (text, ok) =>
        {
            connected = ok;
            Console.WriteLine($"[DshProbe] ConnStatus: {(ok ? "🟢" : "🔴")} {text}");
        };
        adapter.MuxFrameReceived += type => frameTypes.AddOrUpdate(type, 1, (_, c) => c + 1);
        adapter.Start();
        await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
        adapter.Stop();

        if (!connected)
        {
            Console.WriteLine("[DshProbe] ✗ DshAdapter 未能连接 events.mux（DSH 是否在 3080 运行？）");
            failures++;
        }
        else if (frameTypes.IsEmpty)
        {
            Console.WriteLine("[DshProbe] ✗ 已连接但 4 秒内未收到任何 mux 帧");
            failures++;
        }
        else
        {
            Console.WriteLine("[DshProbe] ✓ 收到 mux 帧（4 秒统计）：");
            foreach (var kv in frameTypes.OrderByDescending(k => k.Value))
            {
                Console.WriteLine($"      {kv.Key,-28} × {kv.Value}");
            }
        }

        // ── 阶段 2：session.list（观察通道 RPC） ──
        Console.WriteLine("[DshProbe] 阶段 2：HTTP RPC session.list…");
        var (listOk, listErr, listValue) = await PostRpcAsync("session.list", new { }).ConfigureAwait(false);
        if (listOk)
        {
            var (count, firstId) = ParseSessionList(listValue);
            Console.WriteLine($"  ✓ session.list 成功：会话数 = {count}" + (string.IsNullOrEmpty(firstId) ? string.Empty : $"，首个会话 = {firstId}"));
        }
        else
        {
            Console.WriteLine($"  ✗ session.list 失败：{listErr}");
            failures++;
        }

        // ── 阶段 3：session.create（独立 selftest 会话，幂等复用） ──
        Console.WriteLine($"[DshProbe] 阶段 3：HTTP RPC session.create（sessionId={SelfTestSessionId}，独立会话）…");
        var (createOk, createErr, createValue) = await PostRpcAsync("session.create", new
        {
            cwd = Environment.CurrentDirectory,
            sessionId = SelfTestSessionId,
        }).ConfigureAwait(false);
        if (createOk)
        {
            var createdId = ExtractSessionId(createValue);
            Console.WriteLine($"  ✓ session.create 成功：会话 id = {createdId ?? SelfTestSessionId}");
        }
        else
        {
            Console.WriteLine($"  ✗ session.create 失败：{createErr}");
            failures++;
        }

        // ── 阶段 4（可选）：session.prompt 无害 "ping"（验证对话 RPC 链路） ──
        if (wantPrompt)
        {
            Console.WriteLine("[DshProbe] 阶段 4：HTTP RPC session.prompt（text=\"ping\"，mode=queue）…");
            var (promptOk, promptErr, _) = await PostRpcAsync("session.prompt", new
            {
                sessionId = SelfTestSessionId,
                mode = "queue",
                content = new object[] { new { type = "text", text = "ping" } },
            }).ConfigureAwait(false);
            if (promptOk)
            {
                Console.WriteLine("  ✓ session.prompt accepted（对话 RPC 链路可用；回复经 WS mux 增量推送）");
            }
            else
            {
                Console.WriteLine($"  ✗ session.prompt 失败：{promptErr}");
                failures++;
            }
        }
        else
        {
            Console.WriteLine("[DshProbe] 阶段 4：跳过（默认不发 prompt，避免消耗模型 token；加 --prompt 可验证）");
        }

        Console.WriteLine(failures == 0
            ? "=== DshProbe 全部通过 ==="
            : $"=== DshProbe 失败：{failures} 处 ===");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>RPC 信封：POST /api/{method}，body {type:client-request, rpcId, method, payload}；回包 result.ok。</summary>
    private static async Task<(bool Ok, string? Error, string? ValueJson)> PostRpcAsync(string method, object payload)
    {
        var body = JsonSerializer.Serialize(new
        {
            type = "client-request",
            rpcId = "dshprobe-" + Guid.NewGuid().ToString("N"),
            method,
            payload,
        });
        try
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await s_http.PostAsync($"{BaseUrl}/api/{method}", content).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
            {
                var ok = result.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
                string? err = null;
                if (!ok && result.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.Object)
                {
                    err = errEl.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : "rpc failed";
                }
                var valueJson = result.TryGetProperty("value", out var v) ? v.GetRawText() : null;
                return (ok, err, valueJson);
            }
        }
        catch (HttpRequestException ex)
        {
            return (false, $"HTTP 请求失败：{ex.Message}", null);
        }
        catch (JsonException)
        {
            // 回包不可解析 → 按失败
        }
        return (false, "bad response", null);
    }

    /// <summary>解析 session.list 的 value：{items:[{sessionId,...}]} → (会话数, 首个会话 id)。</summary>
    private static (int Count, string? FirstId) ParseSessionList(string? valueJson)
    {
        if (string.IsNullOrEmpty(valueJson)) { return (0, null); }
        try
        {
            using var doc = JsonDocument.Parse(valueJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return (0, null);
            }
            string? firstId = null;
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("sessionId", out var sid) && sid.ValueKind == JsonValueKind.String)
                {
                    firstId = sid.GetString();
                    break;
                }
            }
            return (items.GetArrayLength(), firstId);
        }
        catch (JsonException)
        {
            return (0, null);
        }
    }

    /// <summary>解析 session.create 的 value：{sessionId,...} → sessionId 字符串。</summary>
    private static string? ExtractSessionId(string? valueJson)
    {
        if (string.IsNullOrEmpty(valueJson)) { return null; }
        try
        {
            using var doc = JsonDocument.Parse(valueJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("sessionId", out var sid) && sid.ValueKind == JsonValueKind.String)
            {
                return sid.GetString();
            }
        }
        catch (JsonException)
        {
            // 解析失败返回 null
        }
        return null;
    }
}
