"""Build offline point heights from a maps API snapshot or raw snapshot folder.

Usage: python tools/build-point-heights.py <maps.json or snapshot directory>
Only real position.y values are retained. No nearest-point/height guessing.
"""
import json
import math
import sys
from pathlib import Path

ALIASES = {'Ground Zero 21+': 'ground-zero-21', 'Ground Zero Tutorial': 'ground-zero-tutorial',
           'The Lab (Dark)': 'the-lab-dark'}
KINDS = {'extracts': 'extract', 'transits': 'transit', 'switches': 'switch', 'locks': 'door',
         'stationaryWeapons': 'static', 'hazards': 'hazard', 'spawns': 'spawn', 'lootLoose': 'loose',
         'btrStops': 'btr'}


def main():
    source = Path(sys.argv[1])
    root = Path(__file__).resolve().parent.parent
    result = {}

    def add(key, kind, position):
        if not isinstance(position, dict) or not all(isinstance(position.get(c), (int, float))
                and math.isfinite(position[c]) for c in ('x', 'y', 'z')):
            return
        result.setdefault(key, {}).setdefault(kind, []).append(
            [position['x'], position['z'], position['y']])

    paths = [source] if source.is_file() else sorted(source.glob('tarkov_*_raw.json'))
    for path in paths:
        doc = json.loads(path.read_text(encoding='utf-8-sig'))
        maps = doc.get('data', doc).get('maps', [])
        if isinstance(maps, dict):
            maps = maps.values()
        for m in maps:
            key = m.get('normalizedName') or ALIASES.get(m.get('name')) or m.get('name', '').lower().replace(' ', '-')
            if not key:
                continue
            for field, kind in KINDS.items():
                for point in m.get(field) or []:
                    if field == 'locks' and point.get('lockType') != 'door':
                        continue
                    add(key, kind, point if field == 'btrStops' else point.get('position'))
    btr_path = source / 'tarkov_btr.json'
    if btr_path.is_file():
        for key, block in json.loads(btr_path.read_text(encoding='utf-8-sig')).items():
            for stop in block.get('stops', []):
                add(key, 'btr', stop)
    markers = json.loads((root / 'Assets/markers.json').read_text(encoding='utf-8'))['maps']
    for key, block in markers.items():
        for row in block.get('loose', []):
            if len(row) >= 4:
                add(key, 'loose', {'x': row[0], 'z': row[1], 'y': row[3]})
    for kinds in result.values():
        for kind, rows in kinds.items():
            kinds[kind] = [list(row) for row in sorted(set(map(tuple, rows)))]
    output = {'source': 'tarkov.dev API positions; offline raw snapshots from sayser/TarkovTracker/Config',
              'maps': result}
    (root / 'Assets/point-heights.json').write_text(json.dumps(output, ensure_ascii=False, separators=(',', ':'))+'\n', encoding='utf-8')
    def coordinate(x, z):
        return tuple(int(math.copysign(math.floor(abs(v) * 10 + .5), v)) for v in (x, z))
    index = {}
    for map_key, kinds in result.items():
        for kind, rows in kinds.items():
            positions = {}
            for x, z, y in rows:
                key = coordinate(x, z)
                positions[key] = y if key not in positions or positions[key] == y else None
            index[map_key, kind] = positions
    seed_path = root / 'Assets/data-seed.json'
    seed = json.loads(seed_path.read_text(encoding='utf-8'))
    for field, kind in [('extracts', 'extract'), ('transits', 'transit'), ('btrStops', 'btr')]:
        for map_key, points in seed.get(field, {}).items():
            for point in points:
                height = index.get((map_key, kind), {}).get(coordinate(point['x'], point['z']))
                if height is not None:
                    point['h'] = height
    seed['schema'] = max(seed.get('schema', 0), 12)
    seed_path.write_text(json.dumps(seed, ensure_ascii=False, separators=(',', ':'))+'\n', encoding='utf-8')
    print({kind: sum(len(v.get(kind, [])) for v in result.values()) for kind in set(KINDS.values())})


if __name__ == '__main__':
    main()
