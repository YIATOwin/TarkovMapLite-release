"""Build Assets/maps/<map>/_union.png — every layer of a map composited into one image.

Why: the map data comes in two shapes. Most maps ship a base image plus thin floor
overlays ("the walls this floor adds"), but factory ships a complete drawing for each
floor. Drawing base-then-layer therefore double-draws on factory.

The renderer now treats the union as context and the selected floor as the solid one:
   union (faint)  ->  selected floor (full opacity)
That works for both shapes, and the union edge doubles as the map outline the user
asked for.

Usage: python build-unions.py <TarkovMapLite dir>
"""
import json
import sys
from pathlib import Path

from PIL import Image

NAMES = ("base", "_union")


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
            continue  # 单层图没有合成意义
        base_path = folder / "base.png"
        if not base_path.exists():
            print(f"{key}: 没有 base.png，跳过")
            continue
        try:
            union = Image.open(base_path).convert("RGBA")
            used = 0
            for name in layers:
                p = folder / f"{name}.png"
                if not p.exists():
                    continue
                layer = Image.open(p).convert("RGBA")
                if layer.size != union.size:
                    layer = layer.resize(union.size, Image.LANCZOS)
                # alpha_composite = 标准的 over 合成，不压暗
                union = Image.alpha_composite(union, layer)
                used += 1
            out = folder / "_union.png"
            union.save(out, "PNG", optimize=True)
            made += 1
            print(f"{key:<22} 合成 {used + 1} 张 -> _union.png  {out.stat().st_size/1024:.0f} KB")
        except Exception as error:  # noqa: BLE001 - 一张失败不该中断整批
            print(f"{key}: 合成失败 {error}")
    print(f"完成：{made} 张")


if __name__ == "__main__":
    main()
