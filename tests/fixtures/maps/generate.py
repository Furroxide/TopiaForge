"""Generate the public synthetic map fixture; no creator's map is included."""
import json
from pathlib import Path

entities = {
    "entry": {
        "id": "entry", "name": "Entry", "parent": None,
        "components": {"spawn-location": {}, "transform": {"position": [2, 0, 4]}},
    }
}
for index in range(295):
    key = f"prop-{index:03d}"
    entities[key] = {
        "id": key, "name": f"Prop {index}", "parent": None,
        "components": {"model-renderer": {"model": "kit/floor"}},
    }
project = {
    "version": 1, "name": "Synthetic Courtyard", "modified": "2026-01-01T00:00:00Z",
    "scenes": {"courtyard": {"id": "courtyard", "name": "Courtyard", "entities": entities}},
}
(Path(__file__).parent / "courtyard.json").write_text(
    json.dumps(project, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n"
)
