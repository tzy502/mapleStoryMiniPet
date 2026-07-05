#!/usr/bin/env python3
"""
Fetch mob frames from wiki-backend API and generate sprite strips.

Usage:
  python3 fetch_and_generate.py <mob_code> [--api http://192.168.3.46:10502] [--out <cache_dir>]

Flow:
  1. Get mob name via POST /api/wz/data/query/string
  2. Get frame paths via POST /api/wz/renderImgPath
  3. If no images, check for link via POST /api/wz/tree
  4. Download frames via GET /api/wz/image?path=...
  5. Ground-anchor align and generate sprite strips + pet_config.json

Author: self-contained — no imports from regenerate_sprites.py
"""

import argparse, json, os, re, sys
from collections import defaultdict
from urllib.request import Request, urlopen

from PIL import Image

# ══════════════════════════════════════════════════════════
#  WZ Origin alignment  (matches GifUtil.wzImgDetailToGif)
# ══════════════════════════════════════════════════════════

def fetch_wz_xml(api_base, path):
    """Fetch raw WZ XML for a given path. Returns XML string or None."""
    try:
        body = json.dumps({"path": path}).encode()
        req = Request(f'{api_base}/api/wz/xml',
                       data=body,
                       headers={'Content-Type': 'application/json'})
        with urlopen(req, timeout=15) as resp:
            data = json.loads(resp.read().decode())
            return data.get('xml', '') if isinstance(data, dict) else None
    except Exception:
        return None


def parse_origins_from_xml(xml_text):
    """Parse per-frame origin vectors from WZ XML.
    Returns dict {frame_number: (originX, originY)} keyed by frame name."""
    if not xml_text:
        return {}
    origins = {}
    # Match: <canvas name="N">...<vector name="origin" value="X, Y"/>...</canvas>
    for m in re.finditer(
            r'<(?:canvas|png)\s+name="(\d+)".*?'
            r'<vector\s+name="origin"\s+value="(\d+),\s*(\d+)"',
            xml_text, re.DOTALL):
        frame_num = int(m.group(1))
        ox, oy = int(m.group(2)), int(m.group(3))
        origins[frame_num] = (ox, oy)
    return origins


def align_by_wz_origin(anims_frames, mob_code, api_base, pad=4, global_origin=None, entity_type='mob'):
    """
    Align ALL animations to a shared global origin point.
    Returns (result, origins) where origins is {anim_name: (originX, originY)}.
    Formula: drawX = global_max_ox - frame.originX
             drawY = global_max_oy - frame.originY
    """
    origins_out = {}  # {anim_name: (originX, originY)}
    # Phase 1: Collect all origin data across all animations
    all_origins = []  # (ox, oy)
    anim_matched = {}  # {anim_name: [(img, ox, oy), ...]}
    has_wz_data = False
    wz_prefix = 'Npc' if entity_type == 'npc' else 'Mob'

    for anim_name, frames in anims_frames.items():
        xml_path = f'{wz_prefix}/{mob_code}.img/{anim_name}'
        xml_text = fetch_wz_xml(api_base, xml_path)
        origin_dict = parse_origins_from_xml(xml_text)

        matched = []
        for fn, img in frames:
            if fn in origin_dict:
                ox, oy = origin_dict[fn]
                all_origins.append((ox, oy))
                matched.append((img, ox, oy))
                has_wz_data = True
            else:
                matched.append((img, None, None))
        anim_matched[anim_name] = matched

    # Phase 2: Compute global max origin (shared across ALL animations)
    if has_wz_data:
        if global_origin is not None:
            global_max_ox, global_max_oy = global_origin
            print(f"  Global WZ origin (override): ({global_max_ox}, {global_max_oy})")
        else:
            global_max_ox = max(o[0] for o in all_origins)
            global_max_oy = max(o[1] for o in all_origins)
            print(f"  Global WZ origin: ({global_max_ox}, {global_max_oy})")
    else:
        global_max_ox = global_max_oy = 0

    # Phase 3: Align each animation with the global origin
    result = {}
    for anim_name, matched in anim_matched.items():
        imgs = [img for img, _, _ in matched]
        # All animations share the same global max origin (matching GifUtil.wzImgDetailToGif)
        origins_out[anim_name] = (global_max_ox, global_max_oy) if has_wz_data else (0, 0)

        if has_wz_data:
            max_w = max_h = 0
            for img, ox, oy in matched:
                if ox is None:
                    ox, oy = global_max_ox // 2, global_max_oy // 2
                draw_x = global_max_ox - ox
                draw_y = global_max_oy - oy
                max_w = max(max_w, draw_x + img.width)
                max_h = max(max_h, draw_y + img.height)

            canvas_w, canvas_h = max_w + pad, max_h + pad
            print(f"  {anim_name}: {len(matched)}f, canvas={canvas_w}x{canvas_h}")

            fixed = []
            for img, ox, oy in matched:
                if ox is None:
                    ox, oy = global_max_ox // 2, global_max_oy // 2
                canvas = Image.new('RGBA', (canvas_w, canvas_h), (0, 0, 0, 0))
                canvas.paste(img, (global_max_ox - ox, global_max_oy - oy), img)
                fixed.append(canvas)
            result[anim_name] = fixed
        else:
            # Fallback: ground anchor (per-animation, no WZ data)
            anchors = _ground_anchors(imgs)
            max_ax = max(a[0] for a in anchors)
            max_ay = max(a[1] for a in anchors)
            # Update per-animation origin for ground anchor mode
            origins_out[anim_name] = (max_ax, max_ay)
            max_w = max_h = 0
            for img, (ax, ay) in zip(imgs, anchors):
                px, py = max_ax - ax, max_ay - ay
                max_w = max(max_w, px + img.width)
                max_h = max(max_h, py + img.height)
            canvas_w, canvas_h = max_w + pad, max_h + pad
            print(f"  {anim_name}: {len(matched)}f, ground anchor, canvas={canvas_w}x{canvas_h}")
            fixed = []
            for img, (ax, ay) in zip(imgs, anchors):
                canvas = Image.new('RGBA', (canvas_w, canvas_h), (0, 0, 0, 0))
                canvas.paste(img, (max_ax - ax, max_ay - ay), img)
                fixed.append(canvas)
            result[anim_name] = fixed

    # Phase 4: Unify canvas size — all animations share max width/height
    if result:
        global_cw = max(frames[0].width for frames in result.values())
        global_ch = max(frames[0].height for frames in result.values())
        print(f"  Unified canvas: {global_cw}x{global_ch}")

        unified = {}
        for anim_name, frames in result.items():
            cw, ch = frames[0].size
            if cw == global_cw and ch == global_ch:
                unified[anim_name] = frames
            else:
                padded = []
                for img in frames:
                    canvas = Image.new('RGBA', (global_cw, global_ch), (0, 0, 0, 0))
                    canvas.paste(img, (0, 0), img)
                    padded.append(canvas)
                unified[anim_name] = padded
        return unified, origins_out

    return result, origins_out


def _ground_anchors(imgs):
    """Compute ground anchors for a list of images (fallback alignment)."""
    anchors = []
    for img in imgs:
        b = img.getbbox()
        if not b or b == (0, 0, img.width, img.height):
            anchors.append((img.width // 2, img.height - 1))
            continue
        bb_w = b[2] - b[0]
        margin = int(bb_w * 0.3)
        left, right = max(b[0], b[0] + margin), min(b[2], b[2] - margin)
        scan_top = max(b[1], b[3] - int((b[3] - b[1]) * 0.15))
        pixels = img.load()
        best = None
        for y in range(b[3] - 1, scan_top - 1, -1):
            row = [x for x in range(left, right) if pixels[x, y][3] > 0]
            if row:
                best = (sum(row) // len(row), b[3] - 1)
                break
        anchors.append(best or ((b[0] + b[2]) // 2, b[3] - 1))
    return anchors


def make_strip(frames, output_path):
    """Compose fixed-size frames into a horizontal sprite strip. Returns (w, h, count)."""
    if not frames:
        return None, None, 0
    cw, ch = frames[0].size
    strip = Image.new('RGBA', (cw * len(frames), ch), (0, 0, 0, 0))
    for i, img in enumerate(frames):
        strip.paste(img, (i * cw, 0), img)
    strip.save(output_path)
    return cw, ch, len(frames)


# ══════════════════════════════════════════════════════════
#  API helpers
# ══════════════════════════════════════════════════════════

def api_post(api_base, path, body):
    req = Request(f'{api_base}{path}', data=json.dumps(body).encode(),
                  headers={'Content-Type': 'application/json'})
    with urlopen(req) as resp:
        return json.loads(resp.read().decode())


def api_get_image(api_base, path):
    with urlopen(f'{api_base}/api/wz/image?path={path}') as resp:
        return resp.read()


# ══════════════════════════════════════════════════════════
#  Main
# ══════════════════════════════════════════════════════════

def main():
    parser = argparse.ArgumentParser(description='Fetch mob/npc frames and generate sprite strips')
    parser.add_argument('mob_code', nargs='?', help='Mob code (e.g. 8880150), or comma-separated list')
    parser.add_argument('--api', default='http://127.0.0.1:10502', help='API base URL (tries 127.0.0.1 first)')
    parser.add_argument('--out', default=None, help='Output cache dir (default: ~/Library/Caches/MiniPet/{code})')
    parser.add_argument('--delete', action='store_true', help='Delete cached sprites for specified codes')
    parser.add_argument('--update', action='store_true', help='Force re-fetch even if cache exists')
    parser.add_argument('--type', default='mob', choices=['mob', 'npc'], help='Entity type: mob or npc (default: mob)')
    args = parser.parse_args()

    if not args.mob_code:
        print("用法: python3 fetch_and_generate.py <code>[,code2,...] [--delete|--update] [--type mob|npc]")
        sys.exit(1)

    codes = [c.strip() for c in args.mob_code.split(',') if c.strip()]
    entity_type = args.type
    api_base = args.api.rstrip('/')
    # Auto-detect: try health check, if fails, try fallback
    fallback = 'http://192.168.3.46:10502'
    try:
        req = Request(f'{api_base}/api/health')
        with urlopen(req, timeout=3) as resp:
            json.loads(resp.read())
    except Exception:
        print(f'  {api_base} unreachable, trying {fallback}')
        try:
            req = Request(f'{fallback}/api/health')
            with urlopen(req, timeout=3) as resp:
                json.loads(resp.read())
            api_base = fallback
        except Exception:
            print(f'  Both APIs unreachable, using {api_base} anyway')

    if len(codes) > 1:
        # ── Batch merge mode ──
        merged_dir = '_'.join(codes)
        prefix_dir = f'{entity_type}_{merged_dir}'
        out_dir = args.out or os.path.expanduser(f'~/Library/Caches/MiniPet/{prefix_dir}')
        raw_dir = os.path.join(out_dir, 'raw')

        if args.delete:
            import shutil
            for code in codes:
                d = os.path.expanduser(f'~/Library/Caches/MiniPet/{entity_type}_{code}')
                if os.path.exists(d):
                    shutil.rmtree(d)
                    print(f'  Deleted individual cache: {d}')
            if os.path.exists(out_dir):
                shutil.rmtree(out_dir)
                print(f'  Deleted merged cache: {out_dir}')
            return

        if args.update:
            if os.path.exists(out_dir):
                for f in os.listdir(out_dir):
                    if f.endswith('.png') or f == 'pet_config.json':
                        os.remove(os.path.join(out_dir, f))
                print(f'  Merged cache cleared for {",".join(codes)}')

        _process_batch(codes, api_base, out_dir, raw_dir, entity_type)
    else:
        # ── Single code mode ──
        for code in codes:
            cache_prefix = f'{entity_type}_{code}'
            out_dir = args.out or os.path.expanduser(f'~/Library/Caches/MiniPet/{cache_prefix}')
            raw_dir = os.path.join(out_dir, 'raw')

            if args.delete:
                if os.path.exists(out_dir):
                    import shutil
                    shutil.rmtree(out_dir)
                    print(f'  Deleted cache: {out_dir}')
                else:
                    print(f'  No cache found for {code}')
                continue

            if args.update:
                if os.path.exists(out_dir):
                    files = os.listdir(out_dir)
                    for f in files:
                        if f.endswith('.png') or f == 'pet_config.json':
                            os.remove(os.path.join(out_dir, f))
                    print(f'  Cache cleared for {code}')
            _process_mob(code, api_base, out_dir, raw_dir, entity_type)


def _fetch_code_groups(code, api_base, entity_type='mob'):
    """Fetch frame paths for a single code. Returns {action: [(frame_num, path), ...]} or None if link."""
    groups = defaultdict(list)

    if entity_type == 'npc':
        # NPC path: Npc/_Canvas/{code}.img
        try:
            paths_resp = api_post(api_base, '/api/wz/renderImgPath',
                                  {'type': 'npc', 'code': code,
                                   'path': f'Npc/_Canvas/{code}.img'})
        except Exception:
            paths_resp = {'success': False}

        if paths_resp.get('success') and paths_resp.get('images'):
            paths = paths_resp['images']
            for p in paths:
                parts = p.split('/')
                if 'info' in parts:
                    continue
                if len(parts) < 2 or not parts[-1].isdigit():
                    continue
                groups[parts[-2]].append((int(parts[-1]), p))
            print(f'    Via Npc/_Canvas: {len(paths)} paths, {len(groups)} actions')

        if not groups:
            print('    Npc/_Canvas not found, trying per-animation tree...')
            try:
                tree = api_post(api_base, '/api/wz/tree',
                                {'type': 'npc', 'code': code,
                                 'path': f'Npc/{code}.img'})
                anim_names = []
                for node in tree:
                    if node.get('type') == '容器':
                        anim_names.append(node['name'])
            except Exception:
                anim_names = []

            for anim in anim_names:
                try:
                    anim_resp = api_post(api_base, '/api/wz/renderImgPath',
                                         {'type': 'npc', 'code': code,
                                          'path': f'Npc/{code}.img/{anim}'})
                    if anim_resp.get('success') and anim_resp.get('images'):
                        for p in anim_resp['images']:
                            parts = p.split('/')
                            if 'info' in parts:
                                continue
                            if len(parts) < 2 or not parts[-1].isdigit():
                                continue
                            groups[anim].append((int(parts[-1]), p))
                except Exception:
                    pass

        for action in sorted(groups):
            groups[action].sort(key=lambda x: x[0])

        if groups:
            print(f'    Actions: {", ".join(sorted(groups.keys()))} ({sum(len(v) for v in groups.values())} frames)')
        return dict(groups)
    else:
        # Original mob logic
        try:
            paths_resp = api_post(api_base, '/api/wz/renderImgPath',
                              {'type': 'mob', 'code': code,
                               'path': f'Mob/_Canvas/{code}.img'})
    except Exception:
        paths_resp = {'success': False}

    if paths_resp.get('success') and paths_resp.get('images'):
        paths = paths_resp['images']
        for p in paths:
            parts = p.split('/')
            if 'info' in parts:
                continue
            if len(parts) < 2 or not parts[-1].isdigit():
                continue
            groups[parts[-2]].append((int(parts[-1]), p))
        print(f'    Via _Canvas: {len(paths)} paths, {len(groups)} actions')

    # Strategy B: Per-animation query for mobs not in _Canvas
    if not groups:
        print('    _Canvas not found, discovering animations via tree...')
        try:
            tree = api_post(api_base, '/api/wz/tree',
                            {'type': 'mob', 'code': code,
                             'path': f'Mob/{code}.img'})
            anim_names = []
            for node in tree:
                if node.get('type') == '容器':
                    anim_names.append(node['name'])
        except Exception:
            anim_names = []

        if not anim_names:
            print('    No animations found. Checking for linked mob...')
            try:
                tree = api_post(api_base, '/api/wz/tree',
                                {'type': 'mob', 'code': code,
                                 'path': f'Mob/{code}.img/info/link'})
                link_target = _extract_value(tree, code)
                if link_target:
                    print(f'    -> links to {link_target}')
                    return None
            except Exception:
                pass
            print('    No link found and no images available.')
            return {}

        print(f'    Found {len(anim_names)} animations: {anim_names}')
        for anim in anim_names:
            try:
                anim_resp = api_post(api_base, '/api/wz/renderImgPath',
                                     {'type': 'mob', 'code': code,
                                      'path': f'Mob/{code}.img/{anim}'})
                if anim_resp.get('success') and anim_resp.get('images'):
                    for p in anim_resp['images']:
                        parts = p.split('/')
                        if 'info' in parts:
                            continue
                        if len(parts) < 2 or not parts[-1].isdigit():
                            continue
                        groups[anim].append((int(parts[-1]), p))
            except Exception:
                pass

    for action in sorted(groups):
        groups[action].sort(key=lambda x: x[0])

    if groups:
        print(f'    Actions: {", ".join(sorted(groups.keys()))} ({sum(len(v) for v in groups.values())} frames)')
        for action in sorted(groups):
            tag = 'single' if len(groups[action]) == 1 else f'{len(groups[action])}f'
            print(f'      {action}: {tag}')

    return dict(groups)


def _process_batch(codes, api_base, out_dir, raw_dir, entity_type='mob'):
    """Batch merge: fetch frames from multiple codes, merge with {action}-{code} naming."""
    first_code = codes[0]

    # ── 1. Get mob name from first code ──
    print(f' [1/5] Fetching name for merged {entity_type} (primary: {first_code})...')
    try:
        name_data = api_post(api_base, '/api/wz/data/query/string',
                             {'type': entity_type, 'code': first_code})
        if isinstance(name_data, list) and name_data:
            mob_name = name_data[0].get('name', ','.join(codes))
        elif isinstance(name_data, dict):
            mob_name = name_data.get('name', ','.join(codes))
        else:
            mob_name = ','.join(codes)
    except Exception as e:
        print(f'  Warning: name fetch failed ({e})')
        mob_name = ','.join(codes)
    print(f'  Name: {mob_name}')

    # ── 2. Fetch frame paths for each code ──
    print(f' [2/5] Fetching frame paths for {len(codes)} codes...')
    all_groups = {}  # {code: {action: [(frame_num, path), ...]}}
    for code in codes:
        print(f'  Code {code}:')
        groups = _fetch_code_groups(code, api_base, entity_type)
        if groups is None:
            print(f'    -> links to another {entity_type}, skipping')
            continue
        if groups:
            all_groups[code] = groups

    if not all_groups:
        print('  No valid frames found for any code.')
        return

    # ── 3. Download frames for each code ──
    print(f' [3/5] Downloading frames...')
    for code, groups in all_groups.items():
        print(f'  Code {code}:')
        for action in sorted(groups):
            frames = groups[action]
            action_dir = os.path.join(raw_dir, code, action)
            os.makedirs(action_dir, exist_ok=True)
            downloaded = 0
            for fn, path in frames:
                dst = os.path.join(action_dir, f'{fn}.png')
                if not os.path.exists(dst):
                    try:
                        img_data = api_get_image(api_base, path)
                        if img_data and len(img_data) > 100:
                            with open(dst, 'wb') as f:
                                f.write(img_data)
                        else:
                            continue
                    except Exception as e:
                        print(f'      ! {action}/{fn}.png: {e}')
                        continue
                else:
                    if os.path.getsize(dst) < 100:
                        os.remove(dst)
                        continue
                downloaded += 1
            print(f'    {action}: {downloaded}/{len(frames)} frames saved')

    # ── 4. Align all codes with cross-code global max origin ──
    print(f' [4/5] Aligning frames (WZ origin, cross-code global)...')

    # Phase A: Load frames grouped by code→action
    code_anims = {}  # {code: {action: [(fn, img), ...]}}
    for code in sorted(all_groups):
        code_groups = all_groups[code]
        anims_frames = {}
        for action in sorted(code_groups):
            action_dir = os.path.join(raw_dir, code, action)
            if not os.path.isdir(action_dir):
                continue
            files = sorted(os.listdir(action_dir), key=lambda f: int(re.sub(r'\D', '', f)))
            imgs = []
            for f in files:
                if f.endswith('.png'):
                    num = int(f.replace('.png', ''))
                    img = Image.open(os.path.join(action_dir, f)).convert('RGBA')
                    imgs.append((num, img))
            if imgs:
                anims_frames[action] = imgs
        if anims_frames:
            code_anims[code] = anims_frames

    # Phase B: Collect all WZ origins across all codes, compute cross-code global max
    all_wz_origins = []
    for code, anims_frames in code_anims.items():
        for action in anims_frames:
            xml_path = f'{entity_type.title()}/{code}.img/{action}'
            xml_text = fetch_wz_xml(api_base, xml_path)
            origin_dict = parse_origins_from_xml(xml_text)
            for fn, _ in anims_frames[action]:
                if fn in origin_dict:
                    all_wz_origins.append(origin_dict[fn])

    if all_wz_origins:
        cross_max_ox = max(o[0] for o in all_wz_origins)
        cross_max_oy = max(o[1] for o in all_wz_origins)
        global_override = (cross_max_ox, cross_max_oy)
        print(f'  Cross-code global WZ origin: ({cross_max_ox}, {cross_max_oy})')
    else:
        global_override = None
        print(f'  No WZ origin data, falling back to ground anchor per animation')

    # Phase C: Align per code with the cross-code global origin
    merged_frames = {}
    merged_origins = {}
    for code in sorted(code_anims):
        anims_frames = code_anims[code]
        fixed, origins = align_by_wz_origin(anims_frames, code, api_base, pad=4,
                                            global_origin=global_override)
        for action in sorted(fixed):
            merged_name = f'{action}-{code}'
            merged_frames[merged_name] = fixed[action]
            if action in origins:
                merged_origins[merged_name] = origins[action]

    # ── 5. Generate strips & merged config ──
    print(f' [5/5] Generating sprite strips & merged config...')
    config = {
        'name': mob_name,
        'version': 2,
        'type': 'sprite',
        'sprites': {}
    }

    for action in sorted(merged_frames):
        out_path = os.path.join(out_dir, f'{action}.png')
        cw, ch, count = make_strip(merged_frames[action], out_path)
        if cw:
            entry = {
                'file': f'{action}.png',
                'frames': count,
                'frameWidth': cw,
                'frameHeight': ch
            }
            if action in merged_origins:
                entry['originX'], entry['originY'] = merged_origins[action]
            config['sprites'][action] = entry
            print(f'  -> {action}.png  {count}f  {cw}x{ch}')

    config_path = os.path.join(out_dir, 'pet_config.json')
    with open(config_path, 'w') as f:
        json.dump(config, f, indent=2, ensure_ascii=False)

    print(f'\nDone!  Merged {entity_type}: {",".join(codes)} ({mob_name})')
    print(f'  Output: {out_dir}/')
    print(f'  Config: {config_path}')


def _process_mob(code, api_base, out_dir, raw_dir, entity_type='mob'):
    """Process a single mob/npc: download frames, align, generate strips."""
    # ── 1. Get name ──
    print(f' [1/5] Fetching {entity_type} name for {code}...')
    try:
        name_data = api_post(api_base, '/api/wz/data/query/string',
                             {'type': entity_type, 'code': code})
        if isinstance(name_data, list) and name_data:
            mob_name = name_data[0].get('name', code)
        elif isinstance(name_data, dict):
            mob_name = name_data.get('name', code)
        else:
            mob_name = str(code)
    except Exception as e:
        print(f'  Warning: name fetch failed ({e}), using code as name')
        mob_name = str(code)
    print(f'  Name: {mob_name}')

    # ── 2. Get frame paths ──
    print(f' [2/5] Fetching frame paths...')
    groups = _fetch_code_groups(code, api_base, entity_type)
    if groups is None:
        return
    if not groups:
        print('  No valid action frames found.')
        return

    # ── 4. Download frames ──
    print(' [3/5] Downloading frames...')
    for action in sorted(groups):
        frames = groups[action]
        action_dir = os.path.join(raw_dir, action)
        os.makedirs(action_dir, exist_ok=True)

        downloaded = 0
        for fn, path in frames:
            dst = os.path.join(action_dir, f'{fn}.png')
            if not os.path.exists(dst):
                try:
                    img_data = api_get_image(api_base, path)
                    if img_data and len(img_data) > 100:
                        with open(dst, 'wb') as f:
                            f.write(img_data)
                    else:
                        print(f'    ! {action}/{fn}.png: empty response ({len(img_data)} bytes)')
                        continue
                except Exception as e:
                    print(f'    ! {action}/{fn}.png: {e}')
                    continue
            else:
                # Verify existing file is valid
                if os.path.getsize(dst) < 100:
                    os.remove(dst)
                    continue
            downloaded += 1

        print(f'  {action}: {downloaded}/{len(frames)} frames saved')

    # ── 5. Align & generate strips ──
    print(' [4/5] Aligning frames (ground anchor, pad=4)...')
    anims_frames = {}
    for action in sorted(groups):
        action_dir = os.path.join(raw_dir, action)
        if not os.path.isdir(action_dir):
            continue
        files = sorted(os.listdir(action_dir), key=lambda f: int(re.sub(r'\D', '', f)))
        imgs = []
        for f in files:
            if f.endswith('.png'):
                num = int(f.replace('.png', ''))
                img = Image.open(os.path.join(action_dir, f)).convert('RGBA')
                imgs.append((num, img))
        if imgs:
            anims_frames[action] = imgs

    fixed, origins = align_by_wz_origin(anims_frames, code, api_base, pad=4)

    print(' [5/5] Generating sprite strips & config...')
    config = {
        'name': mob_name,
        'version': 2,
        'type': 'sprite',
        'sprites': {}
    }

    for action in sorted(fixed):
        out_path = os.path.join(out_dir, f'{action}.png')
        cw, ch, count = make_strip(fixed[action], out_path)
        if cw:
            entry = {
                'file': f'{action}.png',
                'frames': count,
                'frameWidth': cw,
                'frameHeight': ch
            }
            # Include per-animation WZ origin for client anchoring
            if action in origins:
                entry['originX'], entry['originY'] = origins[action]
            config['sprites'][action] = entry
            print(f'  -> {action}.png  {count}f  {cw}x{ch}')

    config_path = os.path.join(out_dir, 'pet_config.json')
    with open(config_path, 'w') as f:
        json.dump(config, f, indent=2, ensure_ascii=False)

    print(f'\nDone!  {entity_type.title()}: {code} ({mob_name})')
    print(f'  Output: {out_dir}/')
    print(f'  Config: {config_path}')


def _extract_value(tree_data, fallback):
    """Try to extract a link value from a tree API response."""
    for key in ('value', 'data'):
        if isinstance(tree_data, dict) and key in tree_data:
            val = tree_data[key]
            if isinstance(val, str):
                return val
            if isinstance(val, dict) and 'value' in val:
                return val['value']
    if isinstance(tree_data, str):
        return tree_data
    return None


if __name__ == '__main__':
    main()
