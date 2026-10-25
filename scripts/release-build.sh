#!/bin/bash
# MiniPet 对外发布打包脚本（在 macOS 上运行，交叉产出全部平台纯净版）：
#   1) osx-arm64 / osx-x64：self-contained publish → MiniPet.app（含 AppIcon.icns + Info.plist）→ zip
#   2) win-x64：self-contained 交叉 publish（含 bass.dll，按 RID 判定拷贝）→ zip
# 全部 -p:MiniPetNoAi=true（纯净版：无任何 AI 功能/网络连接/文件监控）。
#
# 用法：scripts/release-build.sh [版本号] [RID...]
#   版本号缺省从 MiniPet.csproj 的 <Version> 读取（如 0.1.0）；RID 缺省 = osx-arm64 osx-x64 win-x64
# 产物：out/MiniPet-<ver>-win-x64.zip / MiniPet-<ver>-macos-arm64.zip / MiniPet-<ver>-macos-x64.zip
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DOTNET="${DOTNET:-/Volumes/SSD/C#/dotnet}"
PROJ="$ROOT/MiniPet/MiniPet.csproj"
OUT="$ROOT/out"

ARGS=("$@")
VERSION="${ARGS[0]:-}"
if [ -n "$VERSION" ] && [[ "$VERSION" != *.* ]]; then
    # 第一个参数不是版本号（形如 x.y.z）则视为 RID 列表开头
    RIDS=("${ARGS[@]:-osx-arm64 osx-x64 win-x64}")
    VERSION=""
else
    RIDS=("${ARGS[@]:1}")
    if [ ${#RIDS[@]} -eq 0 ]; then
        RIDS=(osx-arm64 osx-x64 win-x64)
    fi
fi
if [ -z "$VERSION" ]; then
    VERSION="$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$PROJ" | head -1 | tr -d '[:space:]')"
fi
if [ -z "$VERSION" ]; then
    echo "[错误] 未读到版本号（MiniPet.csproj <Version>）" >&2
    exit 1
fi
echo "=== MiniPet v$VERSION 纯净版（NO_AI）发布打包 ==="

publish_and_zip() {
    local rid="$1"      # osx-arm64 / osx-x64 / win-x64
    local publish_dir="$ROOT/MiniPet/bin/Release/net9.0/$rid/publish"

    echo ""
    echo "--- [$rid] dotnet publish ---"
    "$DOTNET" publish "$PROJ" -c Release -r "$rid" --self-contained true \
        -p:MiniPetNoAi=true -p:UsedAvaloniaProducts= -o "$publish_dir"

    # 纯净版自检：AI 程序集类型必须不存在于主 dll
    if strings "$publish_dir/MiniPet.dll" 2>/dev/null | grep -q "DshAdapter\|HermesAdapter"; then
        echo "[错误] [$rid] 纯净版自检失败：MiniPet.dll 仍含 AI 适配器符号" >&2
        exit 1
    fi

    if [[ "$rid" == win-* ]]; then
        # Windows：校验 bass.dll 已随 RID 拷贝（交叉发布修复点），exe 图标已由 ApplicationIcon 内嵌
        if [ ! -f "$publish_dir/bass.dll" ]; then
            echo "[错误] [$rid] bass.dll 未拷贝（RID 判定失效）" >&2
            exit 1
        fi
        local zip="$OUT/MiniPet-$VERSION-$rid.zip"
        rm -f "$zip"
        (cd "$publish_dir" && zip -qr "$zip" .)
        echo "[$rid] 完成: $zip"
    else
        # macOS：组装 .app（Contents/MacOS = publish 产物；Resources = icns；Info.plist）
        local app_parent="$ROOT/MiniPet/bin/Release/net9.0/$rid/app"
        local app="$app_parent/MiniPet.app"
        rm -rf "$app"
        mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
        cp -R "$publish_dir/." "$app/Contents/MacOS/"
        cp "$ROOT/MiniPet/Assets/mac/AppIcon.icns" "$app/Contents/Resources/"
        cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>MiniPet</string>
    <key>CFBundleDisplayName</key><string>MiniPet</string>
    <key>CFBundleIdentifier</key><string>com.tzy502.minipet</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleExecutable</key><string>MiniPet</string>
    <key>CFBundleIconFile</key><string>AppIcon</string>
    <key>LSMinimumSystemVersion</key><string>10.15</string>
    <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST
        # ad-hoc 签名：arm64 可执行的前提（未签名 arm64 二进制一律拒绝执行）；发布包无开发者证书，
        # 首次运行仍需用户右键打开绕过 Gatekeeper（发布说明已注明）。签名+验证必须双通过，否则终止。
        if ! codesign --force --deep -s - "$app" 2>/tmp/minipet_codesign_err.log; then
            echo "[错误] [$rid] ad-hoc 签名失败: $(cat /tmp/minipet_codesign_err.log)" >&2
            exit 1
        fi
        if ! codesign --verify "$app" 2>>/tmp/minipet_codesign_err.log; then
            echo "[错误] [$rid] 签名校验失败: $(cat /tmp/minipet_codesign_err.log)" >&2
            exit 1
        fi
        if [ ! -f "$app/Contents/MacOS/libbass.dylib" ]; then
            echo "[错误] [$rid] libbass.dylib 未拷贝（RID 判定失效）" >&2
            exit 1
        fi
        local zip="$OUT/MiniPet-$VERSION-macos-${rid#osx-}.zip"
        rm -f "$zip"
        ditto -c -k --sequesterRsrc --keepParent "$app" "$zip"
        echo "[$rid] 完成: $zip"
    fi
}

mkdir -p "$OUT"
for rid in "${RIDS[@]}"; do
    publish_and_zip "$rid"
done

echo ""
echo "=== 全部完成 ==="
ls -lh "$OUT" | grep "MiniPet-$VERSION"
