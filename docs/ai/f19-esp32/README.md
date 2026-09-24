# F19 — ESP32 硬件桌宠（分支线）

> **状态**：决策汇总 / 需求池（**未进入 Phase 1 正式需求分析**）
> **定位**：**独立分支线**，与 F18 无限之路主线分开推进，不计入主线编号
> **更新**：2026-09-24
> **编号提醒**：桌面版 V0.3.0 的时钟需求「暂编 F19/F20」与本分支编号冲突 → 本分支正式立项时改用独立编号（建议 `E1、E2…`）

---

## 一、一句话概述

把桌宠从桌面软件搬到**硬件摆件**：PC 本地服务端（复用现有 C# 核心服务）+ Web 配置前端 + ESP32 哑终端（只做渲染与播放）。

---

## 二、总体架构

```
┌─ PC 本地服务端 MiniPet.Server ─────────────────────┐
│ 复用：WzService / PaperdollService / MapService /  │
│       BalloonService / MusicCatalog / CacheManager │
│ 新增：布局导出器（部件图 RGB565 + origin / 布局表   │
│       帧×部件×x,y,z / 地图背景烘焙 / MP3 流）       │
│ 设备管理：manifest 版本 / 素材包 / 状态指令         │
└──────────── WiFi 局域网 ───────────────────────────┘
┌─ MiniPet.Web（HTML 前端）─────────────────────────┐
│ 设置中心 / 素材浏览器 / 纸娃娃编辑 / 曲库 / 地图选择 /  │
│ 表情入口 / 设备卡片列表 / 音源管理                   │
│ 技术栈：Vue 3 + Naive UI（Vite 构建 → wwwroot）     │
└───────────────────────────────────────────────────┘
┌─ ESP32-S3 固件（哑终端）──────────────────────────┐
│ ① 轮询 manifest 拉素材包（TF 卡缓存）               │
│ ② LVGL 本地合成 + 30fps 播放                       │
│ ③ 触摸/IMU 交互 → 上报事件                         │
│ ④ HTTP 流式 BGM → minimp3 → I2S                   │
│ 固件内：**零 WZ、零 Hermes、零 HA、零 WZ 解析**      │
└───────────────────────────────────────────────────┘
```

- 通信：**设备永远是 HTTP client**（轮询/长轮询），不用内网穿透
- 多设备：单固件代码库 + **设备 profile**（w/h/shape/psram/audio/touch）

---

## 三、硬件

| 项 | 桌面主力 | 冰箱贴（二期） |
|---|---|---|
| 型号 | **ESP32-S3-Touch-AMOLED-2.16** 带锂电池版（SKU 33969，约 ¥184） | ESP32-S3-Touch-LCD-1.28-B |
| 屏 | 480×480 AMOLED，QSPI（CO5300） | 240×240 圆屏 LCD（GC9A01） |
| 内存 | 8MB PSRAM + 16MB Flash | 2MB PSRAM + 16MB Flash |
| 触摸 | CST9220 | CST816 |
| 音频 | ES8311 + 板载喇叭 + 双麦（语音不做） | 无喇叭 → 静音宠物 |
| 传感器 | IMU QMI8658 / RTC PCF85063 / AXP2101 电量计 | IMU / RTC |
| 其他 | TF 卡槽 / GPIO18 可编程键 / IPEX 天线座 / 1000mAh 电池 | CNC 壳（磁吸改造） |

**关键实测约束**
- 整帧 RGB565 = 450KB（>SRAM）→ 帧缓冲必须 PSRAM，**必须脏区更新**（QSPI 实测 ≈9.7MB/s，整屏滚 13.2MB/s 超限）
- 无无线充电（Type-C 唯一充电口；后装磁吸 Type-C 头 ≈¥15 实现"放下即充"）
- 无人体存在传感器（IMU 只能感知拍打/拿起/静置；真存在检测需外挂 LD2410 毫米波，P3）
- 续航：屏常亮 5~8h/1000mAh → 手机式体验：白天无线、晚上磁吸充电
- 省电三杠杆：AMOLED 纯黑背景 + 低亮度默认 + 无交互自动休眠（IMU/触摸唤醒）

---

## 四、渲染方案（方案 C：部件图下发 + 设备端合成）

- 服务端把纸娃娃拆成**部件图（RGB565 + origin）+ 布局表（帧 × 部件 × x,y,z）**下发
- 设备端 LVGL 在**动作帧切换时**合成一次（非每 vsync），平时只 blit 合成结果
- 480 屏：发 1x 部件 + **scale 2x nearest**（像素风无损，省 4 倍带宽）
- 480 屏同屏多宠 **N≤2**（脏区 ≈200KB，仅帧切换时上传）；240 圆屏限 1 只
- 协议 `entities[]` 数组设计，首版 N=1 也按数组结构

**地图**：tile/obj 静止烘焙 + back 视差条带
- 导出 `static_back.rgb565`（不自动滚动的 back 合成）+ `tile_layer.rgba565`（tile+obj+front，含 alpha）+ `strips[]`（{图块, span, y, 速度, rx}）
- 偏移 = f(time)，用 WZ back 自带的 `tileMode ScrollH/ScrollV`，**不需要相机源**（走路模式已废弃）
- 复用 `MapService.ParseBacks/GetMeshBack` 公式（camCenter → time）
- 480 屏云带 480×60 @30fps 仅占 17% QSPI 带宽；冰箱贴版不启用条带动画（省电）

---

## 五、字体

- PC 端 **lv_font_conv 预转 bin** → Flash（候选字体：霞鹜文楷 / 方舟像素）
- 气泡文字由**设备端 LVGL label 渲染**，协议传 **UTF-8 文本**（不是位图）
- 尺寸参考：16px 3500 字 4bpp ≈ 438KB、24px ≈ 984KB（16MB Flash 无压力）

---

## 六、表情系统（覆盖桌面版与设备端）

- **WZ 实证 25 个表情**（Face_000.wz）：default/blink/hit/smile/troubled/cry/angry/bewildered/stunned/vomit/oops/cheers/chu/wink/pain/glitter/despair/love/shine/blaze/hum/bowing/hot/dam/qBlue
- **blink 自动循环**（随机 3~8s，设备端本地循环，断网也眨眼——生命感地基）
- 触发型播完回 default

| 触发 | 动作 | 表情 |
|---|---|---|
| 触摸轻点 / 抚摸 / 长按 | stand | smile / love / troubled |
| IMU 轻拍 <2g | alert | bewildered |
| IMU 大力拍打 ≥4g | hit 反馈 | **hit** |
| IMU 摇晃 / 拿起 | stunned / fly | stunned / oops |
| 系统（BGM / 低电 / 配对 / 故障 / 过温） | stand / sit | hum / despair / cheers / dam / hot |
| 自动 | — | blink + wink/chu/qBlue 随机轮播 |

- **素材动作铁律**：只做 WZ 真实存在的动作（`00002000.img` 动作表：walk1/2、stand1/2、fly、jump、sit、ladder、rope、prone、alert、heal、rain、swing/stab/shoot 系…），**禁自创**
- 桌面版先行落地：真实 `ExpressionDriver`（替换 EffectLayerService:314 的恒 default stub）+ 绑定主宠 + 设置中心 25 表情入口 + blink 自动循环
- F19：导出器按 expression 维度出部件帧 + 固件表情状态机；两线共用同一张映射表

---

## 七、重力感应交互

```
设备左右倾斜 → 背景视差条带反向平移（背景动、人不动）+ 人物播 walk1（原地走）
设备上倾     → 人物切 fly（回正落地 stand）
```

- 灵敏度 / 死区（默认 ±8°）/ 力度阈值（2g/4g）Web 可配
- 防抖：倾斜状态需连续保持 300ms 才生效
- IMU → 服务端 → 重发 layout（设备只上报事件，不上报原始数据）

---

## 八、时间功能（**已定稿**）

📄 完整规格见 **`docs/ai/clock-display-spec.md`**（定稿 2026-09-24）

- 待机时用 **WZ 数字时钟**（不做纯表盘），素材 `Map/Obj/etc.img/clock/fontTime`
- 起点 = `clock + (18+3, 83)`；`AMPM_GAP=12px`；12h 制、午夜 `AM 00:xx`、comma 偶显奇隐
- 时间源 = **PCF85063 RTC**（断网也能走时）
- 26 张含 clock 配置的地图清单：`~/Desktop/wz-clock-maps.md`
- **番茄钟系列：砍**（timer 系素材不用）

---

## 九、BGM

- **双源**：WZ 曲库（默认）+ QQ 音乐（可选），**用户先选类型，之后切歌/上下曲/随机只在所选类型内转换**
- 短路处理：只在**同源内**重试降级，**禁止自动跨源换歌**；源整体不可用 → 设备界面该入口置灰，手动切类型才生效；failover 上报 + 宠物 `despair` 表情
- QQ 音乐接入路径（子 agent 实证调研）：
  - 官方 API 死路（qqmusicsdk 域名枯竭 / QPlay 需企业资质 / 小微仅限音箱厂商）
  - **主路径** = 本地部署 `Rain120/qq-music-api`（Node，Koa2，活跃）→ 服务端注入 cookie 取链
  - 只锁 **128k 明文 MP3**（minimp3 可直接消费）；VIP 无损 mflac/mgg 有 STag 加密，不碰
  - **vkey 链接短时效 → 服务端必须实时取链代理转发，直链绝不下发设备**
  - cookie 扫码获取会过期 → Web 音源卡片 + 过期告警；个人局域网自用风险低
  - 架构 = `IMusicSource` 可插拔接口
- **控制入口在设备触摸屏**（现场控制：播放/暂停/切歌/音量），Web 只管曲库/歌单配置

---

## 十、设备端选择器（控制中心）

- 唤出：**设备物理按键（GPIO18）**（最终形式待硬件到货确定）
- 三个 tab：**地图 / 纸娃娃 / 怪物·NPC**，内容 = 预设 + 最近使用
- 缩略图由**服务端渲染 64×64**；`cached` 标记由服务端计算（设备上报缓存清单）
- 地图列表**只由后端返回**；后端不可用 → 只显示本地缓存可选项，其余置灰
- 切换后宠物反应 = **随机表情**（不指定固定表情）
- 素材落 TF 卡缓存 → 切过的秒切
- **第 4 个 tab「家居」**：480 触摸屏当 HA 控制面板（开关灯 / 空调 / 场景）——P2

---

## 十一、多设备与多桌宠

- **每设备绑一只宠物实例**（桌面版 PetManager 概念平移）
- 设备身份：UUID 存 NVS + Web 配对码入册
- 服务端设备表：`deviceId → {profile, petConfig, bgm偏好}`
- manifest 按设备隔离；BGM 曲库共享但播放状态独立
- Web 首页 = 设备卡片列表（在线状态 / 换宠 / 换装）
- 开发基线 = **单设备**（AMOLED-2.16），冰箱贴二期

---

## 十二、降级与容错

- **无网络降级**：无 WiFi 时用 TF 卡缓存素材 + 静音（或本地缓存）继续运行；回网自动同步
- 素材加载失败 → 宠物态提示（`despair`）
- 服务端离线 → 选择器只显示本地缓存

---

## 十三、支撑件

| 项 | 方案 |
|---|---|
| 首次配网 | **SoftAP captive portal**（设备开热点 + 网页配网）；之后上电自动连 |
| 局域网发现 | mDNS 自动找服务端（P1） |
| OTA | 服务端下发固件 + 双分区 OTA（P1） |
| RTC 定时任务 | 早安 / 晚安（P1，表情+动作，素材现成） |
| 素材缓存 | TF 卡（最近 N 个 + 收藏，断网可切） |
| 设备自检页 | WiFi / 内存 / 温度 / 固件版本（长按键进，P2） |

---

## 十四、明确不做（已拍板）

| 项 | 原因 |
|---|---|
| **Hermes 全线** | 协议无 state 槽位，Adapters/AgentStateAggregator 永不搬（表情源=触摸+随机idle+Web手动） |
| 无限之路 / 走路模式 | 硬件无桌面舞台 |
| 地图 ↔ BGM 自动联动 | 胶水故意砍（体验不好） |
| 番茄钟系列 | 先不做，只要时间 |
| 节日自动换装 | 客户端每次更新会删节日素材，不可靠 |
| 亲密度 / 养成 | 放弃 |
| 陪伴统计 | 不需要 |
| 双设备社交 | 只做单设备 |
| BLE 通知(ANCS) / BLE 配网 / 计步 / 手机 App | D 组只保「无网络降级」，**不开手机新 App** |
| 小智 AI 语音 | 固件刷掉即覆盖，语音不做（双麦硬件留着） |
| 设备端内嵌 Web / 浏览器 | 技术上不可能（LVGL 仅 CSS-like）；重 UI 全在服务端 Web |

---

## 十五、P2 / 候选池

| 项 | 说明 |
|---|---|
| HA 家居面板 | 选择器第 4 tab（设备当 HA 遥控器） |
| 特效层 | 点击反应出 WZ 真实特效（需先算带宽预算） |
| 触摸手势分级 | 单击/双击/长按/滑动 → 不同动作（PetEvents 概念平移） |
| 随机台词气泡 | 静置冒出预设台词（文本 Web 配置） |
| 天气联动 | 外部 API → `rain` 动作等 |
| 地图分层显隐 | Web 配置 back/obj/tile 显隐 |
| **桌宠终端内容转发 Hermes** | 服务端暴露 MCP / 本地 HTTP 接口，设备仍零 Hermes 代码 |
| 宠物小剧场 | 静置随机播未用动作（swing/stab/shoot/heal/rain/savage） |
| WZ 音效反馈 | 点击播 WZ 原版音效（SoundEff 分类现成） |
| 名片 NameTag | 等客户端桌宠完成后迁移 |

---

## 十六、工程与流程

**资产盘点（F19 启动时执行）**
- 可复用：`Services` 核心 **10.4K 行**（WzService / PaperdollService / MapService / BalloonService / MusicCatalog / MusicPlayer / AnimService / CacheManager / SpriteService）
- Avalonia 污染：仅 12 文件 17 处（全是 `Dispatcher.UIThread` 封送，半天可清）
- 不搬：Views 15.8K 行（被 Web 替代）、Adapters 4K 行（Hermes 相关）、走路模式系
- **git 策略**：新仓库纯 copy（不用 subtree/filter-repo），旧仓库冻结为桌面版，F18 主线原地继续

**出图/验证工具**
- `~/mapprobe_backup/` —— 复用项目 MapService/WzService 源码的探针工程，可出任意地图任意视口 PNG
  （关键坑：必须 `ClampCamera`；缩放画布勿与未缩放坐标混用）
- `~/Desktop/clockshot/proj_*.png` —— 时钟区域样张

---

## 十七、下一步

1. **Phase 1 需求分析**（逐条确认，编号用 E 系列避免与主线 F 冲突）
2. 硬件到货后：跑官方 BSP demo（点屏 / 触摸 / 喇叭）确认硬件完好
3. 并行可先行：服务端协议定义、布局导出器、Web 前端骨架（不依赖硬件）

---

## 相关文档

- `docs/ai/clock-display-spec.md` —— 时钟显示规格（**已定稿**）
- `~/Desktop/wz-clock-maps.md` —— 26 张含 clock 配置的地图全清单
- `~/mapprobe_backup/` —— 地图出图探针
