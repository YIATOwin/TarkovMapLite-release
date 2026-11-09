# 代码地图 —— 没有 AI 之后怎么接着改

> 这份文件的目的：**任何一个人（包括半年后的你）打开工程，十分钟内知道
> "哪里管什么、改这个会牵连什么、坏了先看哪"。**
>
> 更细的规则见同目录 `DATA-RULES.md`（接口字段）和 `UI-RULES.md`（WinForms 的坑）。
>
> 文件路径都写成相对形式：开发树里它们在 `TarkovMapLite/` 下，
> 发布仓库里是扁平的（`.cs` 在根目录、本文件在 `docs/` 下）。

---

## 0. 三十秒看懂

一个原生 WinForms 窗口，中间一块画布画地图，右边一栏画任务；数据来自
**游戏日志**（任务状态、进了哪张图）、**游戏截图文件名**（玩家坐标）、
**tarkov.dev 的 JSON 接口**（任务、物品、撤离点）；地图图片编在 exe 里。
另外内置一个局域网 HTTP 服务，把上面这些同步给手机浏览器。

```
Program
  └─ MapForm（主窗口，唯一持有其它东西的地方）
       ├─ MapCanvas       画：底图/楼层/区域/撤离点/玩家/角标
       ├─ TaskLog         读游戏日志  → 任务状态 + "进了哪张图"
       ├─ PlayerLocator   读截图文件名 → 玩家坐标/朝向
       ├─ TarkovData      接口 + 缓存 → 任务/物品/撤离点/刷新点（世界坐标）
       ├─ MapPack + Assets 世界坐标 ↔ 图片像素；嵌入资源读取
       └─ LocalServer     局域网 HTTP :8787 → MobileState → Assets/mobile/app.js
```

**一句话记住坐标系**：内存里**永远存世界坐标 (x, z)**，只有画的那一刻才用
`MapPack.ToImage(map, x, z)` 换算成图片像素；手机端拿到的是电脑端算好的像素。

---

## 1. 启动时发生了什么（按顺序）

1. `Program.Main` → `Application.Run(new MapForm())`
2. `MapForm` 构造：`BuildLayout()` 搭界面 → 挂 `TaskLog` / `PlayerLocator` 事件
3. `Load` 事件 → `StartAsync()`：
   - `TarkovData.LoadCached()` 读本地缓存（没有就用内嵌 seed）
   - `_log.ReplayAll()`：把日志从头放一遍，得到所有任务状态 + 最后一次进图
   - `ApplyMap()`：按当前地图把区域/撤离点投影到画布
   - 缓存超过 24 小时 → `TarkovData.RefreshAsync()` 联网刷新
   - 起两个定时器：`_timer`（3 秒：读日志增量、读新截图）、`_mobileTimer`（250ms：手机端）
4. 之后所有变化都走事件 → `RefreshTaskList()` / `UpdatePlayerMarker()` / `canvas.Invalidate()`

**没有后台线程在改 UI**：网络和 HTTP 都在别的线程，但只把结果塞进
`ConcurrentQueue` 或不可变对象，UI 线程的定时器再去取。想加异步功能，也照这个套路。

---

## 2. 文件职责与"改它的风险"

| 文件 | 干什么 | 改它要小心什么 |
|---|---|---|
| `Program.cs` | 入口 | 加崩溃日志 / 单实例在这里挂钩子 |
| `MapForm.cs` | 主窗口、事件流、手机端宿主 | 顶栏按钮是**绝对坐标**；任务选中态以 `_taskList` 为准 |
| `MapCanvas.cs` | 所有绘制 + 缩放拖动 | `OnPaint` 里的**绘制顺序就是设计**；`SetMap` 才会重算投影 |
| `MapPack.cs` | 投影 | `transform/bounds/viewBox` 是标定数据，改错整图偏移 |
| `TarkovData.cs` | 数据层 | 加字段要改 4 处（见 §4），坐标必须留世界坐标 |
| `TaskLog.cs` | 解析日志 | 游戏更新改了措辞会静默失效（任务不动/不切图） |
| `PlayerLocator.cs` | 解析截图文件名 | 只认启动之后的新截图；文件名格式由游戏决定 |
| `MapMarkers.cs` | 容器/物资/出生点/危险区 | 数据是离线快照，要更新得重跑脚本再打包 |
| `ContainerKinds.cs` | 容器类型 → 名字/颜色/记号 | 加记号要**同时**改这里和 `MapCanvas.DrawContainer` |
| `ItemImages.cs` | 物品缩略图下载缓存 | 异步设计，别改成同步（会卡界面） |
| `MapLabels.cs` | 地名 | 缓存 key 必须是 `map.Key` 不是 `map.Data` |
| `Assets.cs` | 嵌入资源读取 | MSBuild 把 `-` 变 `_`，必须走 `Normalize()` |
| `LocalServer.cs` | 手机端 HTTP | 路由表在文件头；HTTP 线程**不许碰控件** |
| `MobileState.cs` | 手机端快照 JSON | 字段名就是协议，改要和 `app.js` 同步 |
| `MobileForm.cs` | 「手机端」面板 | 定位由 MapForm 手动算，`CenterParent` 会跑到屏幕外 |
| `Assets/mobile/*` | 手机页面 | 嵌在 exe 里，改完要重新打包（或用散文件后门） |

---

## 3. 四条数据链

### 3.1 任务 / 物品 / 撤离点（网络或缓存）
```
json.tarkov.dev ──(24h 过期才刷)──> %LOCALAPPDATA%\TarkovMapLite\data-v8.json
                                        │ LoadCached() 或 RefreshAsync()
                                        ▼
                   TarkovData.ZonesByMap / Extracts / Transits / TaskObjectives …
                                        │ 世界坐标
                                        ▼
                     MapForm.ApplyMap() 用当前地图投影一次
                                        ▼
                          MapCanvas.Extracts / _projected
```
- 第一次运行没网 → 用内嵌的 `Assets/data-seed.json` 兜底
- 缓存里 `schema` 和 `TarkovData.CurrentSchema` 不一致 → 强制重拉（文件名一直是 `data-v8.json`，别被它骗）

### 3.2 局内状态（游戏日志）
```
E:\Tarkov\Logs\*.log ──增量读取──> TaskLog
                                   ├─ Changed     → 刷新任务列表、重画区域
                                   └─ MapLoading  → MapForm.SelectMap() 自动切图
```

### 3.3 玩家位置（游戏截图文件名）
```
<截图目录>\2026-11-09[13-27]_250.0, 20.0, 60.0_0,0,0,1_21.87 (0).png
        │ PlayerLocator（前 3 个数是 x,y,z，后 4 个是朝向四元数）
        ▼
   Fix(x, z, forwardX, forwardZ)  ←── 配 "_raidMapKey"（最后一次进图的地图）
        │ MapPack.ToImage
        ▼
   MapCanvas.PlayerPosition/PlayerForward（图片像素）+ MobileState.player
```
地图对不上时不画玩家（否则会把位置标到别的图上）。

### 3.4 地图图片
```
TarkovMapLite.exe（EmbeddedResource） ──Assets.Open("maps/customs/base.jpg")──> Image
        ▲ 也有后门：exe 同级 Assets\ 目录优先
```
`MapPack.ImageFor(layer)` 返回**不带扩展名**的 stem，调用方按 `.jpg → .png` 依次试
（瓦片底图是 jpg，楼层图和 SVG 渲染图是 png）。

---

## 4. 想改一件事，要动哪里

| 目标 | 必须改 | 别忘了 |
|---|---|---|
| **加一张新地图** | `tools/` 里出图 → `Assets/maps/maps.json` 加条目（transform/bounds/viewBox/layers） | 打包脚本要能读到新目录；`MapPack.Maps` 会自动带上 |
| **换地图标定（图偏了）** | `maps.json` 的 `transform` / `bounds` / `viewBox` | 用 `tools/shrink-tiles.ps1` 重算，别手改数值 |
| **加一种容器类型** | `ContainerKinds.Table` 加一行 | 只需颜色 + 名字 + 已有的 `Icon` |
| **加一种新记号（图形）** | `ContainerKinds.Icon` 加枚举 **和** `MapCanvas.DrawContainer` 加分支 | 两处都改，否则画空白 |
| **加一个图层开关** | `MapCanvas.ShowXxx` + `MapForm.BuildLayerMenu` 加一项 + `MapCanvas.OnPaint` 里调用 | 图层默认关是产品决定（开图只看区域和撤离点） |
| **升级数据结构（接口加字段）** | `TarkovData.CurrentSchema` +1、`RefreshAsync` 解析、`ApplyCache` 回填、`Assets/data-seed.json` | 少改一处 → "在线正常、离线全空" |
| **顶栏加按钮** | `MapForm.BuildLayout` | 位置是绝对坐标，自己挪；窄窗口会被裁 |
| **手机端加字段** | `MobileState.Build()` + `Assets/mobile/app.js` 读取处 | 字段名就是协议，两端同时改，否则手机端读到 undefined |
| **手机端加动作** | `MobileAction` 常量 + `LocalServer` 路由不用改 + `MapForm.ApplyMobileAction` 加 case | 手机端 `sendAction(type, value)` |
| **改手机页面样式** | `Assets/mobile/app.css` | 改完要重新打包（或放 exe 同级 `Assets\mobile\` 后重启） |
| **升级版本号** | `TarkovMapLite.csproj` 的 `<Version>` | 发版还要在 release 仓库打同名 tag |

---

## 5. 出问题先看这里

| 现象 | 先看 | 多半是 |
|---|---|---|
| 进图不自动切图 | 底栏 `日志：` 有没有路径；`TaskLog.EventCount` 涨不涨 | 日志目录变了，或游戏改了措辞 |
| 任务状态不对/全是未开始 | 同上 | 同上 |
| 底栏一直"等待游戏内截图…" | 截图目录是否存在；有没有按过截图键 | 截图目录不是默认位置（可用 `TARKOVMAPLITE_SHOTS`） |
| 玩家点在别的图上 | 底栏写的"<地图> 的玩家" | 玩家切到别的图了，切回去就可见；属正常 |
| 区域整体偏移/跑到图外 | `maps.json` 的 `bounds`、`viewBox` | 标定被改坏，或缓存里存的是旧投影（清 `%LOCALAPPDATA%\TarkovMapLite\data-v8.json`） |
| 任务区域、撤离点在瓦片版错位 | 缓存 key 用了 `Data` 而不是 `Key` | 见 `MapPack.cs` 头部注释 |
| 地名/容器不显示 | 「图层」菜单是否勾上；`Assets/markers.json` 是否存在 | 默认全关，这是设计 |
| 物品缩略图空白 | 网络；`%LOCALAPPDATA%\TarkovMapLite\img` | 拿不到就画占位，不报错 |
| exe 体积暴涨 | csproj 是否又加了 `Content` / `CopyToOutputDirectory` | 单文件会把资源收两遍（296 MB 那次） |
| 打包报 MSB3027 | 有没有先杀进程 | exe 被占用 |
| 手机连不上 | 防火墙是否允许；手机和电脑是否同一 WiFi；面板上的地址 | 换过 exe 路径 → 防火墙会重新问一次 |
| 手机页面空白 | 浏览器控制台；`/api/state` 能不能打开 | 页面资源没嵌进 exe，或 JS 报错 |
| 手机上点位和电脑不一致 | 看 `/api/state` 里的 `player` / `zones` | 手机端不做投影，不一致只可能是状态没发过去 |

---

## 6. 构建 / 打包 / 发布

```powershell
# 1) 先杀进程（否则 exe 被占用，报 MSB3027）
Get-Process TarkovMapLite -EA SilentlyContinue | Stop-Process -Force

# 2) 发布单文件（自包含，玩家不用装 .NET 运行时）
dotnet publish TarkovMapLite.csproj -c Release -r win-x64 --self-contained true `
    -o publish -v q --nologo
```

- 当前 **125.8 MB**（地图位图 79 MB 占大头），私有内存约 **164 MB**
- 只改了 `.cs` 或页面 → 必须 `publish`，不能只 `build`（资源是嵌进 exe 的）
- 版本号在 `TarkovMapLite.csproj` 的 `<Version>`；发版时**同时**打同名 tag `v0.x.y`，
  GitHub Actions（`.github/workflows/build.yml`）会构建并把 exe 传到 Release
- 大文件 push 容易 `curl 55` 超时：可以逐个提交
  `git push origin <sha>:refs/heads/main`，再用 `git ls-remote` 确认

---

## 7. 别踩这些坑（都是踩过的）

1. **MSBuild 把资源名里的 `-` 换成 `_`**：`ground-zero` → `ground_zero`，查资源必须 `Normalize()`。
2. **WinForms 菜单**：`CheckOnClick` 会让菜单立刻关闭；父项有 `DropDownItems` 时不会触发 `Click`；
   程序改 `CheckState` 后要 `DropDown.Invalidate(true)`（详见 `UI-RULES.md`）。
3. **`ListBox.SelectedIndex` 在 `MouseDown` 之前就变了**：判断"点的是不是已选中的那一行"
   要用"上次点的行号"，不能拿 `SelectedIndex` 比。
4. **`RichTextBox.AppendText` 只追加**：换任务必须先 `Clear()`，否则目标会叠在一起。
5. **单文件 exe 别用 `Content` 收资源**：会和 `EmbeddedResource` 重复，体积翻倍。
6. **`HttpListener` 要管理员权限**：手机端服务用的是 `TcpListener`，别换回去。
7. **PowerShell 5.1 给原生 exe 传参会吞掉内嵌双引号**：`curl.exe -d '{"a":1}'` 实际丢了引号。
   测 POST 用 `Invoke-WebRequest -Body`。（这个坑害我查了半轮）
8. **PS 的 `-like '2026-11-09[13-2*'` 里 `[` 是通配符**：要么转义，要么用 `-match` 正则。
9. **`.ps1` 脚本必须纯 ASCII**：PS 5.1 把无 BOM 的 UTF-8 当 ANSI 读，中文注释会让后面的语句解析失败。
10. **非模态窗口别用 `StartPosition=CenterParent`**：实测会跑到屏幕外（x=-158），手算 + clamp。
11. **测玩家位置**：往截图目录写一张名字合法的 PNG，但必须**在应用启动之后**写
    （`PlayerLocator.NotBefore`）；测完记得删掉，别留在玩家自己的截图目录里。

---

## 8. 术语表

| 词 | 含义 |
|---|---|
| 世界坐标 | 游戏里的 (x, z)，单位米。**所有数据都存这个** |
| 图片像素 | 地图图片上的像素坐标，和 `viewBox` 同尺度（不是图片真实分辨率） |
| `viewBox` | `maps.json` 里的一张图"逻辑尺寸"，绘图和手机端都用它 |
| `map.Key` | 这张图自己（决定投影、图片目录） |
| `map.Data` | 任务/撤离点数据借谁的（瓦片版海关借 `customs`） |
| `map.ImageKey` | 位图借谁的（夜间工厂借 `factory`） |
| `svgLayer` | 楼层标识（`Second_Floor` / `Underground_Level`…），空串＝地面 |
| stem | 不带扩展名的图片路径片段，如 `customs/base` |
| 转移点 / 撤离点 | `Kind = "transit"` / `"extract"` |
| 任务区域 | 一个任务在地图上的一块多边形（`ZoneShape`），一个任务可能有多块 |
