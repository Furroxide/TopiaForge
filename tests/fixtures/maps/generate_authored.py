"""Generate the small, synthetic authored-map conformance corpus."""
import json
from pathlib import Path

root = Path(__file__).parent


def entity(name, x, model=None, parent=None, rotation=None, **components):
    value = {'name': name, 'components': {
        'transform': {'position': {'x': x, 'y': 2, 'z': 3},
                      'rotation': rotation or {'x': 0, 'y': 90, 'z': 0},
                      'scale': {'x': 1, 'y': 2, 'z': 1}}, **components}}
    if model:
        value['components']['model-renderer'] = {'model': model}
    if parent:
        value['parent'] = parent
    return value


entities = {
    'child': entity('Child', 1, 'kit/box', 'root', {'x': 0, 'y': 0.5, 'z': 0, 'w': 0.8660254}),
    'consoleA': entity('Console', 4, 'kit/console'),
    'consoleB': entity('Console', 4.5, 'kit/console'),
    'crate': entity('Supply crate', 12, 'kit/box', **{'poi': {}}),
    'entry': entity('Entry', 0, **{'spawn-location': {}}),
    'gate': entity('Console', 20, 'kit/gate'),
    'root': entity('Group', 0, **{'prefab-instance': {'uri': 'kit:group'}, 'aoi': {}, 'agent': {}}),
}
project = {'name': 'Workshop', 'modified': '2026-01-01T00:00:00Z', 'scenes': {
    'workshop': {'id': 'workshop', 'name': 'Workshop', 'environment': 'day', 'entities': entities}}}


def anchor(key, identity, name=None, model=None, x=None, required=True, tolerance=None):
    value = {'key': key, 'entityId': identity, 'required': required}
    if name is not None:
        value['name'] = name
    if model is not None:
        value['modelId'] = model
    if x is not None:
        value['editorPosition'] = {'x': x, 'y': 2, 'z': 3}
    if tolerance is not None:
        value['positionTolerance'] = tolerance
    return value


anchors = [
    anchor('id', 'entry'),
    anchor('renamed', 'crate', 'Old crate', 'kit/box', 30),
    anchor('near', 'oldcrate', 'Supply crate', 'kit/box', 11.5),
    anchor('far', 'oldfar', 'Supply crate', 'kit/box', 10),
    anchor('ambiguous', 'oldconsole', 'Console', 'kit/console', 4.2),
    anchor('wrong-model', 'oldgate', 'Console', 'kit/console', 20),
    anchor('optional', 'absent', required=False),
    anchor('wide', 'widecrate', 'Supply crate', 'kit/box', 10, tolerance=2),
]
outcomes = [
    ('id', 'Id', 'entry', False), ('renamed', 'Id', 'crate', True),
    ('near', 'NameModelAndPosition', 'crate', True), ('far', 'Missing', None, False),
    ('ambiguous', 'Ambiguous', None, False), ('wrong-model', 'Missing', None, False),
    ('optional', 'Missing', None, False), ('wide', 'NameModelAndPosition', 'crate', True),
]
expected = {'order': ['root', 'child', 'consoleA', 'consoleB', 'crate', 'entry', 'gate'],
            'isComplete': False,
            'outcomes': [dict(key=k, match=m, entityId=e, changed=c) for k, m, e, c in outcomes]}
for name, data in [('authored-project.json', project),
                   ('authored-anchors.json', {'schemaVersion': 1, 'anchors': anchors}),
                   ('authored-expected.json', expected)]:
    (root / name).write_text(json.dumps(data, indent=2) + '\n', encoding='utf-8', newline='\n')
