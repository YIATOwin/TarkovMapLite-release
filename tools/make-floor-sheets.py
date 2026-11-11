"""Generate the "floors to annotate" workbook.

Points whose storey cannot be derived (doors, switches, extracts, transits,
containers) get one row each, with a blank 层级 column for the user to fill in.
A stable 编号 (`<map>/<kind>/<n>`) is written next to each row so the answers can
be turned back into data later.

Everything is translated to what the UI shows: Chinese layer names, Chinese
container kinds (read out of ContainerKinds.cs so the two stay in sync), and the
same short switch labels the map uses.

Usage: python make-floor-sheets.py <TarkovMapLite dir> <out.xlsx>
"""
import json
import re
import sys
from pathlib import Path

from openpyxl import Workbook
from openpyxl.styles import Alignment, Font, PatternFill
from openpyxl.utils import get_column_letter
from openpyxl.worksheet.datavalidation import DataValidation

LEVELS = ["地面", "2", "3", "4", "5", "地下", "地堡"]
HEAD_FILL = PatternFill("solid", fgColor="2F3A45")
HEAD_FONT = Font(bold=True, color="FFFFFF")
DIM_FONT = Font(color="7F8A96")

# maps.json 里的图层名是 SVG 原版的英文，界面显示的是翻译过的中文
LAYER_ZH = {
    "2nd Floor": "2 层",
    "3rd Floor": "3 层",
    "4th Floor": "4 层",
    "5th Floor": "5 层",
    "Second Level": "2 层",
    "Technical": "技术层",
    "Bunkers": "地堡",
    "Underground": "地下",
    "Garage": "车库",
    "Tunnels": "地下通道",
}


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def container_names(root):
    """从 ContainerKinds.cs 抠出 slug → 中文名，避免表格和界面两套叫法。"""
    text = (root / "ContainerKinds.cs").read_text(encoding="utf-8")
    return dict(re.findall(r'\["([^"]+)"\]\s*=\s*new\("([^"]+)"', text))


def switch_label(raw):
    """和 MapDoors.SwitchLabel 一个思路：给人看的是「拉杆 00632」这种。"""
    name = raw.replace("switch_", "")
    kind = "闸"
    for token, word in (
        ("lever", "拉杆"),
        ("button", "按钮"),
        ("console", "控制台"),
        ("valve", "阀门"),
        ("_switcher", "开关"),
    ):
        if token in name:
            kind = word
            break
    code = re.search(r"(\d{3,})", name)
    return f"{kind} {code.group(1)}" if code else kind


def door_display(key_name):
    """和界面一致：门名去掉钥匙/钥匙卡这类后缀。"""
    if not key_name:
        return ""
    for suffix in (" 钥匙卡", "钥匙卡", " 钥匙", "钥匙", "keycard", "key card", "key", "Key"):
        if key_name.lower().endswith(suffix.lower()):
            key_name = key_name[: -len(suffix)]
    return key_name.strip(" -–—")


def sheet(wb, title, headers, widths, rows, note):
    ws = wb.create_sheet(title)
    ws.append(headers)
    for cell in ws[1]:
        cell.fill = HEAD_FILL
        cell.font = HEAD_FONT
        cell.alignment = Alignment(horizontal="center", vertical="center")
    for row in rows:
        ws.append(row)
    for index, width in enumerate(widths, start=1):
        ws.column_dimensions[get_column_letter(index)].width = width
    ws.freeze_panes = "A2"
    if rows:
        ws.auto_filter.ref = f"A1:{get_column_letter(len(headers))}{len(rows) + 1}"
    for r in range(2, len(rows) + 2):
        ws.cell(row=r, column=1).font = DIM_FONT
    if rows:
        dv = DataValidation(type="list", formula1='"' + ",".join(LEVELS) + '"', allow_blank=True)
        dv.prompt = "填这个点位在几层；留空 = 跟随当前楼层显示"
        dv.promptTitle = "层级"
        ws.add_data_validation(dv)
        col = get_column_letter(headers.index("层级") + 1)
        dv.add(f"{col}2:{col}{len(rows) + 1}")
    if note:
        ws.cell(row=len(rows) + 3, column=1, value=note).font = DIM_FONT
    return ws


def main():
    root = Path(sys.argv[1])
    out = Path(sys.argv[2])
    assets = root / "Assets"
    kinds = container_names(root)

    maps_doc = load(assets / "maps" / "maps.json")
    layered = {}
    for m in maps_doc["maps"]:
        if m["key"].endswith("-tiles"):
            continue
        if len(m.get("layers") or []) > 1:
            names = ["地面"] + [
                LAYER_ZH.get(l["name"], l["name"]) for l in m["layers"] if l.get("svgLayer")
            ]
            layered[m["key"]] = (m.get("displayName") or m["key"], names)

    doors = load(assets / "doors.json")
    markers = load(assets / "markers.json")
    cache = load(Path.home() / "AppData/Local/TarkovMapLite/data-v8.json")

    # 编号里的 map 段用数据键，用户填回来的表按它定位
    door_rows = []
    for map_key, block in doors.items():
        if map_key not in layered:
            continue
        display, names = layered[map_key]
        for i, door in enumerate(block.get("doors") or []):
            door_rows.append([
                f"{map_key}/door/{i}", display, door_display(door[2] if len(door) > 2 else ""),
                "需电源" if (len(door) > 3 and door[3]) else "",
                door[0], door[1], "", " / ".join(names),
            ])

    switch_rows = []
    for map_key, block in doors.items():
        if map_key not in layered:
            continue
        display, names = layered[map_key]
        for i, sw in enumerate(block.get("switches") or []):
            raw = sw[2] if len(sw) > 2 else ""
            switch_rows.append([
                f"{map_key}/switch/{i}", display, switch_label(raw), raw,
                sw[0], sw[1], "", " / ".join(names),
            ])

    faction = {"pmc": "PMC", "scav": "SCAV", "shared": "合作", "": ""}
    exit_rows = []
    for label, key in (("撤离点", "extract"), ("转移点", "transit")):
        for map_key, items in (cache.get(key + "s") or {}).items():
            if map_key not in layered:
                continue
            display, names = layered[map_key]
            for i, point in enumerate(items):
                exit_rows.append([
                    f"{map_key}/{key}/{i}", display, label, point.get("n", ""),
                    faction.get(point.get("t", ""), point.get("t", "")),
                    point.get("x"), point.get("z"), "", " / ".join(names),
                ])

    container_rows = []
    for map_key, block in (markers.get("maps") or {}).items():
        if map_key not in layered:
            continue
        display, names = layered[map_key]
        for i, item in enumerate(block.get("containers") or []):
            slug = item[2] if len(item) > 2 else ""
            container_rows.append([
                f"{map_key}/container/{i}", display, kinds.get(slug, slug),
                item[0], item[1], "", " / ".join(names),
            ])

    wb = Workbook()
    wb.remove(wb.active)

    intro = wb.create_sheet("说明")
    intro.column_dimensions["A"].width = 14
    intro.column_dimensions["B"].width = 100
    info = [
        ("这是什么", "地图上「推导不出楼层」的点位清单。任务区域能从接口的 top/bottom 算出楼层；"
                     "门锁、闸点、撤离点、转移点、容器只有平面坐标，所以现在不分楼层、一直显示。"),
        ("怎么填", "在「层级」列填这个点位在几层（有下拉），可以留空。留空的点保持现在的行为。"),
        ("取值", " / ".join(LEVELS) + "　也可以直接写「可选层级」列里该图自己的层名"),
        ("编号别动", "第一列是回写用的编号（如 reserve/door/12），删了就对不上数据了。"),
        ("填完给我", "把 xlsx 给我，我生成一张对应表，之后这些点位也会按楼层淡化/隐藏。"),
        ("优先级", "门锁 + 闸点 + 撤离点/转移点 最值得标（数量少、位置固定）；"
                   "容器表上千行，挑你在意的填就行，不用全填。"),
        ("坐标", "X/Z 是游戏世界坐标，和截图文件名里那三个数是同一套，方便对照。"),
        ("同一位置多条", "撤离点会有 PMC/SCAV 两条几乎重合的记录，都填或都填一样即可。"),
    ]
    for row, (a, b) in enumerate(info, start=1):
        intro.cell(row=row, column=1, value=a).font = Font(bold=True)
        intro.cell(row=row, column=2, value=b).alignment = Alignment(wrap_text=True, vertical="top")
    intro.cell(row=len(info) + 2, column=1, value="统计").font = Font(bold=True)
    intro.cell(row=len(info) + 2, column=2,
               value=f"门锁 {len(door_rows)} · 闸点 {len(switch_rows)} · "
                     f"撤离/转移 {len(exit_rows)} · 容器 {len(container_rows)}")

    sheet(wb, "门锁", ["编号", "地图", "门名", "备注", "X", "Z", "层级", "可选层级"],
          [24, 12, 30, 10, 12, 12, 10, 34], door_rows,
          "门名已去掉「钥匙」后缀；备注写的是这道门是否需要通电。")
    sheet(wb, "闸点", ["编号", "地图", "闸点", "内部名", "X", "Z", "层级", "可选层级"],
          [24, 12, 14, 52, 12, 12, 10, 34], switch_rows,
          "内部名是接口给的原始字符串，怕名称翻译不准时以它为参考。")
    sheet(wb, "撤离点转移点", ["编号", "地图", "类型", "名称", "阵营", "X", "Z", "层级", "可选层级"],
          [26, 12, 10, 32, 8, 12, 12, 10, 34], exit_rows, "")
    sheet(wb, "容器", ["编号", "地图", "类型", "X", "Z", "层级", "可选层级"],
          [28, 12, 18, 12, 12, 10, 34], container_rows,
          "只列多层图上的容器；量大，挑你在意的填即可。")

    wb.save(out)
    print(f"saved {out}")
    print(f"doors={len(door_rows)} switches={len(switch_rows)} exits={len(exit_rows)} "
          f"containers={len(container_rows)}")
    print("layered maps: " + ", ".join(f"{k}={v[0]}" for k, v in layered.items()))


if __name__ == "__main__":
    main()
