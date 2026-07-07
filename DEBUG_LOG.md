# Debug 操作记录 — 2026-07-05

## 已修复问题

### 1. WzSkillEffectRenderer.swift — 3 个 release 编译错误

#### 1a. Line 282: Int vs CGFloat 类型冲突
- **问题**: `max(frameWidth, effectFrame.image.size.width.rounded())` — `frameWidth` 是 `Int`, `width.rounded()` 是 `CGFloat`, Swift 泛型 `max<T>(T, T)` 无法推断统一类型。
- **修复**: `max(CGFloat(frameWidth), ...)` 将 Int 转为 CGFloat。

#### 1b. Line 359-360: CompositedResult vs CompositedCharacter 类型不匹配
- **问题**: `loadCharacterWithEffect()` 调用 `avatarCompositor.compositeCharacterFrameWithOrigin()` 返回 `CompositedResult?`, 但 `compositeWithCharacter()` 和 `compositeWithCharacterAllFrames()` 的参数类型声明为 `CompositedCharacter`。`CompositedCharacter` 定义在 `WzCharacterModel.swift`（image + originX + originY），`CompositedResult` 定义在 `WzCompositor.swift`（额外有 width + height）。
- **修复**: 将两个 static 方法的参数类型从 `CompositedCharacter` 改为 `CompositedResult`。两个 struct 都包含 image/originX/originY 字段，兼容。

#### 1c. Line 458: CVDisplayLinkSetOutputCallback 闭包签名 5 参数 → 6 参数
- **问题**: SDK 更新后 `CVDisplayLinkSetOutputCallback` 的回调函数签名增加了第 6 个参数 `_ flagsIn: UnsafeMutablePointer<CVOptionFlags>`。原来的闭包只有 5 个参数，导致 release 编译时类型不匹配。
- **修复**: 闭包参数列表增加第 6 个参数 `_`, 匹配新签名 `(_, _, _, _, _, displayLinkContext)`。

### 2. ChairMountView.swift — entityType "chair"/"mount" 传参问题
- **问题**: `loadEntity()` 将 entityType "chair"/"mount" 传给 `fetch_and_generate.py` 的 `--type` 参数，但 Python 脚本只支持 `--type mob|npc`。
- **修复**: 在 ChairMountView 中增加判断：entityType 为 "mob" 或 "npc" 时才调用 API 生成；chair/mount 只支持缓存已有数据，不调用 Python 脚本。

### 3. 旧问题修复（历史记录 - 本轮之前已完成）
- PetView 动画抖动 — ContainerView.layout() setContentSize 前保存 window origin
- 右键菜单动画切换被 isSitting 拦截 — menuSwitchAnimation 先调用 standUp()
- idleTimer 5s 过快覆盖手动切换 — 改为 8s
- WzCompositor flipX 渲染 — saveGraphicsState/restoreGraphicsState
- WzSkillEffectRenderer 裁剪时序 — ctx.clip(to:) 在 ctx.draw() 之前
- SettingsPanel freq 不生效 — 改为 controller?.applySettings()
- CacheManager 移除 entityType 参数
- APIClient.fetchAndGenerateSprites 添加 type 参数

## 编译验证
- `swift build` — ✅ Debug 编译通过
- `swift build -c release` — ✅ Release 编译通过
