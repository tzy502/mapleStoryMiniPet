# Hermes → MiniPet 桌宠接入指南

> 依据锁定计划 **PLAN.md（Rev 3，Hermes 客户端 surface）** 与 **ADR-0006（0006-hermes-api-server-client-surface.md）**。全中文。

## 一句话

MiniPet 是 **Hermes 客户端 surface**：点桌宠弹出迷你对话框，输入发给 Hermes agent（**api_server**），SSE 事件流实时驱动桌宠 7 态动画 + 回复显示。api_server（默认 8642）是 Hermes **官方唯一外部客户端通道**（Open WebUI 等外部客户端同款接入方式）。

## 必须配置（一次性，否则连不上）

### 1. Hermes 侧启用 api_server

Hermes 的 api_server 平台默认不跑，需启用 + 重启：

1. 设 `API_SERVER_KEY` 环境变量（本地凭证）：
   ```bash
   export API_SERVER_KEY=$(openssl rand -hex 16)
   ```
2. 确认 config.yaml `platforms.api_server`（`max_concurrent_runs` 等）。
3. 以 **gateway 模式** 启动 Hermes（api_server 是 gateway 适配器，`hermes serve` 起的是 dashboard 不跑 api_server）：
   ```bash
   hermes gateway start
   ```
4. 验证：`curl -H "Authorization: Bearer $API_SERVER_KEY" http://127.0.0.1:8642/health`

> 本机 8642 默认监听 127.0.0.1，锁死本机（远程直连被 Hermes 非 loopback OAuth gate 阻断，走 SSH 隧道）。

### 2. MiniPet 侧配置

设置中心 → 🤖 Agent 接入：

| 配置项 | 说明 | 默认 |
|--------|------|------|
| 端口 | 0 = 自动发现（默认 8642） | 0 |
| API Server Key | `API_SERVER_KEY` 值（Bearer，明文=本地凭证，仅本机勿备份同步） | 空 |
| Profile | 留空 = 默认 profile（非空走 `/p/{profile}` + 该 profile key） | 空 |
| 7 态映射 | 每个 AgentState 的动作名 + 气泡文本（用户自选动画） | 内置默认 |

## 用法

1. 右键桌宠 → 「💬 对话」弹出迷你对话框。
2. 输入消息 → 发送 → `POST /api/sessions/{id}/chat/stream`（建会话 `POST /api/sessions`，409/已存在即复用）。
3. SSE 事件流实时驱动桌宠动画：`tool.started`→Run、`_thinking`→Review、`error`→Failed、`run.completed`(成功)→Jump 庆祝→Wave、`done`→Idle；回复文本 `assistant.delta` 实时滚动 + 终稿替换。
4. 多轮对话复用同一 session（连续上下文）。

## 事件 → 状态 → 动画映射

| SSE 事件 | AgentState | 默认动画 |
|----------|-----------|---------|
| `run.started` / `tool.started` / `message.started` | Run | move |
| `tool.progress`（`_thinking`） | Review | 思考中…气泡 |
| `tool.failed` / `error` | Failed | 出错了…气泡 |
| `assistant.completed` / `run.completed`（成功） | Jump → Wave | 搞定! / 完成气泡 |
| `done` | Idle | stand |
| （SSE 无信号） | Waiting | 等你?（配置映射） |

> 全部 7 态映射可在设置页自定义（动作名 + 气泡文本）。

## 安全说明

- `API_SERVER_KEY` 明文存 MiniPet 配置 = **本地 gateway 凭证**，仅本机使用，勿备份/同步（Codex 审查建议优先 Keychain，实现不可行则明文+本文档警示）。
- 绝不 log/persist 事件内容（reasoning/content），只映射状态。

## 故障排查

| 症状 | 排查 |
|------|------|
| 发送无反应 / 状态红 | api_server 启用了吗（`hermes gateway start`）？API_SERVER_KEY 配了吗？8642 监听了吗？ |
| 一直 Idle | 未发起对话（空 session）；或 SSE 流无事件 |
| 取消后仍跑 | HTTP abort 为尽力而为，Hermes 端 turn 可能继续（文档诚实标注） |
| 动画不对 | 设置页 7 态映射改了？ |

## 历史否决记录（ADR-0006）

- **WS `/api/ws`**：无被动订阅（事件按 session 路由，裸连只收全局广播）。
- **REST `/api/sessions` 轮询**：读同一 state.db，零增量 + token 负担。
- **state.db 单源**：被动观察，无 session 归属；现保留为 **api_server 不可达时的离线兜底**。
- **远程直连**：非 loopback OAuth gate 阻断第三方。

## 未来

- 中断/取消增强（`/v1/runs` 可取消回合）。
- API key 入 macOS Keychain。
- 多 profile 切换 UI。
