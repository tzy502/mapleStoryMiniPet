# 调试指南

## 启动失败

### 构建错误

```bash
cd /Users/a502/IdeaProjects/mapleStoryMiniPet
swift build
```

常见问题：
- **Sandbox 权限**：首次运行可能需要辅助功能权限，系统偏好设置 → 隐私与安全性 → 辅助功能
- **Xcode 未安装**：`xcode-select --install`

### 精灵图缺失

检查 `sprites/` 目录下的文件：

```bash
ls -la sprites/
# 应有: stand.png move.png attack1.png skill1.png
```

## 运行时问题

### 桌宠不显示

```bash
# 检查进程
pgrep -l MiniPet

# 重新启动
pkill -f MiniPet
./start.sh
```

### 动画抖动

精灵图可能未对齐。运行修复：

```bash
python3 refix_strip.py --strip sprites/stand.png --frames 16 --frameW 312 --out sprites/stand.png
# 对所有动画重复，然后重启
```

### Hermes 感知不工作

MiniPet 每 3 秒检查 `~/.pet/pet_session.jsonl`。确保 Hermes cron job 正在写入：

```bash
ls -la ~/.pet/pet_session.jsonl
cat ~/.pet/pet_session.jsonl | tail -5
```

## 精灵图验证

```bash
python3 -c "
from PIL import Image
import os
for f in ['stand.png','move.png','attack1.png','skill1.png']:
    img = Image.open(f'sprites/{f}')
    print(f'{f}: {img.size}')
"
```

## 进程管理

```bash
# 查看
pgrep -l MiniPet

# 终止
pkill -f MiniPet

# 启动
./start.sh
```

## 日志

MiniPet 标准输出/错误被丢弃（`nohup > /dev/null 2>&1`）。如需调试输出，改为：

```bash
.build/arm64-apple-macosx/debug/MiniPet 2>&1 | tee minipet.log
```
