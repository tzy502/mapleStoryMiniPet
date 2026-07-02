# ChatBalloon 渲染算法

> 冒险岛 WZ 聊天气泡九宫格拼接 + 字体颜色转换
> 供 Claude Code / MiniPet 桌宠使用

## 一、WZ 资源结构

路径: `UI/ChatBalloon.img/{balloonId}`

每个气球 11 个节点:

| 节点 | 类型 | 用途 |
|------|------|------|
| nw, ne, sw, se | 画布 | 四角, 固定尺寸 |
| n, s | 画布 | 上下边, 水平平铺 |
| w, e | 画布 | 左右边, 垂直平铺 |
| c | 画布 | 中心, 双向平铺 |
| arrow | 画布 | 箭头, 替换底行中间S |
| clr | 数值 | 默认字体颜色(int32) |

共 486 个气球样式 (ID: 0~500+)。

## 二、CLR 颜色转换

```python
def clr_to_rgba(clr: int) -> tuple:
    """WZ clr (signed int32) → (R, G, B, A)"""
    u = clr & 0xFFFFFFFF        # 转无符号
    a = (u >> 24) & 0xFF
    r = (u >> 16) & 0xFF
    g = (u >> 8) & 0xFF
    b = u & 0xFF
    return (r, g, b, a)
```

```
clr = -1          → 0xFFFFFFFF → (255,255,255,255) 不透明白
clr = -16777216   → 0xFF000000 → (0,0,0,255)       不透明黑
clr = -65536      → 0xFFFF0000 → (255,0,0,255)     不透明红
clr = 0           → 0x00000000 → (0,0,0,0)         透明
```

> ⚠️ 颜色排列为 **ARGB** (Alpha-Red-Green-Blue), 非 RGBA。

## 三、九宫格拼接

### 布局

```
         NW    N × cols    NE
         W     C × cols    E
         W     C × cols    E    ← rows 行
         W     C × cols    E
         SW  S×n + 箭头 + S×n  SE
```

- 箭头替换底行正中间的 S 块, 不在 S 下方
- 文字渲染在 C 区域内, 水平和垂直居中

### 伪代码

```python
def render_balloon(balloon_id, lines, font, pad_x=10, pad_y=6,
                   min_cols=3, min_rows=2, text_color=(0,0,0,255)):
    """
    balloon_id: ChatBalloon ID
    lines:      文本行数组
    font:       PIL ImageFont
    pad_x/pad_y: 内边距(px)
    返回:       PIL Image (RGBA)
    """
    # ── 1. 测量文本 ──
    line_w = [text_width(l, font) for l in lines]
    line_h = text_height("测", font)
    max_w = max(line_w)
    text_h = line_h * len(lines)

    # ── 2. 加载切片 ──
    tiles = load_tiles(balloon_id)  # nw,n,ne,w,c,e,sw,s,se,arrow
    C_W, C_H = tiles["c"].size

    # ── 3. 计算 C 网格 ──
    content_w = max_w + pad_x * 2
    content_h = text_h + pad_y * 2
    cols = max(min_cols, ceil(content_w / C_W))
    rows = max(min_rows, ceil(content_h / C_H))

    # ── 4. 画布尺寸 ──
    NW_W, _ = tiles["nw"].size
    N_W, N_H = tiles["n"].size
    NE_W, _ = tiles["ne"].size
    SW_H = tiles["sw"].size[1]
    S_H  = tiles["s"].size[1]
    ARR_H = tiles["arrow"].size[1]

    total_w = NW_W + cols * N_W + NE_W
    bottom_h = max(SW_H, S_H, ARR_H)
    total_h = N_H + rows * C_H + bottom_h

    canvas = Image.new('RGBA', (total_w, total_h), (0,0,0,0))

    # ── 5. 顶行: NW + N×cols + NE ──
    canvas.paste(tiles["nw"], (0, 0))
    for i in range(cols):
        canvas.paste(tiles["n"], (NW_W + i*N_W, 0))
    canvas.paste(tiles["ne"], (NW_W + cols*N_W, 0))

    # ── 6. 中行: W + C×cols + E (×rows) ──
    for r in range(rows):
        y = N_H + r * C_H
        canvas.paste(tiles["w"], (0, y))
        for i in range(cols):
            canvas.paste(tiles["c"], (NW_W + i*C_W, y))
        canvas.paste(tiles["e"], (NW_W + cols*C_W, y))

    # ── 7. 底行: SW + S×(cols) + SE, 箭头替换中间S ──
    y_bot = N_H + rows * C_H
    SW_W = tiles["sw"].size[0]
    S_W = tiles["s"].size[0]
    ARR_W = tiles["arrow"].size[0]

    canvas.paste(tiles["sw"], (0, y_bot))
    mid = cols // 2
    for i in range(cols):
        x = SW_W + i * S_W
        if i == mid:
            ax = x + (S_W - ARR_W) // 2
            ay = y_bot + bottom_h - ARR_H  # 底部对齐
            canvas.paste(tiles["arrow"], (ax, ay), tiles["arrow"])
        else:
            canvas.paste(tiles["s"], (x, y_bot))
    canvas.paste(tiles["se"], (SW_W + cols*S_W, y_bot))

    # ── 8. 文字: C 区域内水平+垂直居中 ──
    draw = ImageDraw.Draw(canvas)
    c_x, c_y = NW_W, N_H
    c_w, c_h = cols * C_W, rows * C_H
    gap_y = (c_h - text_h) // 2

    for li, line in enumerate(lines):
        lx = c_x + (c_w - line_w[li]) // 2
        ly = c_y + gap_y + li * line_h
        draw.text((lx, ly), line, fill=text_color, font=font)

    return canvas
```

### 关键约束

1. **C 网格最小 3×2** — 即使文本很小, 至少 3 列 2 行 C 块
2. **文本先量后定框** — 先测量文字尺寸, 再加 padding, 最后算 C 网格
3. **箭头替换而非附加** — 底行 S 块中, 正中间那块被箭头替换, 不增加高度
4. **四角固定不拉伸** — NW/NE/SW/SE 保持原始尺寸
5. **边距平铺** — N/S 水平重复, W/E 垂直重复, C 双向重复

## 四、Swift 侧集成要点

```swift
// 气球资源缓存
// ~/Library/Caches/MiniPet/balloons/{balloonId}/
//   ├── nw.png ... arrow.png    (10张切片)
//   └── clr.json                {"clr": -16777216}

// 渲染时:
// 1. NSAttributedString 测量文字尺寸
// 2. 按九宫格算法计算总尺寸
// 3. CGContext 逐片 draw + 文字 draw
// 4. CALayer/NSTextField 展示
```

## 五、已知气球 ID

| ID | 样式 | clr |
|----|------|-----|
| 0  | 默认(极小/透明) | ? |
| 5  | 聊天戒指风格 | ? |
| 3  | 气泡左倾 | ? |

> 具体 clr 值需通过 `POST /api/wz/property {"path":"UI/ChatBalloon.img/{id}/clr"}`
> 查询已部署的 wiki-backend。
