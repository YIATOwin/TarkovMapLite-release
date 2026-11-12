"""Build a standalone, complete floor image for every layered map.

The map sources ship two shapes:
  · overlay maps  - base.png is the ground floor drawing; each floor layer is only
                    "the walls this floor adds" (5-43% of the base's opaque area);
  · complete maps - factory ships a whole drawing per floor (100% coverage).

The app must show ONE floor at a time with the others fully hidden (that is what the
official map does), so every floor needs a complete standalone image:

  overlay map  -> floor_<layer>.png = base.png over-composited with <layer>.png
  complete map -> floor_<layer>.png = <layer>.png as is

Ground floor stays base.png. tools/build-unions.py is superseded by this script.

Usage: python build-floors.py <TarkovMapLite dir>
"""
import json
import sys
from pathlib import Path

from PIL import Image

SAMPLE = 300


def coverage(base, layer):
    """层的不透明区域覆盖了底图不透明区域的多少（这个比例决定它是完整图还是叠加层）。"""
    small_b = base.resize((SAMPLE, max(1, int(SAMPLE * base.height / base.width))))
    small_l = layer.resize(small_b.size)
    opaque = [(x, y) for y in range(small_b.height) for x in range(small_b.width)
              if small_b.getpixel((x, y))[3] > 40]
    if not opaque:
        return 0.0
    hit = sum(1 for (x, y) in opaque if small_l.getpixel((x, y))[3] > 40)
    return hit / len(opaque)


def main():
    root = Path(sys.argv[1])
    assets = root / "Assets" / "maps"
    doc = json.loads((assets / "maps.json").read_text(encoding="utf-8"))
    made = 0
    for entry in doc["maps"]:
        key = entry.get("key") or ""
        image_key = entry.get("imageKey") or key
        folder = assets / image_key
        layers = [l.get("svgLayer") for l in entry.get("layers") or [] if l.get("svgLayer")]
        if len(layers) < 2:
            continue
        base_path = folder / "base.png"
        if not base_path.exists():
            continue
        base = Image.open(base_path).convert("RGBA")
        for name in layers:
            src = folder / f"{name}.png"
            if not src.exists():
                continue
            layer = Image.open(src).convert("RGBA")
            if layer.size != base.size:
                layer = layer.resize(base.size, Image.LANCZOS)
            cover = coverage(base, layer)
            if cover >= 0.9:
                # 本身就是完整平面图，直接用
                result = layer
                kind = "完整图"
            else:
                # 叠加层：和底图合成，得到"这一层的完整平面图"
                result = Image.alpha_composite(base, layer)
                kind = "底图+叠加"
            out = folder / f"floor_{name}.png"
            result.save(out, "PNG", optimize=True)
            made += 1
            print(f"{key:<20} {name:<16} 覆盖{cover*100:5.1f}%  {kind:<10} -> {out.stat().st_size/1024:6.0f} KB")
    print(f"完成：{made} 张楼层图")


if __name__ == "__main__":
    main()
