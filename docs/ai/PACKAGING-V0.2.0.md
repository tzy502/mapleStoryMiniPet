# V0.2.0 打包记录 + 待修复项

> 日期：2026-08-27
> 版本：V0.2.0（feature/0.2.0 分支，HEAD 2b43d2d「V0.2.0」）
> 关联：`docs/ai/claude-prompt-20260827-01.md`（音乐模块开发提示语）

## 一、产物位置（已备份）

| 产物 | 路径 | 大小 | 说明 |
|------|------|------|------|
| **Windows x64 包**（主交付） | `~/Desktop/MiniPet-V0.2.0-win-x64.zip` | 51MB | 自包含免装 .NET，含修正后的 bass.dll |
| macOS arm64 包（顺手产出） | `~/Downloads/MiniPet-V0.2.0-osx-arm64.zip` | 45MB | 自包含 |
| 临时解包目录（重启即失） | `/private/tmp/MiniPet V0.2.0 Win/`、`/private/tmp/MiniPet V0.2.0/` | 127M/115M | zip 的未压缩源，仅本轮打包用 |

**打包命令备查**：
```bash
export DOTNET_ROOT="/Volumes/SSD/C#"
# Windows
"/Volumes/SSD/C#/dotnet" publish MiniPet/MiniPet.csproj -c Release -r win-x64 --self-contained true -o <输出目录>
# macOS
"/Volumes/SSD/C#/dotnet" publish MiniPet/MiniPet.csproj -c Release -r osx-arm64 --self-contained true -o <输出目录>
```

## 二、🔴 待修复：csproj 交叉发布 native 库拷贝 bug（下次开发必修）

**现象**：在 macOS 上交叉发布 win-x64 时，发布目录里是 macOS 的 `libbass.dylib`，**缺 Windows 的 `bass.dll`** —— Windows 上音乐模块会静默失败（Bass 初始化失败）。

**根因**：`MiniPet/MiniPet.csproj` 第 44-49 行，BASS 原生库拷贝条件用的是**构建机平台**（`$([MSBuild]::IsOSPlatform('OSX'))`），不是**目标运行时**：

```xml
<None Include="lib/audio/libbass.dylib" Condition="$([MSBuild]::IsOSPlatform('OSX'))">
<None Include="lib/audio/bass.dll" Condition="$([MSBuild]::IsOSPlatform('Windows'))">
```

→ 在 mac 上无论发哪个 RID，都只拷 dylib；在 Windows 上无论发哪个 RID，都只拷 dll。

**修复方向**（交 Claude Code）：
- 条件改为按目标 RID 判断，例如 `Condition="'$(RuntimeIdentifier)' == 'win-x64' or '$(RuntimeIdentifier)' == 'win-arm64'"` / `Condition="'$(RuntimeIdentifier)' == 'osx-arm64' or '$(RuntimeIdentifier)' == 'osx64'"`（注意 macOS 老写法 osx64；无 RID 的本地 build 兜底 `$(RuntimeIdentifier) == ''` 时回退 IsOSPlatform 判断，保证 `dotnet build` 默认场景不回归）
- 同步检查 `VerifyBassNativeBinaries` Target（csproj 76 行起）：它只校验「存在的文件」，不校验「该存在而不存在」——修复后建议按 RID 断言**必须有对应平台库**（win 发布必须含 bass.dll，osx 发布必须含 libbass.dylib），把本次这种静默错包变成编译期报错
- 关联 ADR：`docs/adr/0007`（BASS 选型），修复时同步更新

**本轮的临时处置**（未动代码）：手动从发布目录删除 `libbass.dylib`，拷入仓库 `MiniPet/lib/audio/bass.dll`（SHA256 `febb2cf1882d554c3a958280777da0b69f07de6e262df271de11c56e4a54afd4`，与 csproj 期望哈希一致，非篡改件）。

**验证方式**（修复后）：
```bash
# mac 上交叉发布 win-x64，断言 bass.dll 在、dylib 不在
"/Volumes/SSD/C#/dotnet" publish MiniPet/MiniPet.csproj -c Release -r win-x64 --self-contained true -o /tmp/verify-win
ls /tmp/verify-win | grep -E "^bass.dll$" && ! ls /tmp/verify-win | grep -q "dylib"
```

## 三、版本备注

- V0.2.0 音乐模块（F15-F17）实现：MusicCatalogService / MusicPlayerService / IMusicPlayer + BassMusicPlayer（ManagedBass 3.1.1）
- 打包时工作区有未提交改动（音乐模块新文件 + 若干 M 文件），包内容以 feature/0.2.0 工作区现状为准
- macOS 包未做冒烟启动（Windows 包是主交付，且无法在本机验证 exe）
