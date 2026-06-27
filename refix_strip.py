#!/usr/bin/env python3
"""
从现有精灵图条中提取帧，用接地锚点重新对齐，消除 bbox 抖动。
适用于没有 WZ  origin 数据但有精灵图条的宠物。

用法:
  python3 refix_strip.py --strip stand.png --frames 16 --frameW 305 --out stand_fixed.png
"""

import argparse, os, json
from PIL import Image
from collections import defaultdict


def get_bbox(img):
    b = img.getbbox()
    return b if b else (0, 0, img.width, img.height)


def find_ground_anchor(img, center_ratio=0.4):
    """找接地锚点：底部15%区域内，中间center_ratio部分的水平中心"""
    w, h = img.size
    bb = get_bbox(img)
    if bb is None or bb == (0, 0, w, h):
        return (w // 2, h - 1)

    bb_w = bb[2] - bb[0]
    margin = int(bb_w * (1 - center_ratio) / 2)
    left = max(bb[0], bb[0] + margin)
    right = min(bb[2], bb[2] - margin)

    pixels = img.load()
    scan_top = max(bb[1], bb[3] - int((bb[3] - bb[1]) * 0.15))
    contact_xs = []

    for y in range(bb[3] - 1, scan_top - 1, -1):
        row_xs = [x for x in range(left, right) if pixels[x, y][3] > 0]
        if row_xs:
            contact_xs = row_xs
            break

    if contact_xs:
        anchor_x = sum(contact_xs) // len(contact_xs)
    else:
        anchor_x = (bb[0] + bb[2]) // 2

    return (anchor_x, bb[3] - 1)


def extract_frames(strip_path, frame_count, frame_width):
    """从水平精灵图中提取帧"""
    strip = Image.open(strip_path).convert('RGBA')
    h = strip.height
    frames = []
    for i in range(frame_count):
        crop = strip.crop((i * frame_width, 0, (i + 1) * frame_width, h))
        frames.append(crop)
    return frames


def realign_frames(frames, pad=4):
    """
    用接地锚点重新对齐帧，返回固定画布尺寸的帧列表。
    锚点=max_anchor作为参考，每帧按偏移量放置。
    """
    anchors = [find_ground_anchor(f) for f in frames]
    max_ax = max(a[0] for a in anchors)
    max_ay = max(a[1] for a in anchors)

    # 计算画布尺寸
    max_w, max_h = 0, 0
    for img, (ax, ay) in zip(frames, anchors):
        px = max_ax - ax
        py = max_ay - ay
        max_w = max(max_w, px + img.width)
        max_h = max(max_h, py + img.height)

    canvas_w = max_w + pad
    canvas_h = max_h + pad

    fixed = []
    for img, (ax, ay) in zip(frames, anchors):
        canvas = Image.new('RGBA', (canvas_w, canvas_h), (0, 0, 0, 0))
        px = max_ax - ax
        py = max_ay - ay
        canvas.paste(img, (px, py), img)
        fixed.append(canvas)

    return fixed, canvas_w, canvas_h


def make_strip(frames, output_path):
    """将固定画布帧拼接成水平精灵图"""
    if not frames:
        return
    cw, ch = frames[0].size
    total_w = cw * len(frames)
    strip = Image.new('RGBA', (total_w, ch), (0, 0, 0, 0))
    for i, img in enumerate(frames):
        strip.paste(img, (i * cw, 0), img)
    strip.save(output_path)
    return cw, ch


def main():
    parser = argparse.ArgumentParser(description='从精灵图条重新对齐帧，消除抖动')
    parser.add_argument('--strip', required=True, help='输入精灵图条路径')
    parser.add_argument('--frames', type=int, required=True, help='帧数')
    parser.add_argument('--frameW', type=int, required=True, help='当前每帧宽度')
    parser.add_argument('--out', required=True, help='输出路径')
    parser.add_argument('--pad', type=int, default=4, help='padding (默认4)')
    args = parser.parse_args()

    print(f"重新对齐: {args.strip} ({args.frames}f × {args.frameW}px)")
    frames = extract_frames(args.strip, args.frames, args.frameW)
    fixed, cw, ch = realign_frames(frames, pad=args.pad)
    nw, nh = make_strip(fixed, args.out)
    print(f"  → {args.out}: {args.frames}f, {nw}x{nh} (曾 {args.frames*args.frameW}x{ch})")
    print(f"  帧尺寸: {cw}x{ch}")


if __name__ == '__main__':
    main()
