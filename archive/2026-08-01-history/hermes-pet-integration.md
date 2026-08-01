# Hermes 桌宠集成方案

> 讨论整理：胶水 × 502 | 2026-07-04  
> 目标：将已有桌宠系统接入 Hermes，实现动画驱动 + 双向对话 + 主动推送

---

## 一、Hermes 桌宠状态

### 6 个动画状态

| 状态名 | Agent 在干什么 | 宠物表现建议 |
|--------|---------------|-------------|
| `idle` | 空闲，等待用户输入 | 待机动画 |
| `run` | 正在执行工具（终端/文件/网络） | 忙碌/跑动 |
| `review` | 模型思考/阅读中 | 思考（摸下巴等） |
| `wave` | 一轮对话干净完成 | 挥手 |
| `failed` | 任务执行失败 | 沮丧/倒地 |
| `jump` | 所有 todo 完成 | 庆祝跳跃 |

### 状态映射（如果你的宠物动画不足 6 个）

```
idle          → 待机
run + review  → 忙碌（合并到同一个动画）
wave + jump   → 高兴（合并）
failed        → 沮丧
```

### 获取方式

| 方式 | 延迟 | 实现 |
|------|------|------|
| **WS 推送（推荐）** | 实时 | 连 `/ws`，监听 `activity` 事件 |
| **HTTP 轮询** | 1s | `GET /status`，读 `state` 字段 |
| **Unix socket** | 近实时 | 命名管道只读，写 Python 桥接 |

```javascript
// 推荐：WebSocket 实时
const ws = new WebSocket("ws://172.17.0.1:5200/ws");
ws.onmessage = (e) => {
  const msg = JSON.parse(e.data);
  if (msg.type === "activity") {
    pet.setAnimation(msg.state);  // idle / run / review / wave / failed / jump
  }
};
```

```python
# 备选：HTTP 轮询
import requests, time
while True:
    r = requests.get("http://172.17.0.1:5200/status")
    state = r.json()["state"]
    pet.play(state)
    time.sleep(1)
```

---

## 二、长连接双向通信

### 连接

```
ws://<hermes_ip>:5200/ws
```

- 同机器：`ws://127.0.0.1:5200/ws`
- NAS Docker：`ws://172.17.0.1:5200/ws`
- Mac 连 NAS（蒲公英 VPN）：`ws://172.16.1.13:5200/ws`

### 接收（Hermes → 宠物）

| 事件类型 | 数据格式 | 用途 |
|---------|---------|------|
| `activity` | `{type:"activity", state:"run"}` | 驱动宠物动画 |
| `tool_progress` | `{type:"tool_progress", tool:"terminal"}` | 显示当前在用什么工具 |
| `streaming` | `{type:"streaming", token:"这"}` | 逐字气泡 / TTS 朗读 |
| `response` | `{type:"response", text:"完整回复"}` | 一次性完整回复 |

```javascript
ws.onmessage = (e) => {
  const msg = JSON.parse(e.data);
  switch (msg.type) {
    case "activity":
      pet.setAnimation(msg.state);
      break;
    case "streaming":
      pet.showBubble(msg.token);   // 实时气泡
      pet.ttsFeed(msg.token);      // 逐字朗读
      break;
    case "response":
      pet.showBubble(msg.text);    // 完整显示
      break;
  }
};
```

### 发送（宠物 → Hermes）

```javascript
// 发送消息
ws.send(JSON.stringify({
  type: "chat",
  message: "帮我查一下数据库连接状态"
}));

// 查询状态
ws.send(JSON.stringify({ type: "status" }));
```

### REST 查询状态

```python
# 查询 Agent 当前状态
GET /status
→ {"active": true, "state": "run", "current_tool": "terminal"}

# 健康检查
GET /health
→ {"status": "ok", "version": "0.17.0"}

# 发送消息并获取回复
POST /chat
Body: {"message": "...", "session_id": "pet-001"}
→ {"reply": "...", "session_id": "pet-001"}
```

---

## 三、主动推送（宠物突然说话）

Hermes 没有原生"主动找你说话"机制，但有三种方式实现：

### 方式 1：Cron 定时推送

```bash
# 每天早上 10 点主动说骚话
hermes cron create "0 2 * * *" \
  --name "早安骚话" \
  --prompt "用轻松幽默的语气问候用户，可以带一句今日天气或编程冷笑话" \
  --deliver origin
```

宠物保持 WS 连接，cron 触发 → Hermes 生成消息 → WS 推 `streaming` → 宠物突然说话。

### 方式 2：后台脚本注水

在你的宠物系统里跑一个监控脚本，满足条件时往 WS 注入 chat 消息：

```python
import random, time, json, websocket

HERMES_WS = "ws://172.17.0.1:5200/ws"
QUOTES = [
    "用轻松语气说一句编程冷笑话",
    "问用户要不要喝杯咖啡休息一下",
    "用傲娇的语气提醒用户已经连续工作两小时了",
]

ws = websocket.create_connection(HERMES_WS)
while True:
    time.sleep(random.randint(600, 1800))  # 10-30 分钟
    ws.send(json.dumps({
        "type": "chat",
        "message": f"（系统指令）{random.choice(QUOTES)}"
    }))
```

### 方式 3：/goal 监控回调

```
/goal 每30分钟检查一次 Wiki 后端心跳，如果挂了立刻通知我
```

Hermes 后台持续监控，发现异常 → WS 推结果 → 宠物弹提醒。

---

## 四、架构全貌

```
┌──────────────────────┐          WebSocket           ┌──────────────────────┐
│   你的桌宠 (Mac)      │ ←─────────────────────────→ │   Hermes Gateway      │
│                       │    activity/streaming/      │   (NAS Docker)        │
│  ┌─────────────────┐  │    response/chat            │   172.16.1.13:5200    │
│  │ 动画引擎         │  │                             │                       │
│  │ idle/run/think/  │  │   蒲公英 VPN 172.16.x.x     │   ┌─────────────────┐ │
│  │  happy/sad       │  │                             │   │ QQ/飞书/Telegram │ │
│  ├─────────────────┤  │                             │   │ 多平台收发       │ │
│  │ 气泡 + TTS       │  │                             │   └─────────────────┘ │
│  ├─────────────────┤  │                             │                       │
│  │ 主动推送定时器    │  │                             │   ┌─────────────────┐ │
│  │ (cron/脚本注水)  │  │                             │   │ cron 定时任务    │ │
│  └─────────────────┘  │                             │   │ /goal 长期任务   │ │
└──────────────────────┘                             └──────────────────────┘

接口一览：
  WS  /ws        实时双向（推荐）
  REST GET /status   查询状态
  REST GET /health   健康检查
  REST POST /chat    发送消息
```

---

## 五、你需要的改动量

| 你已有 | 需要加 |
|--------|--------|
| 宠物渲染引擎 | WS 客户端，解析 `activity`/`streaming`/`response` |
| 动画系统 | 6 个动画状态映射表 |
| 语音/TTS | 接 `streaming` token 流 |
| 气泡文字 | 接 `response` 完整回复 |
| （无） | 主动推送定时器，注水 chat 消息 |
| （无） | WebSocket 连接管理（断线重连） |

---

## 六、环境地址

| 组件                  | 地址                                                  |
|---------------------|-----------------------------------------------------|
| Hermes Gateway（本地）  | `127.0.0.1:5200`（容器内）                               |
| Hermes Gateway（NAS） | `172.17.0.1:5200`（容器内）/ `172.16.1.13:5200`（蒲公英 VPN） |
| Hermes Dashboard    | `http://172.16.1.13:9119`                           |
| WZ 后端               | `172.17.0.1:10502`                                  |
| 蒲公英网络 ID            | 6840461（tzy502的网络）                                  |

---

## 七、参考命令

```bash
# 启动 API Server（如果未启用）
hermes gateway setup  # 勾选 API Server adapter

# 验证 WebSocket
curl -i -N -H "Connection: Upgrade" -H "Upgrade: websocket" \
  http://172.17.0.1:5200/ws

# 测试状态查询
curl http://172.17.0.1:5200/status

# 创建定时主动推送
hermes cron create "0 2 * * *" \
  --name "morning-greeting" \
  --prompt "用轻松语气问候用户" \
  --deliver origin
```
