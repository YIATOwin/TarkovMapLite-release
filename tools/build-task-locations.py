"""Preserve factual objective positions from the pinned tarkov.dev snapshot.

Usage: python tools/build-task-locations.py path/to/tarkov_tasks_raw.json
"""
import argparse
import json
import math
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('snapshot')
args = parser.parse_args()
tasks = json.loads(Path(args.snapshot).read_text(encoding='utf-8-sig'))
rows = lambda value: value if isinstance(value, list) else [value] if value else []
points = []
excluded = []
seen = set()
for task in tasks:
    for objective in rows(task.get('objectives')):
        for zone in rows(objective.get('zones')):
            position = tuple(zone.get(axis) for axis in ('x', 'y', 'z'))
            if not all(isinstance(n, (float, int)) and not isinstance(n, bool) and math.isfinite(n) for n in position):
                raise ValueError(f'Invalid position in {task["id"]}/{objective["id"]}')
            row = (task['id'], objective['id'], zone['normalizedName'], objective['type'], *position)
            if row in seen:
                continue
            seen.add(row)
            record = dict(zip(('taskId', 'objectiveId', 'map', 'type', 'x', 'y', 'z'), row))
            # Same snapshot also has this exact x/z on Customs at y=2.22, matching the
            # original Customs outline and the objective text. Do not import its bad Reserve duplicate.
            if task['id'] == '59c124d686f774189b3c843f' and zone['normalizedName'] == 'reserve' and position == (-334.93, -101.46, -163.46):
                excluded.append(record)
                continue
            points.append(record)
root = Path(__file__).resolve().parent.parent
document = {
    'source': 'https://github.com/sayser/TarkovTracker/blob/f4ad6a50be49bbba3c3791fd52aa042d99ce3cfd/tools/data/tarkov_tasks_raw.json',
    'asOf': '2026-09-14',
    'points': points,
    'excluded': excluded,
    'regionExclusions': {'59c124d686f774189b3c843f': ['reserve']},
}
(root / 'Assets/task-locations.json').write_text(json.dumps(document, ensure_ascii=False, separators=(',', ':'))+'\n', encoding='utf-8')
seed_path = root / 'Assets/data-seed.json'
seed = json.loads(seed_path.read_text(encoding='utf-8-sig'))
seed['schema'] = 14
for task in seed['tasks']:
    if task['id'] in document['regionExclusions'] and (task.get('meta') or {}).get('AsOf') == document['asOf']:
        task['meta']['Regions'] = [m for m in task['meta']['Regions'] if m not in document['regionExclusions'][task['id']]]
seed_path.write_text(json.dumps(seed, ensure_ascii=False, separators=(',', ':'))+'\n', encoding='utf-8')
print(f'{len(points)} unique positions, {len(set(p["taskId"] for p in points))} tasks; {len(excluded)} conflicting record excluded')
