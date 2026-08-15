# MiniPet 编译启动指南

## 环境

- dotnet 是**可执行文件**（非目录）：`/Volumes/SSD/C#/dotnet`
- `#` 是 shell 特殊字符，路径**必须引号调用**
- WzComparerR2.WzLib.dll 引用项目内相对路径 `MiniPet/lib/WzComparerR2.WzLib.dll`

## 编译

```bash
"/Volumes/SSD/C#/dotnet" build MiniPet/MiniPet.csproj -c Debug
```

0 错误才交付（既有警告与本次改动无关，可忽略）。

## 启动

```bash
caffeinate -dimsu "/Volumes/SSD/C#/dotnet" run --project MiniPet/MiniPet.csproj --no-build
```

- GUI 需显示器唤醒：必须 `caffeinate -dimsu`，否则 Avalonia.Native 渲染定时器错误 `-6661`
- 先 build 再 `--no-build` 启动，避免 run 时重复编译

## 运行前提

- WZ 资源：本地 WZ 文件（Character / Mob / Npc / Map / MapHelper / Tile / Obj / Back），路径在应用配置（AppConfig）中设置
- macOS：桌宠点击穿透走原生 `NSWindow.ignoresMouseEvents` + `objc_msgSend`

## 常见问题

| 现象 | 原因 | 处理 |
|------|------|------|
| 渲染定时器错误 -6661 | 显示器休眠 | 用 `caffeinate -dimsu` 启动 |
| 路径找不到 dotnet | `#` 未加引号 | 路径加双引号 |
| WzLib 解析失败 | dll 路径 | 确认 `MiniPet/lib/WzComparerR2.WzLib.dll` 存在 |
