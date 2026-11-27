"""Synthetic shared source-library cases; no remote maps or private content."""
import json
from pathlib import Path

cases = []
def case(name, data, valid=False):
    cases.append(dict(name=name, json=data if isinstance(data, str) else json.dumps(data, separators=(',', ':')), valid=valid))
def document(*sources): return dict(schemaVersion=1, sources=list(sources))
def source(**overrides): return dict(dict(map='example-map', scene='courtyard', name='Courtyard'), **overrides)
case('empty library', document(), True)
case('one published scene', document(source()), True)
case('different scenes', document(source(), source(scene='workshop')), True)
case('case-sensitive map ids', document(source(map='Example'), source(map='example')), True)
case('maximum ids', document(source(map='m'*64, scene='s'*128, name='N'*128)), True)
case('duplicate source', document(source(), source()))
case('URL instead of canonical id', document(source(map='https://robotopia.gg/map/example/')))
case('live document rejected', document(source(map='automerge:example')))
case('map id length', document(source(map='m'*65)))
case('scene length', document(source(scene='s'*129)))
case('name length', document(source(name='n'*129)))
case('name control', document(source(name='Map\nPrivate')))
case('empty name', document(source(name=' ')))
case('scene traversal', document(source(scene='../other')))
case('unknown source field', document(dict(source(), token='synthetic')))
case('unknown document field', dict(document(), token='synthetic'))
case('wrong version', dict(document(), schemaVersion=2))
case('missing version', dict(sources=[]))
case('wrong field type', document(source(scene=1)))
case('duplicate property', '{"schemaVersion":1,"schemaVersion":1,"sources":[]}')
case('unpaired surrogate', '{"schemaVersion":1,"sources":[{"map":"m","scene":"s","name":"\\ud800"}]}')
case('trailing data', '{"schemaVersion":1,"sources":[]}true')
(Path(__file__).parent/'published-source-cases.json').write_text(json.dumps(cases,indent=2)+'\n',encoding='utf-8',newline='\n')
