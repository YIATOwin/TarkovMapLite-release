"""Work out the height bands of every map so loot/spawn markers can follow the floor.

markers.json rows are now [x, z, kind, y]; this script turns the y values into
per-layer bands:

  Assets/marker-floors.json = { "<map>": { "bands": [[from, to, layer], ...] } }

The bands come from the data itself: sort all y values, split at the (n-1) largest
gaps, and assign the resulting groups to layers. Layer order is not the order in
maps.json (factory lists 2nd/3rd/Basement but the basement is physically lowest), so
the assignment goes by name: basement/bunker/technical/underground -> lowest band,
"2"/"Second" -> next, and so on; the ground floor takes whatever is left over.

Usage: python build-marker-floors.py <TarkovMapLite dir>
"""
import json
import sys
from pathlib import Path


def bands_for(ys, count):
    """把 y 排序后按最大的 count-1 个空隙切段，返回 [(low, high), ...]。"""
    values = sorted(set(ys))
    if count <= 1 or len(values) <= 1:
        return [(-1e9, 1e9)]
    gaps = []
    for i in range(1, len(values)):
        gaps.append((values[i] - values[i - 1], i))
    gaps.sort(reverse=True)
    cuts = sorted(cut_index for _, cut_index in gaps[: count - 1])
    edges = [values[i - 0] for i in cuts]
    bands = []
    low = -1e9
    for edge in edges:
        bands.append((low, edge))
        low = edge
    bands.append((low, 1e9))
    return bands


def rank(name):
    """楼层的物理高度顺序：越低的数字越小。"""
    text = name.lower()
    for key, value in (
        ("basement", 0), ("bunker", 0), ("under", 0), ("tunnel", 0),
        ("technical", 1), ("garage", 1), ("first", 1),
        ("2", 2), ("second", 2),
        ("3", 3), ("third", 3),
        ("4", 4), ("fourth", 4),
        ("5", 5), ("fifth", 5),
    ):
        if key in text:
            return value
    return None


def main():
    root = Path(sys.argv[1])
    assets = root / "Assets"
    markers = json.loads((assets / "markers.json").read_text(encoding="utf-8"))["maps"]
    doc = json.loads((assets / "maps" / "maps.json").read_text(encoding="utf-8"))
    layers_by_map = {
        (m.get("imageKey") or m["key"]): [l["svgLayer"] for l in m.get("layers") or [] if l.get("svgLayer")]
        for m in doc["maps"]
    }

    result = {}
    for map_key, block in markers.items():
        ys = []
        for row in (block.get("containers") or []) + (block.get("loose") or []):
            if isinstance(row, list) and len(row) >= 4 and isinstance(row[3], (int, float)):
                ys.append(float(row[3]))
        layers = layers_by_map.get(map_key) or []
        if not ys or not layers:
            continue
        # 地面 + 各楼层 = 段数
        count = len(layers) + 1
        bands = bands_for(ys, count)
        # 按物理高度给每个段配名字：最低段给 rank 最小的层，剩下的给地面
        by_rank = sorted(layers, key=lambda name: (rank(name) is None, rank(name) or 0))
        names = ["(ground)"] * len(bands)
        basement_like = [n for n in by_rank if rank(n) == 0]
        upper = [n for n in by_rank if (rank(n) or 0) > 0]
        # 最低段：地下室类；其余从低到高：2 层、3 层……；地面插在 rank 1 的位置
        ordered = []
        if basement_like:
            ordered.append(basement_like[0])
        ordered.append("(ground)")
        ordered.extend(upper)
        for i, name in enumerate(ordered[: len(bands)]):
            names[i] = name
        result[map_key] = {
            "bands": [[round(low, 2) if low > -1e8 else None, round(high, 2) if high < 1e8 else None, names[i]]
                      for i, (low, high) in enumerate(bands)],
            "count": len(ys),
        }

    out = assets / "marker-floors.json"
    out.write_text(json.dumps({"generated": "build-marker-floors.py", "maps": result},
                              ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"{len(result)} 张图有分层数据 -> {out.name}")
    for key in ("factory", "customs", "reserve"):
        if key in result:
            print(f"  {key}: " + " | ".join(f"{b[0]}..{b[1]} = {b[2]}" for b in result[key]["bands"]))


if __name__ == "__main__":
    main()
