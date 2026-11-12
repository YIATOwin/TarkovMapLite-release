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
    """1D k-means：把 y 分成 count 簇，用簇心之间的中点当断点。

    为什么不用"最大空隙切分"：工厂的 y 在 -2.5（地下）、1（地面）、9（3 层）三处聚集，
    但地面附近的点很密，空隙法会把地面和地下并到一段里。k-means 按密度分簇更稳。
    """
    values = sorted(ys)
    if count <= 1 or not values:
        return [(-1e9, 1e9)]
    count = min(count, len(set(values)))
    if count <= 1:
        return [(-1e9, 1e9)]
    # 用分位数初始化簇心，避免随机性
    centers = [values[min(len(values) - 1, int((i + 0.5) * len(values) / count))] for i in range(count)]
    for _ in range(40):
        groups = [[] for _ in centers]
        for value in values:
            index = min(range(len(centers)), key=lambda i: abs(value - centers[i]))
            groups[index].append(value)
        moved = False
        for i, group in enumerate(groups):
            if group:
                center = sum(group) / len(group)
                if abs(center - centers[i]) > 1e-6:
                    moved = True
                centers[i] = center
        if not moved:
            break
    centers.sort()
    edges = [(centers[i] + centers[i + 1]) / 2 for i in range(len(centers) - 1)]
    bands, low = [], -1e9
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
