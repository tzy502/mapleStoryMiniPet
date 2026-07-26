# MiniPet v1.0 — WZ 帧节点 origin/delay 读取修复

## 根因

`WzService.ExportSpriteStrip()` 和 `BuildSpriteStripPng()` 中，`origin`、`delay`、`a0`、`a1` 等属性在 UOL 解析后的节点上查找，但它们在**原始帧节点**上。

## 当前代码

**文件**: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Services/WzService.cs`

**方法**: `ExportSpriteStrip()` 第 237~248 行

```csharp
foreach (Wz_Node child in actionNode.Nodes)
{
    var pngNode = child.ResolveUol() ?? child;          // ← UOL 解析，得到链接目标节点
    var linkNode = GetLinkedSourceNode(pngNode) ?? pngNode;
    var png = linkNode.GetValue<Wz_Png>() ?? pngNode.GetValue<Wz_Png>();
    int fw = png?.Width ?? 0, fh = png?.Height ?? 0;

    var origin = pngNode.FindNodeByPath("origin")?.GetValueEx<Wz_Vector>(null);  // ← BUG！在链接目标节点上找 origin
    int ox = origin?.X ?? 0, oy = origin?.Y ?? 0;
    int delay = pngNode.FindNodeByPath("delay")?.GetValueEx<int>(100) ?? 100;     // ← BUG！
    int a0 = pngNode.FindNodeByPath("a0")?.GetValueEx<int>(255) ?? 255;           // ← BUG！
    int a1 = pngNode.FindNodeByPath("a1")?.GetValueEx<int>(255) ?? 255;           // ← BUG！
```

## 修复

**`origin`、`delay`、`a0`、`a1` 在 `child`（原始帧节点）上查找，`png` 在 `linkNode` 上查找。**

```csharp
foreach (Wz_Node child in actionNode.Nodes)
{
    var pngNode = child.ResolveUol() ?? child;
    var linkNode = GetLinkedSourceNode(pngNode) ?? pngNode;
    var png = linkNode.GetValue<Wz_Png>() ?? pngNode.GetValue<Wz_Png>();
    int fw = png?.Width ?? 0, fh = png?.Height ?? 0;

    // origin/delay/a0/a1 在原始帧节点 child 上，不在 UOL 解析后的节点上
    var origin = child.FindNodeByPath("origin")?.GetValueEx<Wz_Vector>(null);
    int ox = origin?.X ?? 0, oy = origin?.Y ?? 0;
    int delay = child.FindNodeByPath("delay")?.GetValueEx<int>(100) ?? 100;
    int a0 = child.FindNodeByPath("a0")?.GetValueEx<int>(255) ?? 255;
    int a1 = child.FindNodeByPath("a1")?.GetValueEx<int>(255) ?? 255;
```

## 同样的 Bug 在 `BuildSpriteStripPng()` 第 307~315 行

```csharp
foreach (Wz_Node child in actionNode.Nodes)
{
    var pngNode = child.ResolveUol() ?? child;
    var png = pngNode.GetValue<Wz_Png>();
    if (png == null) continue;
    // ...
    var origin = pngNode.FindNodeByPath("origin")?.GetValueEx<Wz_Vector>(null);  // ← BUG！同样的问题
```

**改为**:
```csharp
    var origin = child.FindNodeByPath("origin")?.GetValueEx<Wz_Vector>(null);
```

## 验证

`dotnet build --no-restore` 0 错误