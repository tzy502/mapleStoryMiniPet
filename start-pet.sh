#!/bin/bash
# MiniPet 桌宠启动脚本（macOS）
# 用法: bash /Users/a502/IdeaProjects/mapleStoryMiniPet/start-pet.sh
#   可选: --build  先编译再启动（默认跳过编译直接启动最新构建）

cd /Users/a502/IdeaProjects/mapleStoryMiniPet || exit 1

export DOTNET_ROOT="/Volumes/SSD/C#"
export PATH="$DOTNET_ROOT:$PATH"
unset HERMES_HOME

# 找下一个日志编号
NEXT=$(($(ls /tmp/minipet-run*.log 2>/dev/null | sed 's/.*run\([0-9]*\)\.log/\1/' | sort -n | tail -1) + 1))
LOG="/tmp/minipet-run${NEXT}.log"

# 杀旧进程
pkill -f "MiniPet/bin/Debug/net9.0/MiniPet" 2>/dev/null
sleep 2

# 可选编译
if [ "$1" = "--build" ]; then
    echo "[start-pet] 编译中..."
    "/Volumes/SSD/C#/dotnet" build MiniPet/MiniPet.csproj --no-restore 2>&1 | grep -E "error CS|个错误" | head -5
    echo "[start-pet] 编译完成"
fi

if [ ! -f MiniPet/bin/Debug/net9.0/MiniPet ]; then
    echo "[start-pet] 二进制不存在，强制编译..."
    "/Volumes/SSD/C#/dotnet" build MiniPet/MiniPet.csproj -c Debug 2>&1 | grep -E "error CS|个错误" | head -5
fi

echo "[start-pet] 启动中... 日志: $LOG"
caffeinate -dimsu "/Volumes/SSD/C#/dotnet" run --project MiniPet/MiniPet.csproj --no-build > "$LOG" 2>&1 &
echo "[start-pet] 后台已启动 (日志 $LOG)"
echo "[start-pet] 查看状态: pgrep -f 'MiniPet/bin' | head -1"
