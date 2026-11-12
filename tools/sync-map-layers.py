"""Sync supported map floors/height extents from tarkov.dev maps.json.

Usage: python tools/sync-map-layers.py path/to/tarkov-dev-maps.json
Requires Pillow. Existing images/projection calibrations are preserved. Missing
floor images are reprojected from the available sibling variant, never guessed.
"""
import json
import re
import sys
from pathlib import Path
from PIL import Image
from importlib import import_module

projection = import_module('build-reserve-floors')


def layer_id(layer):
    if layer.get('svgLayer'):
        return layer['svgLayer']
    return {'2nd Floor': 'Second_Floor', '3rd Floor': 'Third_Floor',
            '4th Floor': 'Fourth_Floor', '5th Floor': 'Fifth_Floor',
            'Second Level': 'Second_Floor', 'Technical': 'Technical'}[layer['name']]


def level(layer):
    match = re.match(r'\d+', layer['name'])
    if match:
        return int(match[0]) - 1
    return 1 if layer['name'] == 'Second Level' else -1


def normalize(name):
    return name.replace('_Level', '').replace('_Floor', '').lower()


def main():
    root = Path(__file__).resolve().parent.parent
    assets = root / 'Assets'
    pack_path = assets / 'maps/maps.json'
    pack = json.loads(pack_path.read_text(encoding='utf-8-sig'))
    maps = pack['maps']
    upstream = json.loads(Path(sys.argv[1]).read_text(encoding='utf-8-sig'))
    sources = {m['normalizedName']: next((x for x in m['maps'] if x.get('projection') == 'interactive'), None)
               for m in upstream}
    rules = {}
    derived = {
        'reserve': ('reserve-tiles', {'Second_Floor', 'Third_Floor', 'Fourth_Floor', 'Fifth_Floor'}),
        'customs': ('customs-tiles', {'Fourth_Floor'}),
        'shoreline-tiles': ('shoreline', {'Second_Floor', 'Third_Floor', 'Underground_Level'}),
        'interchange-tiles': ('interchange', {'First_Floor', 'Second_Floor'}),
    }

    def physical(m):
        return m.get('dataKey') or m.get('imageKey') or m['key']

    def folder(m):
        return assets / 'maps' / (m.get('imageKey') or m['key'])

    def base_size(m):
        path = next(p for suffix in ['.jpg', '.png'] if (p := folder(m) / ('base' + suffix)).exists())
        with Image.open(path) as image:
            return image.size

    for m in maps:
        source = sources.get(physical(m))
        if not source or not source.get('layers'):
            # Single-floor sources (Terminal) must not advertise ground companion groups.
            if source is not None:
                m['layers'] = []
            continue
        layers = []
        entries = []
        for original in source['layers']:
            key = layer_id(original)
            n = level(original)
            prior = next((l for l in m.get('layers', []) if normalize(l['svgLayer']) == normalize(key)), None)
            stem = prior['svgLayer'] if prior else key
            path = folder(m) / (stem + '.png')
            recipe = derived.get(m['key'])
            if not path.exists() or (recipe and key in recipe[1]):
                donor = next(((other, folder(other) / (l['svgLayer'] + '.png'))
                              for other in maps if physical(other) == physical(m)
                              and other['key'] != m['key']
                              and (not recipe or key not in recipe[1] or other['key'] == recipe[0])
                              for l in other.get('layers', [])
                              if normalize(l['svgLayer']) == normalize(key)
                              and (folder(other) / (l['svgLayer'] + '.png')).exists()), None)
                if donor is None:
                    raise RuntimeError(f'No source artwork for {m["key"]}/{stem}')
                other, donor_path = donor
                width, height = base_size(m)
                with Image.open(donor_path) as image:
                    transform = projection.compose(projection.matrix(other, *image.size),
                                                   projection.inverse(projection.matrix(m, width, height)))
                    result = image.convert('RGBA').transform((width, height), Image.Transform.AFFINE,
                                                            transform, Image.Resampling.BICUBIC)
                    result.save(path, optimize=True)
                    print(f'Reprojected {donor_path.parent.name}/{donor_path.name} -> {m["key"]}/{stem}')
            name = f'{n + 1} 层' if n > 0 else {
                'Bunkers': '地堡', 'Technical': '技术层', 'Garage': '车库', 'Tunnels': '隧道'
            }.get(original['name'], '地下')
            layers.append({'name': name, 'svgLayer': stem, 'level': n})
            entries.append({'layer': key, 'level': n, 'extents': original.get('extents', [])})
        m['layers'] = layers
        rules[m.get('dataKey') or m['key']] = {'layers': entries}
        print(f'{m["key"]}: {len(layers) + 1} floors')

    # Aliases that share art also share its physical floor layout.
    (assets / 'floor-extents.json').write_text(json.dumps(rules, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    pack_path.write_text(json.dumps(pack, ensure_ascii=False, indent=1) + '\n', encoding='utf-8')
    index_path = assets / 'maps/index.json'
    index = json.loads(index_path.read_text(encoding='utf-8-sig'))
    for entry in index:
        m = next(m for m in maps if m['key'] == entry['key'])
        entry['variants'] = ['base'] + [l['svgLayer'] for l in m.get('layers', [])]
    index_path.write_text(json.dumps(index, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    main()
