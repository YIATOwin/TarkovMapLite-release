# 地图楼层与菜单修复（0.4.4）

SVG 与瓦片版现在都提供地面、2—5 层、地堡。上游 `Reserve.svg` 仅有
`Ground_Level` 和 `Bunkers`；新增的四张楼层 PNG 来自仓库内现有瓦片楼层，
按两张地图的世界坐标投影重采样到 SVG 底图的 4000×3680 像素坐标系。
这四层使用瓦片画风，地面和地堡保留原有 SVG 画风。

## 修复内容

- 附加层只有一张的地图也会正常绘制选中层。
- 楼层号从稳定的 `svgLayer` ID 读取，中文“地堡”不再被认成地面。
- 版本切换保留当前楼层，显式选 SVG 不再被 `preferTiles` 重定向。
- 楼层按钮绘制与点击采用相同的 84×26 矩形及 30 像素间距。
- 储备站物资、出生点、玩家与有高度数据的任务区域，共用按建筑范围和高度划分的规则。
  工厂、海关、零点、海岸线、立交桥、街区和实验室也导入相应上游规则，
  共用图片的夜间/感染等变体继承相同的物理楼层；无上游规则的地图保留原有判断方式。
- 手机端切层时与桌面相同，将地面底图透明度降到 5%。

## 数据来源与重建

规则来自 https://github.com/the-hideout/tarkov-dev/blob/main/src/data/maps.json ，
使用其中 `reserve` 的 `layers[].extents[].height` 和 `bounds`。
范围矩形允许两个角点顺序相反；高度下界包含，上界不包含。
已配置规则的地图中，未匹配任何附加楼层的点归入地面。

2026-11-12 的上游 SVG 已核对，与仓库内 SVG 文本一致：
https://github.com/the-hideout/tarkov-dev-svg-maps/blob/main/Reserve.svg 。

重新渲染 SVG 或裁剪瓦片后，使用 Pillow 运行：

```powershell
python tools/sync-map-layers.py path/to/tarkov-dev-maps.json
```

该脚本只读取下载好的配置和现有地图图片，不请求网络，不修改投影标定参数。
会生成 `Assets/floor-extents.json`、缺失的补充楼层图，并同步地图楼层列表和素材索引。
海关四层由瓦片补入 SVG；海岸线和立交桥楼层由 SVG 补入瓦片。
`build-reserve-floors.py` 保留为兼容入口，调用同一个同步流程。
不要用全地图单一高度阈值替换建筑规则：兵营三层高度为 -0.64—2.23，
雷达站二层高度却为 22.1—25.7。

## 回归检查

需要 Windows 和 .NET 10 SDK：

```powershell
dotnet run --project tests/FloorRegression/FloorRegression.csproj -c Release -- path/to/render-output
dotnet publish -c Release -r win-x64 --self-contained true
```

检查覆盖全部 25 个地图条目、88 个原生楼层画面，包含建筑范围/高度边界、版本选择、点位分类一致性、
单附加层绘制、按钮右缘与间隙点击，以及无效楼层输入。
绘图验证调用实际 `MapCanvas`，并比较排除按钮和状态栏后的地图画面。
游戏内实地高度与位置仍需在实际游玩时核对，上游数据只覆盖其标注的建筑范围。

## 全图排查与菜单

First_Floor 在海关、零点、海岸线、街区和航站楼是随 Ground_Level 显示的附属分组，
不应作为独立按钮。立交桥的 First_Floor 则代表真实二层，因此楼层号显式保存在
`MapLayer.Level`，不再仅凭素材 ID 猜测。实验室 Technical 对应地下技术层。

`rasterize-maps.ps1` 已改为读取本仓库的 tools/maps-src，并按根分组及
data-keep-with-group 控制可见性。重新渲染后再运行上述同步命令。

MenuButtonBinding 统一处理重复点击、外部点击、Escape 与窗口失焦关闭。
地图菜单保留项目，只在文字或选中项变化时更新；图层 CheckStateChanged 只使变化行失效，
所有分类批量修改在下一轮界面消息中合并成一次按钮数量/画布刷新。
