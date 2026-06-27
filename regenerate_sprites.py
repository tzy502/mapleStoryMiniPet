#!/usr/bin/env python3
"""
重新生成桌宠精灵图 — 使用 WZ origin / 启发式锚点替代 bbox 对齐，消除抖动。

用法:
  python3 regenerate_sprites.py --mob 9602538 --xml <xml_path> --frames <frames_dir> [--out <output_dir>]
  python3 regenerate_sprites.py --mob 8880150 --frames <frames_dir> [--out <output_dir>]
"""

import argparse, os, re, sys, json
from PIL import Image
from collections import defaultdict

# ═══════════════════════════════════════════════════════
# 通用工具
# ═══════════════════════════════════════════════════════

def get_bbox(img):
    """返回非透明像素的 bbox，若全透明则返回画布尺寸"""
    b = img.getbbox()
    return b if b else (0, 0, img.width, img.height)


def load_frames(frames_dir, anim_matcher=None, default_anim=None):
    """
    从目录加载动画帧。
    默认用 name.N.png 格式匹配（如 stand.0.png, move.12.png）
    如果指定 default_anim，则把所有 N.png 格式的文件归到该动画名下。
    """
    files = [f for f in os.listdir(frames_dir)
             if f.endswith('.png') and os.path.getsize(os.path.join(frames_dir, f)) > 100]

    anims = defaultdict(list)
    for f in files:
        if anim_matcher:
            result = anim_matcher(f)
            if result:
                anim_name, fn = result
                anims[anim_name].append((fn, f))
        elif default_anim:
            m = re.match(r'(\d+)\.png$', f)
            if m:
                anims[default_anim].append((int(m.group(1)), f))
        else:
            m = re.match(r'(.+)\.(\d+)\.png$', f)
            if m:
                anims[m.group(1)].append((int(m.group(2)), f))

    result = {}
    for name in sorted(anims):
        frames = anims[name]
        frames.sort(key=lambda x: x[0])
        imgs = [(num, Image.open(os.path.join(frames_dir, f)).convert('RGBA'))
                for num, f in frames]
        result[name] = imgs
    return result


def make_strip(fixed_frames, output_path):
    """将固定尺寸帧拼成水平精灵图，保存到 output_path"""
    if not fixed_frames:
        return None, None, 0
    cw, ch = fixed_frames[0].size
    total_w = cw * len(fixed_frames)
    strip = Image.new('RGBA', (total_w, ch), (0, 0, 0, 0))
    for i, img in enumerate(fixed_frames):
        strip.paste(img, (i * cw, 0), img)
    strip.save(output_path)
    return cw, ch, len(fixed_frames)


# ═══════════════════════════════════════════════════════
# 方法1：WZ Origin 对齐（精确，需要 WZ XML 数据）
# ═══════════════════════════════════════════════════════

def parse_wz_origins(xml_path):
    """
    解析冒险岛 WZ XML，返回 {anim_name: [(originX, originY), ...]}
    origin 按帧号 0→N 顺序排列。
    """
    with open(xml_path, 'r', encoding='utf-8-sig') as f:
        text = f.read()

    origins = defaultdict(list)
    current_stack = []

    lines = text.split('\n')
    for line in lines:
        m_open = re.search(r'<dir name="(\w+)"', line)
        if m_open:
            name = m_open.group(1)
            if name in ('info', 'skill', 'attack', 'qrex', '9602538.img'):
                current_stack.append(None)
            else:
                current_stack.append(name)
            continue

        if '</dir>' in line and current_stack:
            current_stack.pop()
            continue

        m_orig = re.search(r'<vector name="origin" value="(\d+),\s*(\d+)"', line)
        if m_orig:
            ox, oy = int(m_orig.group(1)), int(m_orig.group(2))
            for anim in reversed(current_stack):
                if anim is not None:
                    origins[anim].append((ox, oy))
                    break

    return dict(origins)


def align_by_wz_origin(anims_frames, wz_origins, pad=4):
    """
    使用 WZ origin 数据对齐帧（与 GifUtil.wzImgDetailToGif 逻辑一致）。
    """
    result = {}
    for anim_name, frames in anims_frames.items():
        origins = wz_origins.get(anim_name, [])
        if not origins or len(origins) != len(frames):
            continue

        max_ox = max(o[0] for o in origins)
        max_oy = max(o[1] for o in origins)

        max_r = 0
        max_b = 0
        for (fn, img), (ox, oy) in zip(frames, origins):
            px = max_ox - ox
            py = max_oy - oy
            max_r = max(max_r, px + img.width)
            max_b = max(max_b, py + img.height)

        canvas_w = max_r + pad
        canvas_h = max_b + pad

        print(f"  {anim_name}: {len(frames)}f, origin max=({max_ox},{max_oy}), canvas={canvas_w}x{canvas_h}")

        fixed = []
        for (fn, img), (ox, oy) in zip(frames, origins):
            canvas = Image.new('RGBA', (canvas_w, canvas_h), (0, 0, 0, 0))
            px = max_ox - ox
            py = max_oy - oy
            canvas.paste(img, (px, py), img)
            fixed.append(canvas)

        result[anim_name] = fixed
    return result


# ═══════════════════════════════════════════════════════
# 方法2：启发式「接地锚点」对齐（无 WZ XML 时）
# ═══════════════════════════════════════════════════════

def find_ground_anchor(img, center_ratio=0.4):
    """
    找「接地锚点」: 在底部 15% 区域内，取中间 center_ratio 区域非透明像素的水平中心。
    返回 (anchor_x, anchor_y)，相对于图片左上角。
    """
    w, h = img.size
    bb = get_bbox(img)
    if bb is None or bb == (0, 0, w, h):
        return (w // 2, h - 1)

    # 计算扫描的水平范围（bbox 中间 center_ratio 区域）
    bb_w = bb[2] - bb[0]
    margin = int(bb_w * (1 - center_ratio) / 2)
    left = max(bb[0], bb[0] + margin)
    right = min(bb[2], bb[2] - margin)

    # 在底部 15% 区域找非透明像素
    pixels = img.load()
    scan_top = max(bb[1], bb[3] - int((bb[3] - bb[1]) * 0.15))
    contact_xs = []

    for y in range(bb[3] - 1, scan_top - 1, -1):
        row_xs = []
        for x in range(left, right):
            if pixels[x, y][3] > 0:
                row_xs.append(x)
        if row_xs:
            contact_xs = row_xs
            break

    if contact_xs:
        anchor_x = sum(contact_xs) // len(contact_xs)
    else:
        anchor_x = (bb[0] + bb[2]) // 2

    anchor_y = bb[3] - 1
    return (anchor_x, anchor_y)


def align_by_ground_anchor(anims_frames, pad=8):
    """接地锚点对齐"""
    result = {}
    for anim_name, frames in anims_frames.items():
        imgs = [img for _, img in frames]
        anchors = [find_ground_anchor(img) for img in imgs]

        max_ax = max(a[0] for a in anchors)
        max_ay = max(a[1] for a in anchors)

        max_w = 0
        max_h = 0
        for img, (ax, ay) in zip(imgs, anchors):
            px = max_ax - ax
            py = max_ay - ay
            max_w = max(max_w, px + img.width)
            max_h = max(max_h, py + img.height)

        canvas_w = max_w + pad
        canvas_h = max_h + pad

        print(f"  {anim_name}: {len(frames)}f, anchor max=({max_ax},{max_ay}), canvas={canvas_w}x{canvas_h}")

        fixed = []
        for img, (ax, ay) in zip(imgs, anchors):
            canvas = Image.new('RGBA', (canvas_w, canvas_h), (0, 0, 0, 0))
            px = max_ax - ax
            py = max_ay - ay
            canvas.paste(img, (px, py), img)
            fixed.append(canvas)

        result[anim_name] = fixed
    return result


# ═══════════════════════════════════════════════════════
# 方法3：BBox 对齐（原方案，保留兼容）
# ═══════════════════════════════════════════════════════

def align_by_bbox(anims_frames, pad=8):
    """原 fix_origin.py 逻辑"""
    result = {}
    for anim_name, frames in anims_frames.items():
        imgs = [img for _, img in frames]
        bboxes = [get_bbox(img) for img in imgs]
        max_w = max(b[2] - b[0] for b in bboxes)
        max_h = max(b[3] - b[1] for b in bboxes)
        cw, ch = max_w + pad * 2, max_h + pad * 2

        print(f"  {anim_name}: {len(frames)}f, bbox max={max_w}x{max_h}, canvas={cw}x{ch}")

        fixed = []
        for img, bb in zip(imgs, bboxes):
            canvas = Image.new('RGBA', (cw, ch), (0, 0, 0, 0))
            cx = (cw - (bb[2] - bb[0])) // 2 - bb[0]
            cy = ch - pad - (bb[3] - bb[1]) - bb[1]
            canvas.paste(img, (cx, cy), img)
            fixed.append(canvas)

        result[anim_name] = fixed
    return result


# ═══════════════════════════════════════════════════════
# 主入口
# ═══════════════════════════════════════════════════════

def main():
    parser = argparse.ArgumentParser(description='重新生成桌宠精灵图，消除 origin 抖动')
    parser.add_argument('--mob', required=True, help='怪物ID')
    parser.add_argument('--xml', help='WZ XML 文件路径')
    parser.add_argument('--frames', required=True, help='原始 PNG 帧目录')
    parser.add_argument('--out', default=None, help='输出目录（默认 frames_dir/../sprites_v2）')
    parser.add_argument('--method', choices=['wz', 'ground', 'bbox'], default='auto')
    parser.add_argument('--anim-names', help='逗号分隔的动画名，不指定则全部处理')
    parser.add_argument('--anim-rename', help='动画名映射: old1=new1,old2=new2')
    parser.add_argument('--default-anim', help='当帧文件为 N.png 格式时使用的默认动画名')
    args = parser.parse_args()

    mob = args.mob
    frames_dir = args.frames
    output_dir = args.out or os.path.join(os.path.dirname(frames_dir), 'sprites_v2')
    os.makedirs(output_dir, exist_ok=True)

    print(f"=== 重新生成 {mob} 精灵图 ===")
    print(f"  帧目录: {frames_dir}")
    print(f"  输出: {output_dir}")

    anims_frames = load_frames(frames_dir, default_anim=args.default_anim)
    print(f"  动画: {list(anims_frames.keys())}")

    # 确定方法
    method = args.method
    if method == 'auto':
        if args.xml and os.path.exists(args.xml):
            origins = parse_wz_origins(args.xml)
            has_origins = any(
                anim in origins and len(origins[anim]) == len(frames)
                for anim, frames in anims_frames.items()
            )
            method = 'wz' if has_origins else 'ground'
        else:
            method = 'ground'
    print(f"  方法: {method}")

    # 对齐
    if method == 'wz':
        origins = parse_wz_origins(args.xml)
        fixed_anims = align_by_wz_origin(anims_frames, origins)
    elif method == 'ground':
        fixed_anims = align_by_ground_anchor(anims_frames)
    else:
        fixed_anims = align_by_bbox(anims_frames)

    # 筛选
    if args.anim_names:
        target = set(args.anim_names.split(','))
        fixed_anims = {k: v for k, v in fixed_anims.items() if k in target}

    # 重命名
    rename_map = {}
    if args.anim_rename:
        for pair in args.anim_rename.split(','):
            old, new = pair.split('=')
            rename_map[old.strip()] = new.strip()

    # 输出
    config = {'name': f'MapleStory_Mob_{mob}', 'version': 2, 'type': 'sprite', 'sprites': {}}

    for anim_name, frames in fixed_anims.items():
        out_name = rename_map.get(anim_name, anim_name)
        safe_name = out_name.replace('.', '_')
        out_path = os.path.join(output_dir, f'{safe_name}.png')
        cw, ch, count = make_strip(frames, out_path)
        if cw:
            config['sprites'][out_name] = {
                'file': f'{safe_name}.png',
                'frames': count,
                'frameWidth': cw,
                'frameHeight': ch
            }
            print(f"  → {safe_name}.png: {count}f, {cw}x{ch}")

    config_path = os.path.join(output_dir, 'pet_config.json')
    with open(config_path, 'w') as f:
        json.dump(config, f, indent=2, ensure_ascii=False)
    print(f"\n配置: {config_path}")
    print("完成！")


if __name__ == '__main__':
    main()
