#!/bin/bash
# MiniPet 启动脚本
# 用法: ./start.sh [--build]

set -e
cd "$(dirname "$0")"

BUILD_DIR=".build/arm64-apple-macosx/debug"
BINARY="$BUILD_DIR/MiniPet"

# 先杀掉已运行的实例
pkill -f MiniPet 2>/dev/null || true
sleep 0.5

# 构建（如需）
if [ "$1" = "--build" ] || [ ! -f "$BINARY" ]; then
    echo "🔨 构建 MiniPet..."
    swift build
fi

echo "🚀 启动 MiniPet..."
nohup "$BINARY" > /dev/null 2>&1 &
echo "   进程 PID: $!"
