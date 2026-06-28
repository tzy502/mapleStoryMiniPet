#!/bin/bash
# MiniPet 启动脚本
# 用法: ./start.sh [--build] [--mob <id>]
#
# 参数:
#   --build        重新编译（默认使用已有编译产物）
#   --mob <id>     指定初始怪物ID（默认 8880150）
#   --debug-api    打印 API 调试信息
#   --help         显示帮助

set -e
cd "$(dirname "$0")"

BUILD_DIR=".build/arm64-apple-macosx/debug"
BINARY="$BUILD_DIR/MiniPet"

# 先杀掉已运行的实例
pkill -f MiniPet 2>/dev/null || true
sleep 0.5

# 构建（如需）
BUILD_FLAG=""
ARGS=()
while [[ $# -gt 0 ]]; do
    case "$1" in
        --build) BUILD_FLAG="1"; shift ;;
        --mob) ARGS+=("--mob" "$2"); shift 2 ;;
        --delete-cache)
            python3 "$(dirname "$0")/fetch_and_generate.py" "$2" --delete
            echo "已删除缓存: $2"
            exit 0
            ;;
        --update)
            python3 "$(dirname "$0")/fetch_and_generate.py" "$2" --update
            exit 0
            ;;
        --add-mob)
            python3 "$(dirname "$0")/fetch_and_generate.py" "$2"
            echo "已添加: $2"
            exit 0
            ;;
        --debug-api) ARGS+=("--debug-api"); shift ;;
        --help)
            echo "用法: $0 [--build] [--mob <id>] [--delete-cache <codes>] [--update <codes>]"
            echo "  --build             重新编译"
            echo "  --mob <id>          指定初始怪物ID"
            echo "  --delete-cache <ids> 删除指定怪物缓存（逗号分隔）"
            echo "  --update <ids>       强制重新获取（逗号分隔）"
            echo "  --debug-api          打印 API 调试信息"
            exit 0
            ;;
        *) shift ;;
    esac
done

if [ "$BUILD_FLAG" = "1" ] || [ ! -f "$BINARY" ]; then
    echo "🔨 构建 MiniPet..."
    swift build
fi

echo "🚀 启动 MiniPet..."
nohup "$BINARY" "${ARGS[@]}" > /dev/null 2>&1 &
echo "   进程 PID: $!"
