# Plan Review Log: V0.2.0 音乐模块（F15-F17）
Act 1 (grill-with-docs) complete — plan locked, CONTEXT.md created, ADR-0007 added. MAX_ROUNDS=5.
Reviewer model: deepseek-v4-flash (~/.codex/config.toml custom provider), codex-cli 0.146.0.

## Round 1 — Codex（VERDICT: REVISE，14 条意见）

1. ADR-0007 证据面夸大：R2 Music.cs 只验证了内存建流/播停/音量/释放，无 Bass.Init、END SYNC、Position/Duration/Seek、错误处理；PLAN 的 demo 门槛只测播+停。
2. 原生分发未解决且无视架构：csproj 无 RID；R2 附带的只有 Windows PE 二进制；OS-only 拷贝规则选不出 Windows 架构也给不出 mac dylib。
3. 单例播放器生命周期 ≠ R2 一次性对象：未定义旧流释放/旧 GCHandle Unpin/旧 sync 移除/BASS 失败码检查。
4. 状态过渡竞态：BASS 播完回调、UI 操作、联动变更并发改同一 queue/index/state 无串行化。
5. 联动可把当前播放静默打断：先换队列后验证曲目可用性，违反「不可用保持当前」域规则。
6. 同曲跨图重启：无条件替换在目标地图 BGM 与当前一致时也会重播。
7. WZ 重载留下脏数据：LoadWz 清 _wzs 时在途目录任务持有旧节点引用；InvalidateCatalog 无挂接点与 stale guard。
8. 目录加载冻结 UI：async 不改变锁内同步枚举的事实；_catalogLoading bool 无单飞语义。
9. DI 注册接不进 UI：SettingsWindow 是 PetWindow 手动 new 的（5 个构造依赖），需扩构造链与 _mainPanes。
10. 持久化竞态：整对象 load/mutate/save 互踩；ConfigService 返回共享可变状态；Environment.Exit 跳过 flush。
11. 恢复欠精确：Search 子串匹配 vs Key 全等语义；启动强制建 2.2s 索引；预选触发 CurrentChanged 会弹假气泡。
12. 队列非快照：UI 列表后续被过滤/切换改写会影响 Next/Random。
13. PlayHistory 违反 schema 契约：模型注释定义了去重截断 20，PLAN 却无限增长。
14. 验证缺测试套件与进度机制：§11.12 要求 dotnet test；进度条无轮询/事件规则。

### Claude's response

14 条全部接受实质，其中 2 条收窄执行：
- 全盘采纳：1→扩展版 demo 闸门（Init/sync/seek/换流/清理/错误码全验）；2→RID 分发 + dylib 来源版本 SHA-256 入档；3+4→原子切歌协议 + SemaphoreSlim 串行 + 代际标签丢弃迟到回调；5+6→联动三步预检成功才提交 + 同 Key no-op；7+8→Task.Run 单飞版本化目录任务 + InvalidateCatalog 挂进 LoadWz 成功路径；9→写明手动构造传递链（PetWindow→SettingsWindow ctor→BuildBody）；11→精确 Key 推迟查找 + 静默选中不发事件；12→Play 时深拷贝不可变快照；14→恢复 dotnet test + 新增 5 类纯逻辑单测 + tab 可见才跑的 DispatcherTimer。
- 收窄采纳 10：音乐子树用小型锁内 load→mutate→save helper 原子化；LastTrackKey 改为即时落盘使强退丢数据窗口只剩 Volume 防抖 800ms。拒绝整套 ConfigService 重构与 ProcessExit flush 机器——改造全局持久化模式超出本轮边界（禁止加码），且即时落盘已消除实际风险。该拒绝点 Codex 可在下轮再攻。
- 收窄采纳 13：写入时即做 move-to-front 去重+截断 20，对齐 schema 注释；「P2 截断位置」重新诠释为管线位置待定而非不做——若 Codex 认为应彻底不写历史，下轮给出理由。

PLAN 已修订为 Rev 2。

## Round 2 — Codex（VERDICT: REVISE，上轮 14 条中 9 完全解决 5 部分解决，新增 10 条深化意见）

要点：①原子切歌协议与联动预检矛盾——先毁旧流则预检失败必断当前曲；②项目无 RID，纯 RID 拷贝条件在普通 Debug 构建下永不命中；③失效只挂 LoadWz 成功路径，失败收场的重载留下脏结果；④WzService 直接通知目录服务会 DI 成环；⑤await 旧任务者仍可能拿到过期数据；⑥音乐局部锁防不住既有的整对象写者覆盖；⑦OnMapChanged 在同步 UI 代码里无 await/日志/时序 guard；⑧BASS 回调线程内联做提取+JSON 落盘；⑨窗口是 Hide 非关闭，计时器可见性条件不足；⑩播放失败的 UI/服务契约仍缺位。

### Claude's response

10 条全部采纳（第 6 条收窄）：
- 1→升级为**两阶段交换协议**：准备段在旧流照常播放时建第二个临时流并校验，提交段才在锁内毁旧提升新；预检复用准备阶段，彻底消除矛盾。
- 2→拷贝条件改用 MSBuild OS 平台函数（无 RID 也生效），编译后断言动态库存在于输出目录。
- 3+4+5→WzService 增加重载代际事件（避免成环），每次重载尝试开始即 bump 代际 finally 兜底失效，await 侧二次校验代际不匹配按错误处理。
- 6（收窄）→音乐持久化全部派发到 UI 线程执行，与既有序写入者同线程化即消除现实竞态；拒绝全局事务队列重构（越权），残余窗口如实标注为**今日已存在**的系统形态。
- 7→OnMapChanged 改带日志后台任务 + 地图代际 guard。
- 8→PlaybackEnded 后续编排立即转投 UI 调度器，文件 IO 不进 BASS 回调线程。
- 9→进度计时器三条件制（窗口可见 + tab 选中 + 有活动流），Hide 即停表。
- 10→补充完整失败契约：保住当前播放 / 落回 Stopped、PlaybackError 事件 → tab 状态栏 + stderr。

PLAN 已修订为 Rev 3。

## Round 3 — Codex（VERDICT: REVISE，5 实质 + 2 次要）

1. 两阶段交换在现有接口上无法实现：Play(byte[]) 无预备句柄可校验/提交/丢弃。2. LoadWz 锁外换 _wzs/_wzLoaded 引用，目录遍历可被拦腰替换。3. 「既有写者都在 UI 线程」是不变式误判：ApplyPaperdoll 在 Task.Run 整存整取、SwitchToCameraMode 存旧快照——收窄方案失据；提议 ConfigService.Update(Action<PetSettings>) 窄helper。4. 自动 Next 转 UI 线程会把 WZ 提取+落盘搬进 UI 冻结窗口。5. 自然播完后旧流已终结，「保持当前播放」失败语义不可实现且缺 RepeatOne 重试循环防护。次要：Sequential 队列尾回卷未在 CONTEXT 声明为 override；dotnet 不在 PATH。

### Claude's response

7 条全部采纳：
- 1→IMusicPlayer 扩展 Prepare/Commit/Discard 预备流能力（ADR-0007 已追加修正案），Play(byte[]) 降级为便利方法。
- 2→LoadWz 状态发布段持 _wzLock 发布 + ExtractSound 判空进锁内；属新消费者引致的必要最小修补。
- 3→改采 ConfigService.Update(Action<PetSettings>) 于其现有锁内事务化；ApplyPaperdoll/SwitchToCameraMode 两个碰撞集写入点机械迁移（各约三行）——承认上轮「UI 线程同线程化」论证基于错误前提，被 Codex 证伪；Write 频率上升（每曲历史）使碰撞修复成为必要而非镀金。
- 4→END SYNC 只投轻量后台命令，编排跑第三线程，仅终态通知回 UI。
- 5→自然播完失败 = Stopped + PlaybackError + 状态栏提示；RepeatOne 重备失败同样收场，无重试环；补决策函数单测。
- 次要 2 条照办：Sequential 回卷写入 PLAN 关键决策（显式 override）；测试命令统一绝对路径。

PLAN 已修订为 Rev 4。

## Round 4 — Codex（VERDICT: REVISE，4 实质 + 1 次要）

1. Update helper 闭不死竞态：SettingsWindow 缓存 _settings 后整对象回存（axaml.cs:581），背景音乐提交可插在其读写区间内被覆盖。2. LoadWz 在 109 行即置 _wzLoaded=true 但 Mob/Packs 加载持续到 ~161 行——仅锁赋值仍放出半成品结构。3. 场景预检缺服务操作代际：提取期间用户按停/切歌后过期的场景预备流照样提交。4. Rev 遗漏 §11.9「播放中右键菜单头动态显示曲名」。次要：Commit() 中途 BASS 失败语义未定义。

### Claude's response

5 条全部采纳：①音乐 Update 短临界段最终落 UI 线程（与设置窗口的整存同线程串行，闭合缓存区间）；②LoadWz 改局部构建完毕后单临界区一次性发布（必要最小修补）；③增加服务操作代际 guard，预检被用户操作打断则丢弃预备流；④补回菜单头部动态曲名挂钩并标注为 Rev4 遗漏修正；⑤Commit 段失败定义为终态 Stopped + PlaybackError + 确定性资源释放 + 单测。

PLAN 已修订为 Rev 5。

## Round 5 — Codex（VERDICT: REVISE，达 MAX_ROUNDS=5 上限）

1. 第三个整对象写者漏网：MaterialBrowserWindow.Logic.cs SaveDraftOutfit 在模态对话框前取 cfg 引用、用户点 OK 后整存。2. 自动 Next 后台命令无操作代际 guard，可双跳/复活。3. 场景提交前需重验 SceneLinkEnabled 开关与 WZ 代际。4. MusicPlayerService 缺 SeekAsync（§11.1 列明 seek），进度条点击无落点。5. 双平台原生二进制仅记录哈希未强制校验。

### Claude's response（Post-cap 处置）

5 条全部为已共识模式的机械补全，无一存在仲裁分歧，直接折叠进 Rev 6 并如实标注：**本轮修订未经 Codex 复审**（轮次上限规则优先于收敛强迫症）。逐条：①SaveDraftOutfit 迁 Update/OK 内重读；②END 命令捕获+比对操作代际；③提交前重验四元组快照（地图/操作/WZ/开关）；④补 SeekAsync(clamp, 双态可用)+进度条接线；⑤URL+版本+SHA-256 双二进制入档且编译期强制哈希校验。

若用户要求完美闭环，可手动追加一轮复审；否则 Rev 6 为交付版。
