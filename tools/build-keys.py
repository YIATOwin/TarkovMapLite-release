"""Build Assets/keys.json + Assets/keyicons/*.png from tarkov.dev.

Why build-time: /pve/items is 16.5 MB and its `name` is an i18n key, not a display
name; the Chinese names live in /pve/items_zh. Doing this once here keeps the app
offline and the exe small (only the icons of keys that the door data actually uses
get shipped).

Matching key: doors.json stores the localized key name ("工厂紧急出口钥匙"), and
items_zh gives us item id -> 中文名, so we join on the display name after stripping
the trailing 钥匙/钥匙卡 the same way MapDoors does.

Raw JSON is cached under tools/.cache (curl it once if the API rejects Python's UA),
icons are downloaded 8 at a time, and a key whose icon fails still lands in keys.json
(without an icon) so the list never silently loses entries.

Usage: python build-keys.py <TarkovMapLite dir>
"""
import io
import json
import sys
import time
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

from PIL import Image

BASE = "https://json.tarkov.dev"
UA = {
    "User-Agent": (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
        "(KHTML, like Gecko) Chrome/120.0 Safari/537.36"
    )
}
CACHE = Path(__file__).parent / ".cache"


def fetch(url, attempts=2, timeout=20):
    last = None
    for _ in range(attempts):
        try:
            req = urllib.request.Request(url, headers=UA)
            with urllib.request.urlopen(req, timeout=timeout) as response:
                return response.read()
        except Exception as error:  # noqa: BLE001 - 网络抖动就重试一次
            last = error
            time.sleep(1)
    raise RuntimeError(f"{url}: {last}")


def get(name):
    """取 /xxx 的 data；同名原始 JSON 缓存在 tools/.cache/<name>.json。"""
    CACHE.mkdir(exist_ok=True)
    cached = CACHE / f"{name.replace('/', '_')}.json"
    if cached.exists() and cached.stat().st_size > 100:
        raw = cached.read_bytes()
    else:
        raw = fetch(BASE + name, timeout=120)
        cached.write_bytes(raw)
    return json.loads(raw)["data"]


def unwrap(value, key):
    """有的接口把真正的数据再包一层 {items: {...}}，两种形态都认。"""
    if isinstance(value, dict) and isinstance(value.get(key), (dict, list)):
        return value[key]
    return value


def strip_key(name):
    """和 MapDoors.DoorLabel 一致：去掉尾部「钥匙/钥匙卡」，两边都这么处理才好对。"""
    if not name:
        return ""
    for suffix in (" 钥匙卡", "钥匙卡", " 钥匙", "钥匙", "Keycard", "Key Card", "Key"):
        if name.endswith(suffix):
            name = name[: -len(suffix)]
            break
    return name.strip(" -–—_")


def main():
    root = Path(sys.argv[1])
    assets = root / "Assets"
    out_json = assets / "keys.json"
    icon_dir = assets / "keyicons"
    icon_dir.mkdir(parents=True, exist_ok=True)

    doors = json.loads((assets / "doors.json").read_text(encoding="utf-8"))
    wanted = {}
    for block in doors.values():
        for door in block.get("doors") or []:
            raw = door[2] if len(door) > 2 else ""
            label = strip_key(raw)
            if label:
                wanted.setdefault(label, raw)
    print(f"doors 里用到 {len(wanted)} 个不同的钥匙名")

    items = unwrap(get("/pve/items"), "items")
    zh = unwrap(get("/pve/items_zh"), "items_zh")
    print(f"items={len(items)} 条, items_zh={len(zh)} 条")
    if isinstance(items, list):
        items = {item.get("id", str(i)): item for i, item in enumerate(items)}

    def chinese(item):
        """items_zh 的键是 item.name 那个 i18n 字符串，值才是中文名。"""
        for field in ("name", "shortName"):
            key = item.get(field) or ""
            if key in zh:
                return zh[key]
        return ""

    by_label = {}
    for item_id, item in items.items():
        if not isinstance(item, dict):
            continue
        label = strip_key(chinese(item))
        if label and label not in by_label:
            by_label[label] = (item_id, item)
    print(f"物品里有中文名的 {len(by_label)} 个")

    matched = {label: by_label[label] for label in wanted if label in by_label}
    print(f"能对上的钥匙 {len(matched)}/{len(wanted)}")

    def download(entry):
        label, (item_id, item) = entry
        target = icon_dir / f"{item_id}.png"
        if target.exists():
            return label, item_id, True
        url = item.get("iconLink") or f"https://assets.tarkov.dev/{item_id}-icon.webp"
        try:
            image = Image.open(io.BytesIO(fetch(url, timeout=15))).convert("RGBA")
            image.thumbnail((96, 96), Image.LANCZOS)
            canvas = Image.new("RGBA", (96, 96), (0, 0, 0, 0))
            canvas.paste(image, ((96 - image.width) // 2, (96 - image.height) // 2), image)
            canvas.save(target, "PNG", optimize=True)
            return label, item_id, True
        except Exception as error:  # noqa: BLE001 - 单张失败不该中断整批
            print(f"  图标失败 {label}: {error}")
            return label, item_id, False

    with ThreadPoolExecutor(max_workers=8) as pool:
        outcomes = list(pool.map(download, sorted(matched.items())))
    saved = sum(1 for _, _, ok in outcomes if ok)

    result = {}
    for label, item_id, ok in outcomes:
        entry = {"id": item_id, "name": chinese(matched[label][1])}
        if ok:
            entry["icon"] = f"keyicons/{item_id}.png"
        result[label] = entry

    out_json.write_text(
        json.dumps(
            {
                "generated": time.strftime("%Y-%m-%d %H:%M"),
                "source": "json.tarkov.dev /pve/items + /pve/items_zh, icons from assets.tarkov.dev",
                "keys": result,
            },
            ensure_ascii=False,
            indent=1,
        ),
        encoding="utf-8",
    )
    missing = [label for label in wanted if label not in result]
    print(f"写了 {len(result)} 把钥匙（有图标 {saved} 张）-> {out_json}")
    if missing:
        print(f"没匹配到的 {len(missing)} 个，前 12 个：")
        for label in missing[:12]:
            print("   ", label)


if __name__ == "__main__":
    main()
