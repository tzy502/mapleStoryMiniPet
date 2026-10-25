# sdlMS 人物移动/跳跃逻辑（外部参考摘录）

> 来源：<https://github.com/PShocker/sdlMS> ｜ commit `8962a01eaa24daeb7f6919fb93b474d5cde17d8a`（2026-09-13）
> 本文是**外部项目实现摘录**，不是本仓库代码；供 F18「台阶 jump 联动 / 人物贴地」参考。
> 关键文件：
> - `src/common/physic/physic.h` `.cpp`（44 + 330 行）— 行走、下落、碰撞
> - `src/client/system/logic/character_logic_system.h` `.cpp`（161 + 1688 行）— 状态机 + 跳跃
> - `src/client/game/game_foothold.h`（27 行）— 地面段数据结构

---

## 1. 常量（character_logic_system.h）

| 常量 | 值 | 含义 |
|---|---|---|
| `self_hspeed_min` / `max` | −125 / +125 | 水平速度上下限（px/s） |
| `self_vspeed_min` | −5550 | 垂直速度下限（上升最快，负=向上） |
| `self_vspeed_max` | +670 | 垂直速度上限（下落终速） |
| `hforce`（左右输入时） | ±1400 | 水平加速度（px/s²） |
| `friction` | 800 | 地面摩擦（`physic::walk` 实参） |
| 重力 | 2000 | `run_fall`: `vspeed += dt * 2000` |
| **跳跃初速** | `vspeed_min * 0.1` = **−555** | 起跳（stand/alert/walk） |
| 下跳初速 | −100 | 趴下穿平台（prone + jump） |
| 梯子弹跳 | vspeed −310 / hspeed ±140 | 从梯子跳离 |
| 二段跳 CD | 200ms | `self_two_jump_cooldown` |
| 下跳落地保护 CD | 120ms | `self_foothold_cooldown` |
| 梯子脱离 CD | 120ms | `self_ladderrope_cooldown` |

## 2. foothold 数据结构（game_foothold.h）

```cpp
uint32_t id, page, zmass;
int32_t  prev, next;                 // 相邻段 id（0 = 无）——沿地面链行走
int32_t  x1, y1, x2, y2;             // 段两端点
int32_t  l, r, t, b;                 // 包围盒
bool     forbidFallDown = false;     // 禁止下跳
std::optional<float> k, intercept;   // 直线 y = k*x + intercept；k 不存在 = 墙/天花板
```

**核心语义**：`k` 有值 → 可站立的斜面/平面；`k` 无值 → 墙或天花板（由 `y1` 与 `y2` 的大小关系定朝向）。

## 3. 状态机

`action_enum`：`stand / alert / walk / prone / jump / climb / sit / attack / skill / dead`
主循环 `run()` 按当前状态分派到 `run_walk / run_fall / run_jump / run_prone / run_climb / ...`。

## 4. 行走 `run_walk`（cpp 370-414）→ `physic::walk`（physic.cpp 57-114）

1. 有左右输入：`hforce = ±1400` 并播 walk 动画；无输入：`hforce = 0`（`hspeed == 0` 时直接 return，走 stand/alert）。
2. `physic::walk(pos, dt, hspeed, vspeed, hforce, −125, +125, 800, fall=true, fh, border, fhs)`：
   - **摩擦**：向右走时 `hforce -= friction`，并 `clamp` 到 `−hspeed/dt`（保证摩擦只减速、不反向加速）；向左对称。
   - `hspeed += dt * hforce` → `clamp(hspeed, −125, +125)` → `pos.x += hspeed * dt` → clamp 到地图边界。
   - **沿 foothold 链切段**：`pos.x` 走出当前段 `[l, r]` 时，用 `prev`（往左）/`next`（往右）切到相邻段（`walk_fh`）；
     - 切不过去且 `fall == false`（禁止掉落）→ 贴边定住：`pos.x = l + 0.1`（向左）或 `r − 0.1`（向右），`hspeed = 0`；
     - `fall == true` 且无相邻段 → `current_fh = 0`，进入下落态。
   - 收尾：`pos.y = k * pos.x + intercept`（人物始终贴在该段直线上）。

## 5. 下落 `run_fall`（cpp 416-448）→ `physic::fall`（physic.cpp 198-329）

- **重力**：`vspeed += dt * 2000`；当 `self_vspeed <= 0 && vspeed > 0` 时记录跳跃最高点 `self_fall_min`。
- 空中水平微调：`hspeed ±= 0.1 * dt`（几乎不能空中变向）。
- `physic::fall`：
  1. `vspeed = clamp(vspeed, −5550, +670)`；
  2. 新位置 = 旧位置 + `(hspeed, vspeed) * dt`；
  3. **扫掠碰撞**：用「旧位置 → 新位置」线段与**所有** foothold 线段求交（`fall_intersect` 跨立实验 + `fall_intersect_pos` 按交点 y 排序）——不是逐帧 AABB，高速下落不会穿地；
  4. **下落（vspeed ≥ 0）**：按交点 y 从小到大遍历（只取 `x2 >= x1` 的立足点）：
     - 该段有 `k` → **落地**：`current_fh = 段id`、`hspeed /= 2`、`vspeed = 0`、`page = 段.page`、`pos = 交点`；
     - 无 `k` → **撞墙**（`fall_collide_wall`：沿 `prev`/`next` 找有 `k` 的段确认是真墙）→ `pos.x = 段.x1 ± 0.1`、`hspeed = 0`、`pos.y` clamp 到墙的 y 范围；
  5. **上升（vspeed < 0）**：按交点 y 从大到小遍历：
     - 有 `k` 且 `is_on_slope`（在 `fall_fh` 及其前后邻段内 + 速度朝向坡面）→ 斜坡落地；
     - 无 `k` 且 `zmass == 0` → **天花板**：`vspeed = 0`、`pos.y = 交点.y`。

## 6. 跳跃 `run_jump`（cpp 467-550）

按 `jump` 键时看当前状态分支：

| 当前状态 | 行为 |
|---|---|
| stand / alert / walk | `vspeed = −555`（起跳） |
| prone（趴） | **下跳**：当前段 `forbidFallDown == false` 时，向下找 `pos.y+10 ~ pos.y+600` 内的 foothold → `vspeed = −100`、`hspeed = 0`、`self_fh = 0`、落地保护 CD 120ms |
| jump（空中） | **二段跳**（职业技能 `load_two_jump()`），CD 200ms |
| climb（梯子） | 按左右：`hspeed = ±140`、`vspeed = −310`、脱离梯子 `self_lr = 0`，CD 120ms |
| 其它 | 无 |

起跳统一收尾：`two_jump_cooldown = now + 200`、`self_fall_fh = self_fh`、**`self_fh = 0`（离地进入下落态）**、`run_action("jump")`、播 `Game.img/Jump` 音效。

## 7. 对 MiniPet（F18 无限之路）可借鉴的点

1. **贴地 = 沿 foothold 链走 + 段内直线方程**：x 越界用 `prev/next` 切段，y 由 `k*x + intercept` 得出 —— 天然支持平路/斜坡/台阶，且"禁止掉落"时贴边定住，不会掉出地图。
2. **跳跃三参数成套**：起跳 **−555**、重力 **2000**、落速上限 **+670**（起跳初速恰为 `vspeed_min * 0.1`，即官方手感的比例）。
3. **惯性手感 = 两条规则**：空中变向只有 `0.1 * dt`（基本不能变向）+ 落地水平速度 `hspeed /= 2`。
4. **扫掠线段求交**（旧位置→新位置 vs 所有 foothold）替代逐帧点检测，避免高速穿地；下落取最小 y、上升取最大 y。
5. **下跳要两个配套**：`forbidFallDown` 标记 + 落地保护 CD 120ms（否则穿下去立刻被判定落回原段）。
6. **斜坡落地判定**依赖 `k` 存在 + 速度方向（上坡/下坡）+ 是否落在 `fall_fh` 的前后邻段内。

## 8. 与 MiniPet 现有实现的差异（F18 落地前需补）

- MiniPet 的 `MapFoothold` 只有 `Id / Layer / X1 / Y1 / X2 / Y2 / Prev / Next`：
  **没有** `k / intercept / l / r / t / b / page / forbidFallDown / zmass`；
  落地目前用 `MapService.GetGroundY(map, x)`（取覆盖该 x 的**最高**段，Layer 0 优先）。
- MiniPet 人物是**贴地跟随**，没有速度/重力状态量。F18 需求 §10「台阶 jump 联动（高差 >40px 强制 jump）」需要新增：
  `vspeed`（垂直速度）、重力常量、离地/落地状态 —— 可直接照抄本文件的 `−555 / 2000 / +670` 三个数。
- 若无限之路要支持斜坡（目前**斜坡方案已废弃**、纯平路），则只需 `k = 0` 的水平段 + `l/r` 包围盒 + `prev/next` 链，实现量最小。
