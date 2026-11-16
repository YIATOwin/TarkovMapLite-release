"""Enrich the offline seed with the public tarkov.dev snapshot; never invent missing flags.

Usage: python tools/build-task-metadata.py path/to/tarkov_tasks_raw.json
Snapshot: https://github.com/sayser/TarkovTracker/blob/master/tools/data/tarkov_tasks_raw.json
"""
import json
import argparse
from pathlib import Path

root = Path(__file__).resolve().parent.parent
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('source')
parser.add_argument('--as-of', default='', help='Actual source snapshot date, not download date')
parser.add_argument('--source-url', default='https://github.com/sayser/TarkovTracker/blob/master/tools/data/tarkov_tasks_raw.json')
args = parser.parse_args()
source = json.loads(Path(args.source).read_text(encoding='utf-8-sig'))
source = source if isinstance(source, list) else list(source.get('data', source).get('tasks', {}).values())
seed_path = root / 'Assets/data-seed.json'
seed = json.loads(seed_path.read_text(encoding='utf-8-sig'))
maps = json.loads((root / 'Assets/maps/maps.json').read_text(encoding='utf-8'))['maps']
aliases = {'Streets': 'streets-of-tarkov', 'Labs': 'the-lab', 'Ground Zero 21+': 'ground-zero-21', 'The Lab (Dark)': 'the-lab-dark'}

def key(name):
    name = aliases.get(name, name.lower().replace(' ', '-'))
    m = next((m for m in maps if m['key'] == name), {})
    return m.get('dataKey') or name

by_id = {t['id']: t for t in source}
def rows(value):
    return value if isinstance(value, list) else [value] if value else []

count = 0
for task in seed['tasks']:
    raw = by_id.get(task['id'])
    if not raw:
        continue
    regions = set()
    if raw.get('map') and raw['map'] not in ('Any', 'any'):
        regions.add(key(raw['map']))
    objectives = rows(raw.get('objectives'))
    for objective in objectives:
        for name in rows(objective.get('maps')):
            regions.add(key(name))
    meta = {'TraderName': raw.get('trader', ''), 'Regions': sorted(regions),
            'RegionKnown': 'map' in raw and all('maps' in o for o in objectives),
            'MinLevel': raw.get('minPlayerLevel'), 'Faction': raw.get('factionName'),
            'Kappa': raw.get('kappaRequired'), 'Lightkeeper': raw.get('lightkeeperRequired'), 'AsOf': args.as_of}
    task['meta'] = meta
    count += 1
seed['schema'] = 13
seed['taskMetadataSource'] = args.source_url
seed_path.write_text(json.dumps(seed, ensure_ascii=False, separators=(',', ':')) + '\n', encoding='utf-8')
print(f'Metadata attached: {count}/{len(seed["tasks"])} tasks')
