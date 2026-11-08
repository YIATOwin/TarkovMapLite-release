# 数据获取规则

这份文件记录一条工作规则，避免再做「猜接口」的活。

## 规则：先看接口给什么，再提需求

凡是涉及**从接口取数据**的功能（任务、物品、地图标记、图标、翻译……），按这个顺序走：

1. **先探接口**：把相关端点的实际返回拿出来看——顶层结构、每个对象的字段、几条真实样例、数据量。
2. **给用户看**：把字段清单和样例整理成表发给用户（谁能提供更准确的要求）。
3. **再实现**：用户确认哪些字段值得用、怎么展示之后，才动手写解析和界面。

不要跳过第 2 步直接实现，也不要靠猜字段名。

## 探接口的现成做法

```powershell
# 1) 下载到临时目录（大文件只下一次）
curl.exe -s --max-time 180 -o $env:TEMP\jd_maps_full.json 'https://json.tarkov.dev/pve/maps'

# 2) 看顶层结构
Get-Content $env:TEMP\jd_maps_full.json -TotalCount 1 |
  Select-Object -First 400 | ForEach-Object { $_.Substring(0, 400) }

# 3) 转成对象后看某个对象的全部字段
$j = Get-Content $env:TEMP\jd_maps_full.json -Raw -Encoding UTF8 | ConvertFrom-Json
$j.data.maps.PSObject.Properties[0].Value.PSObject.Properties |
  ForEach-Object { "{0} = {1}" -f $_.Name, $_.Value }
```

注意：`/pve/maps` 这类大文件（8.6 MB）用 `ConvertFrom-Json` 前先确认内存没问题；`items`（16.5 MB）
不要整体解析，程序里是流式正则抽取的。

## 已经探明的东西（截至 2026-11-08）

| 端点 | 内容 | 备注 |
| --- | --- | --- |
| `/pve/tasks` | 499 条任务 | 任务级有 `wikiLink`、`normalizedName`、`taskImageLink`；目标级有 `type/items/zones/count` |
| `/pve/items` | 16.5 MB 物品表 | `iconLink` / `baseImageLink` 等，**另有 `<id>-base-image.png` 是 PNG** |
| `/pve/maps` | 10 张图 + 标记 | `lootLoose` / `lootContainers`（只给 id）/ `hazards` / `spawns` / `stationaryWeapons`；`data.lootContainers` 是 id → 类型的字典 |
| `/pve/maps_zh`、`/pve/items_zh`、`/pve/tasks_zh` | 翻译表 | 键是英文原文或占位名 |

## 已知的缺口（接口没有的东西）

- 任务区域的**地点照片**：接口没有，只有 wiki（fandom，国内不可达）
- 容器类型的**专属图标**：`assets.tarkov.dev` 上没有，是 tarkov.dev 站点自己的 SVG 组件
- 楼层高度区间：没有，只能按区域高度推算
