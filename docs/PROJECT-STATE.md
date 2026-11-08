# TarkovMapLite — 项目交接记忆

> 给以后的我：开工前先读这份，别再从零摸索。
> 最后更新：2026-11-08 18:45（会话 63-87）

## 一、这是什么

给《逃离塔科夫》用的**原生 Windows 小地图**（WinForms + GDI+，不用浏览器内核）。
读游戏日志自动切图、画任务区域、物品点、撤离点、玩家位置。

- 背景：社区版 TarkovMonitor 用 BlazorWebView，一个进程 200 MB；玩家只有 16 GB 内存，所以另起原生实现

## 二、路径

| 用途 | 路径 |
| --- | --- |
| 主工程（开发） | `C:\Users\DAYISUKIMIKU\Desktop\TarkovMonitor-master\TarkovMapLite\` |
| 工具脚本 | `...\TarkovMonitor-master\tools\` |
| 地图源 SVG | `...\TarkovMonitor-master\TarkovMonitor\wwwroot\maps\` |
| 发布仓库 | `C:\Users\DAYISUKIMIKU\Desktop\TarkovMapLite-release\`（git，已推 GitHub） |
| 线上仓库 | https://github.com/YIATOwin/TarkovMapLite-release |
| 桌面 exe | `C:\Users\DAYISUKIMIKU\Desktop\TarkovMapLite.exe` |
| 数据缓存 | `%LOCALAPPDATA%\TarkovMapLite\data-v8.json` |
| 物品图缓存 | `%LOCALAPPDATA%\TarkovMapLite\img\` |
| 游戏日志 | `E:\Tarkov\Logs` |
| 截图目录 | `D:\Users\DAYISUKIMIKU\Documents\Escape From Tarkov\Screenshots` |

**SDK**：`D:\dotnet\dotnet.exe`（10.0.403）。打包前必须先杀 `TarkovMapLite` 进程，否则文件锁报 MSB3027。

## 三、构建 / 打包

```powershell
Get-Process TarkovMapLite -EA SilentlyContinue | Stop-Process -Force
& 'D:\dotnet\dotnet.exe' publish 'C:\...\TarkovMapLite\TarkovMapLite.csproj' -c Release -r win-x64 --self-contained true -o 'C:\...\TarkovMapLite\publish' -v q --nologo
Copy-Item '...\publish\TarkovMapLite.exe' "$env:USERPROFILE\Desktop\TarkovMapLite.exe" -Force
```

单文件 exe，当前 **103.3 MB**（地图位图占大头：迷宫 12 MB）。

## 四、代码地图（TarkovMapLite/）

| 文件 | 职责 |
| --- | --- |
| `Program.cs` | 入口 |
| `MapForm.cs` | 主窗口：顶栏（地图下拉带进行中任务数 / 撤离点 / 图层 / 刷新 / 复位）、右栏（筛选 chip、任务列表、目标、所需物品）、状态栏 |
| `MapCanvas.cs` | 画布：地面+楼层合成、区域多边形、编号点位+连线、静态标记、撤离点、玩家箭头、缩放平移、楼层按钮 |
| `MapPack.cs` | 读 `Assets/maps/maps.json`，世界 (x,z) ↔ 图片像素；支持 `imageKey` 共用位图 |
| `MapMarkers.cs` | 读 `Assets/markers.json`：容器/散落物资/危险区/出生点/固定武器 |
| `ContainerKinds.cs` | 30 种容器类型 → 中文名 + 颜色 + 形状（0 方 1 圆 2 菱 3 十字 4 三角 5 空心圈） |
| `TarkovData.cs` | 拉 json.tarkov.dev、缓存、翻译、投影、任务/物品/撤离点数据 |
| `TaskLog.cs` | 解析 `Logs\*.log`，任务状态、进图场景名 → 地图（走 `SceneToMap`） |
| `PlayerLocator.cs` | 截图文件名里的坐标+四元数 → 玩家位置/朝向 |
| `ItemImages.cs` | 物品缩略图下载 + 磁盘缓存（`<id>-base-image.png` 是真正的 PNG） |
| `Assets.cs` | 嵌入资源读取（MSBuild 把 `-` 变 `_`，要按规范化名查） |

## 五、资源与生成脚本（tools/）

| 脚本 | 作用 |
| --- | --- |
| `rasterize-maps.ps1` | 把 SVG 渲染成每层 PNG（headless Edge，长边 4000px） |
| `build-markers.ps1` | 从 `/pve/maps` 导出 `Assets/markers.json`（容器/物资/危险区/出生点） |
| `stitch-tiles.ps1` | **瓦片拼接**：下载 tarkov.dev 的 XYZ 瓦片并拼成整图 |

**所有 PS 脚本必须纯 ASCII**：PS 5.1 把无 BOM 的 UTF-8 当 ANSI 读，中文注释会让后续语句解析失败。

## 六、当前支持的地图（15 张）

| key | 显示名 | 位图来源 |
| --- | --- | --- |
| customs / factory / woods / lighthouse / shoreline / reserve / interchange / streets-of-tarkov / ground-zero / terminal | 各自 | SVG 渲染 4000px |
| night-factory / infected-factory | Night Factory / Infected Factory | 复用 factory 位图（`imageKey`） |
| ground-zero-21 / ground-zero-tutorial | Ground Zero 21+ / (Tutorial) | 复用 ground-zero |
| **the-labyrinth** | The Labyrinth | **瓦片拼接 4096×3840（147 块 z=4）** |
| **the-lab / the-lab-dark / infected-lab** | The Lab / (Dark) / Infected | **瓦片拼接 2800×2100（1st 111 块 / 2nd 70 块 / Technical 66 块）** |

**还缺 5 张**（数据都在接口里，只差图）：
- `the-lab` / `the-lab-dark` / `infected-lab`：同一套瓦片，3 层（1st / 2nd / Technical）
- `icebreaker`：7 层瓦片（`maps/icebreaker/06_infirmary…`，tileSize 256，transform [2.0,125.0,3.5,91.0]，rotation 180）

## 七、接口与数据（详见 docs/DATA-RULES.md）

| 端点 | 说明 |
| --- | --- |
| `/pve/tasks` | 499 条；`wikiLink` / `normalizedName`（slug）/ `taskImageLink`；目标有 `type/items/zones/count` |
| `/pve/items` | 16.5 MB，**不要整体解析**（会 300 MB）；流式正则抽 id+name |
| `/pve/maps` | 19 张图 + 标记；`data.lootContainers` 是 id→类型字典 |
| `/pve/maps_zh` 等 | 翻译表 |

**tarkov.dev 的栅格地图 = XYZ 瓦片**，配置（transform/rotation/bounds/tilePath）全在 `https://tarkov.dev/static/js/index.xxx.js` 里：
- 实验室：`assets.tarkov.dev/maps/labs_v4/{1st,2nd,technical}/{z}/{x}/{y}.png`，tileSize 175，zoom 2–6，transform `[0.575,281.2,0.575,193.7]`，rotation 270，bounds `[[-80,-477],[-287,-193]]`
- 迷宫：`maps/labyrinth/main/...`，transform `[2.115,85.5,2.115,128]`，rotation 270
- 破冰船：`maps/icebreaker/06_infirmary/...` 等 7 层，tileSize 256，transform `[2.0,125.0,3.5,91.0]`，rotation 180

**瓦片拼接标定公式**：`图片像素 = transform(world) × 2^zoom`。
迷宫据此把 bounds 反推为 `[[-60.52009,-40.42553],[52.95508,80.61466]]`，用用户给的 3 个坐标读数验证（误差 0）：
`(-51.47,-36.22)→(142,306)`、`(-2.47,21.21)→(2086,1964)`、`(51.10,77.73)→(3998,3777)`。

## 八、交互规则（详见 docs/UI-RULES.md）

1. 菜单项不要用 `CheckOnClick`（会关菜单）；自己切 `CheckState` + 取消 `ItemClicked` 关闭原因
2. 带子菜单的父项，点文字只展开、不触发 `Click` → 用「全部显示/全部隐藏」命令
3. 程序改 `CheckState` 后必须 `DropDown.Invalidate(true)`，否则要悬停才刷新
4. `MouseDown` 之前 `SelectedIndex` 已改，取消选中要记「上一次点的行」
5. 别用 `WS_VSCROLL` 去滚动条（滚轮会废），用父容器裁 18px
6. 视野裁剪必须从屏幕 `(0,0)` 起算，不能锚 `origin`

## 九、已修的坑（别再犯）

| 现象 | 根因 |
| --- | --- |
| 街区判定成海关 | 场景名 `city_preset` 匹不上 key，改用 `/pve/maps` 的 `scenePath` 表 |
| 放大后标记全消失 | 裁剪矩形锚在 `origin`（缩放后为负） |
| 打包后部分地图空白 | MSBuild 把资源名 `-` 变 `_`，按规范化名查清单 |
| 任务区域整体偏移 | 4 个 SVG 自带 root `width/height`，重复属性被忽略 → 按 1× 渲染 |
| 打包后地板图糊 | 小地图 viewBox 才 131×141，固定 2× 导出后被放大 4 倍 → 统一长边 4000px |
| 菜单点一下就没 | `CheckOnClick`；父项带子菜单不触发 Click |
| 物品图标不显示/文字挤 | 行高 22→30，图标 24px |
| 缓存缺新字段但不刷新 | 加 `schema` 版本号，低于当前强制刷新 |

## 十、待办 / 下一步

1. **实验室 3 层**（最常用）：用 `stitch-tiles.ps1` 按上面的 tilePath/transform 拼 3 张，写进 maps.json（3 个 layer），标定同样用 `×2^zoom`
2. **破冰船 7 层**：同上，体积会 +15~20 MB
3. 楼层号仍是推算的（区域高度中点 ÷ 3 米），个别任务会错层；彻底解决要人工校正表
4. 发布库积压 10 个提交未推；发版 `git push` + `git tag v0.1.1`（Actions 自动出 exe）

## 十一、用户偏好

- 中文交流；喜欢机制层面解释，不接受"大概能行"
- 会自己验证（截图/实测），报告要对得上他的观察
- **数据相关功能：先探接口、把字段和样例给他看，再动手**
- 界面问题常是「控件默认行为反直觉」，先找默认行为，别加代码
- 反复强调：内存占用要低，体积可以大
## 十二、作息

- 平时 **12:00 睡、07:00 起、09:00 上班**（北京时间）
- 晚上是主要协作时段；白天工作时间的改动他可能没法立刻验证
- 汇报时可以直接给「现在能验 / 需要你确认」两类，省得他找时间
## 十三、瓦片版地图清单（2026-11-08 从站点 JS 提取）

站点对多数地图同时有 SVG 和 **瓦片**两套素材，瓦片是 0.16 的新美术。瓦片路径前缀：

| 地图 | 瓦片目录 | 层数 |
| --- | --- | --- |
| customs | `maps/customs_0.16/{main,2nd,3rd,4th,underground}` | 5 |
| factory | `maps/factory/{main,2nd,3rd,tunnels}` | 4 |
| ground-zero | `maps/groundzero/{main_summer,2nd,3rd,garage}` | 4 |
| interchange | `maps/interchange/main` | 1 |
| reserve | `maps/reserve/{main,2nd,3rd,4th,5th,bunkers}` | 6 |
| shoreline | `maps/shoreline/main_summer` | 1 |
| woods | `maps/woods/main_0.16` | 1 |
| labyrinth | `maps/labyrinth/main` | 1（已做） |
| the-lab | `maps/labs_v4/{1st,2nd,technical}` | 3（已做） |
| icebreaker | `maps/icebreaker/00_control_room … 15_bridge_roof` | **16**（未做） |
| streets / lighthouse / terminal | 无瓦片，只有 SVG | — |

**已计划功能**：给同时有两种素材的地图加「SVG / 瓦片」切换按钮（瓦片更新、层数更多）。
每张图的 transform 都能从站点 JS 里按 `"normalizedName"` 找到，标定公式同 `像素 = transform × 2^zoom`。
## 十四、海关瓦片版进度（2026-11-08 19:10）

- 已拼 5 层到 `Assets/maps/customs-tiles/`：base(160 块,15.3MB) / Second_Floor(25) / Third_Floor(9) / Fourth_Floor(2) / Underground(8)，4096×2816
- maps.json 新增条目 `customs-tiles`（显示名「海关（瓦片）」），用 `dataKey: "customs"` 复用海关的任务与标记数据
- MapPack/MapMarkers/MapForm 已支持 `dataKey`（任务、标记、撤离点按 DataKey 取）
- **坐标还没标定好**：区域落在图左上角，说明 bounds 的换算仍有偏差
  - 参数：tileSize 256、zoom 4、transform [0.239,168.65,0.239,136.35]、rotation 180、瓦片范围 x 0..15 y 3..13
  - 下一步：拿海关配置里的 label 世界坐标（和实验室一样的方法）反推 bounds
- 待办：其余老地图（factory 4 层、reserve 6 层、groundzero 4 层、woods/shoreline/interchange 1 层）照同样流程做；破冰船 16 层最后做
- 另需修：状态栏「N 个区域」还是按 map.Key 统计，应改用 DataKey
### 海关瓦片标定现状（19:15）

- **地标验证通过**：用 customs 配置里的 label（BigRed/Dorms/Fortress/Crackhouse/OldGas/Storage）在拼接图上画十字，六个全部对准
  - 公式：px = -0.239x + 168.65；py = 0.239z + 136.35；u = px*16；v = (py-48)*16（48 = 3*256/16，因为瓦片 y 从 3 开始）
  - 对应 bounds [[705.65,-369.67],[-365.48,366.74]]、viewBox 0,0,4096,2816
- **但程序里区域仍画在图左上角**（超出图片上边缘），说明 app 侧的换算和上面不一致
- 下一步排查：
  1. 打印三个进行中任务 zone 的世界坐标，手算期望像素，和实际截图对照
  2. 检查 MapProjection 反序列化（bounds 是否按 [x,z] 对解析）、MapCanvas 的 origin/scale 计算
  3. 怀疑点：ToImage 用 bounds 矩形归一化，而图片有透明边距（art 实际在 y 250..2210），归一化基准可能和视觉基准不同

### 海关瓦片偏移的真正原因（已定位，19:17）

**不是标定问题，是数据结构问题。**

`TarkovData.cs:451` 在**加载时**就把区域多边形投影成了**那张图自己的像素坐标**：

```csharp
var map = MapPack.Find(mapKey);          // 用的是 customs（SVG 版）
var (u, v) = MapPack.ToImage(map, x, z);
points.Add(new PointF((u, v)));
var shape = new ZoneShape { Points = points.ToArray(), Center = ..., Bounds = ... };
```

于是：
- SVG 海关 → 区域按 SVG 图的像素存
- 海关（瓦片）→ 拿到的是**同一批 SVG 像素坐标**，投影完全不同 → 区域画在图外

实验室/迷宫没这问题，因为它们是独立 key，区域本来就按自己的图投影。

**修法（已做完，19:30）**：
1. `ZoneShape` 增加 `WorldPoints`（世界 x,z）+ `Project(map)`；`Points/Center/Bounds` 改由投影填
2. `TarkovData.ParseRecords` 只存世界坐标
3. `MapCanvas`：`Zones` 改成属性，setter 里 `ProjectZones()`；`SetMap` 也重算；绘制全部走 `_projected`
4. 状态栏「N 个区域」、下拉框「· N 个进行中任务」都改用 `map.Data`

### 同一类 bug 的另外三处（一起修了）

凡是"加载时投影成像素、按 DataKey 共享"的东西都有这个病：

| 位置 | 症状 | 修法 |
|---|---|---|
| `TarkovData.SpawnPoints` | 散落物资点在瓦片图上错位 | `PackSpawns` 只存世界坐标，`MapForm.UpdateSpawnPoints` 按当前图投影 |
| `TarkovData.ProjectMarkers`（撤离/转移点） | `Screen` 被两张图互相覆盖 | 改成 `MapMarker.Project(map)`，`ApplyMap` 里生成副本 |
| `MapMarkers.For` 的缓存 key | 用 `map.Data` 当 key，海关和瓦片互相污染（谁先打开谁对） | 缓存 key 改成 `map.Key` |

**通用规则**：`map.Key` 用来区分"同一份数据的两种画法"（像素缓存、图片）；
`map.Data` 只用来查"数据内容"（区域、任务、撤离点、标记的原始世界坐标）。
凡是缓存投影结果的地方，key 一律用 `map.Key`。

### 验证结果（19:30，两边逐点对照）

- 海关（瓦片）：116 个区域、3 个任务，区域多边形落在大建筑上
- 撤离点 16 个名字（ZB-1011 / ZB-1013 / RUAF 路障 / 十字路口 / 拖车停车场 / 老加油站路障…）
  在 SVG 版与瓦片版**相对位置一致**（同一套地理，只是两张图 fit 比例略有差别）
- 「这是什么梗？」「收视灵药」「掠地攻城」三组区域标记、虚线连线、序号在两张图上位置吻合
- 容器/散落物资点也不再错位