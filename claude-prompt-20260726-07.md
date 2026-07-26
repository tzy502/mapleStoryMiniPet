# MiniPet v1.0 — 透明区域穿透点击修复

## 根因

`SpriteCanvas` 是 `UserControl`，它的 `Background` 默认是 `null`（不透明空背景），会拦截鼠标事件。即使内部的 Canvas 设了 `Background="Transparent"`，UserControl 本身仍然拦截了点击。

## 修复

### 文件 1: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/Controls/SpriteCanvas.axaml`

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             mc:Ignorable="d" d:DesignWidth="300" d:DesignHeight="300"
             x:Class="MiniPet.Views.Controls.SpriteCanvas"
             Background="Transparent"
             IsHitTestVisible="False">

    <Canvas x:Name="RootCanvas" Background="Transparent" HorizontalAlignment="Left" VerticalAlignment="Top">
        <Image x:Name="SpriteImage" IsHitTestVisible="False" />
    </Canvas>
</UserControl>
```

**关键改动**:
1. `UserControl` 添加 `Background="Transparent"` — 让控件背景透明
2. `UserControl` 添加 `IsHitTestVisible="False"` — 让整个控件不拦截鼠标事件

### 文件 2: `/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/Views/PetWindow.axaml.cs`

**第 77 行** `_balloonControl` 已经设置了 `IsHitTestVisible = false`，但 SpriteCanvas 没有。

**确保** `_spriteCanvas` 在 PetWindow 的构造函数中也有 `IsHitTestVisible = false`
```csharp
// 精灵画布
_spriteCanvas = new SpriteCanvas { IsHitTestVisible = false };  // 添加这行
Grid.SetRow(_spriteCanvas, 1);
grid.Children.Add(_spriteCanvas);
```

## 验证

`dotnet build --no-restore` 0 错误