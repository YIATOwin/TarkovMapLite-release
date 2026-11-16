"""Join public story objectives to hand-placed coordinates; retain all unlocated objectives.

python tools/build-story-tasks.py story.json story-marks.json mapdata.js
The last file supplies map floor *array order*. -1 is the base layer, not a basement.
No JavaScript is executed. Chapter text retains its upstream attribution in NOTICE.
"""
import argparse
import collections
import json
import re
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('chapters')
parser.add_argument('marks')
parser.add_argument('floor_maps')
args = parser.parse_args()
root = Path(__file__).resolve().parent.parent
story = json.loads(Path(args.chapters).read_text(encoding='utf-8-sig'))
marks = json.loads(Path(args.marks).read_text(encoding='utf-8-sig'))
source_maps = Path(args.floor_maps).read_text(encoding='utf-8-sig')
heads = list(re.finditer(r'^  (?:"([^"]+)"|(\w+)): \{', source_maps, re.M))
floors = {}
for i, head in enumerate(heads):
    block = source_maps[head.end():heads[i+1].start() if i+1 < len(heads) else len(source_maps)]
    if 'floors: [' not in block:
        continue
    block = block.split('floors: [', 1)[1].split('labels:', 1)[0]
    floors[head[1] or head[2]] = re.findall(r'svgLayer:\s*"([^"]+)"', block)

positions = {m['objectiveId']: m for m in marks['marks']}
chapters = []
for chapter in story['chapters']:
    section = ''; tasks = []
    for objective in chapter['objectives']:
        if objective.get('type') == 'section':
            section = objective['description']; continue
        placed = positions.get(objective['id'], {})
        groups = []
        for point in placed.get('points', []):
            index = point.get('floor', -1)
            layers = floors.get(point['map'], [])
            layer = '' if index == -1 else layers[index] if 0 <= index < len(layers) else None
            groups.append({'map': point['map'], 'layer': layer, 'sourceFloorIndex': index,
                'points': [[p['x'], p['z']] for p in point['pts']]})
        tasks.append({'id': objective['id'], 'sourceQuestId': objective.get('sourceQuestId', ''),
            'step': len(tasks)+1, 'description': objective['description'], 'section': section,
            'optional': objective.get('type') == 'optional',
            'maps': sorted(set(objective.get('maps', []) + placed.get('maps', []) + [g['map'] for g in groups])),
            'zones': groups})
    chapters.append({'id': chapter['id'], 'name': chapter['name'], 'order': chapter['order'],
        'description': chapter.get('description', ''), 'wiki': chapter.get('wikiLink', ''),
        'tasks': tasks})

result = {'schema': 1, 'chapterAsOf': story['bakedAt'], 'positionAsOf': marks['generatedAt'],
    'sources': {
        'chapters': 'https://github.com/szepiz/tarkov-quest-data/blob/16ca34ade3e4a874b83553ffc2f2e577be966fa9/mapdata/story.json',
        'positions': 'https://github.com/szepiz/tarkov-quest-data/blob/main/api/firstparty/story-marks.json',
        'floorOrder': 'https://github.com/szepiz/tarkov-questing-companion/blob/main/mapdata.js'},
    'chapters': chapters}
(root/'Assets/story-tasks.json').write_text(json.dumps(result, ensure_ascii=False, separators=(',', ':'))+'\n', encoding='utf-8')
print(f'{len(chapters)} series, {sum(len(c["tasks"]) for c in chapters)} subtasks, '
      f'{sum(bool(t["zones"]) for c in chapters for t in c["tasks"])} located objectives, '
      f'{sum(len(t["zones"]) for c in chapters for t in c["tasks"])} coordinate groups')
