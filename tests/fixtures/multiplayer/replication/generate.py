"""Generate tests/fixtures/multiplayer/replication/*.json from the written rules, independently of the C# code.

Every value is computed with IEEE-754 doubles in the operation order docs/internal/MultiplayerReplicationModel.md
specifies, and float32 values are written as their bit patterns, so any language can reproduce them exactly. The
random inputs come from a fixed seed and are recorded in the output, so readers never need this script's generator.

Run from the repository root: python -I tests/fixtures/multiplayer/replication/generate.py .
"""
import json
import math
import pathlib
import random
import struct
import sys

ROOT = pathlib.Path(sys.argv[1])
OUT = ROOT / "tests" / "fixtures" / "multiplayer" / "replication"

SQRT2 = 1.4142135623730951
POSITION_UNITS = 512
POSITION_MIN = -(1 << 21)
POSITION_MAX = (1 << 21) - 1
VELOCITY_UNITS = 16
VELOCITY_MIN = -1024
VELOCITY_MAX = 1023
HALF_RANGE = 511
FNV_OFFSET = 0xCBF29CE484222325
FNV_PRIME = 0x00000100000001B3
REST_CONFIRMATION_SENDS = 3


def f32(value):
    return struct.unpack("<f", struct.pack("<f", value))[0]


def bits(value):
    return struct.unpack("<I", struct.pack("<f", value))[0]


def from_bits(pattern):
    return struct.unpack("<f", struct.pack("<I", pattern))[0]


def hex32(value):
    return format(bits(value), "08x")


def round_half_away(value):
    floor = float(math.floor(value))
    fraction = value - floor
    if fraction > 0.5:
        return floor + 1.0
    if fraction < 0.5:
        return floor
    return floor + 1.0 if value > 0.0 else floor


def quantize_axis(value, units, minimum, maximum):
    if math.isnan(value):
        return 0
    if math.isinf(value):
        return maximum if value > 0 else minimum
    rounded = round_half_away(value * units)
    return int(min(max(rounded, minimum), maximum))


def quantize_rotation(x, y, z, w):
    if not all(math.isfinite(v) for v in (x, y, z, w)):
        return identity_bits()
    norm_squared = ((x * x + y * y) + z * z) + w * w
    if not norm_squared > 0.0:
        return identity_bits()
    norm = math.sqrt(norm_squared)
    c = [x / norm, y / norm, z / norm, w / norm]
    largest = 0
    magnitude = abs(c[0])
    for index in range(1, 4):
        if abs(c[index]) > magnitude:
            largest = index
            magnitude = abs(c[index])
    sign = -1.0 if c[largest] < 0.0 else 1.0
    packed = largest
    shift = 2
    for index in range(4):
        if index == largest:
            continue
        scaled = (sign * c[index]) * SQRT2
        scaled = min(max(scaled, -1.0), 1.0)
        packed |= int(round_half_away(scaled * HALF_RANGE) + HALF_RANGE) << shift
        shift += 10
    return packed


def identity_bits():
    return 3 | (HALF_RANGE << 2) | (HALF_RANGE << 12) | (HALF_RANGE << 22)


def dequantize_rotation(packed):
    largest = packed & 3
    values = []
    for k in range(3):
        code = min((packed >> (2 + 10 * k)) & 1023, 2 * HALF_RANGE)
        values.append((code - HALF_RANGE) / HALF_RANGE / SQRT2)
    big = math.sqrt(max(0.0, 1.0 - ((values[0] * values[0] + values[1] * values[1]) + values[2] * values[2])))
    out = []
    rest = iter(values)
    for index in range(4):
        out.append(big if index == largest else next(rest))
    return [f32(v) for v in out]


class BitWriter:
    def __init__(self):
        self.bits = []

    def write(self, value, count):
        assert 0 <= value < (1 << count)
        for index in range(count):
            self.bits.append((value >> index) & 1)

    def to_bytes(self):
        data = bytearray((len(self.bits) + 7) // 8)
        for index, bit in enumerate(self.bits):
            data[index // 8] |= bit << (index % 8)
        return bytes(data)


def transform_hex(position_units, rotation, velocity_units):
    writer = BitWriter()
    for axis in position_units:
        writer.write(axis - POSITION_MIN, 22)
    writer.write(rotation, 32)
    writer.write(1 if velocity_units is not None else 0, 1)
    if velocity_units is not None:
        for axis in velocity_units:
            writer.write(axis - VELOCITY_MIN, 11)
    return writer.to_bytes().hex()


def escape(text):
    out = []
    for character in text:
        if character == "\\":
            out.append("\\\\")
        elif character == "/":
            out.append("\\/")
        elif character == "#":
            out.append("\\#")
        elif character == "\0":
            out.append("\\0")
        else:
            out.append(character)
    return "".join(out)


def scene_preimage(scene, path, component):
    segments = "/".join(escape(name) + "#" + str(sibling) for name, sibling in path)
    return escape(scene) + "\0" + segments + "\0" + escape(component)


def fnv1a64(data):
    value = FNV_OFFSET
    for byte in data:
        value ^= byte
        value = (value * FNV_PRIME) & 0xFFFFFFFFFFFFFFFF
    return value


def dumps(value, compact_depth, depth=0, indent=""):
    """JSON with containers expanded down to compact_depth, and anything deeper written on one line."""
    if depth >= compact_depth or not isinstance(value, (dict, list)) or not value:
        return json.dumps(value, ensure_ascii=False, separators=(", ", ": "))
    inner = indent + "  "
    if isinstance(value, dict):
        items = [inner + json.dumps(key, ensure_ascii=False) + ": " + dumps(item, compact_depth, depth + 1, inner)
                 for key, item in value.items()]
        return "{\n" + ",\n".join(items) + "\n" + indent + "}"
    items = [inner + dumps(item, compact_depth, depth + 1, inner) for item in value]
    return "[\n" + ",\n".join(items) + "\n" + indent + "]"


def write(name, value, compact_depth=2):
    path = OUT / name
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(dumps(value, compact_depth) + "\n", encoding="utf-8", newline="\n")


# ---------------------------------------------------------------- quantization

def float_input(value):
    value = f32(value)
    return {"bits": hex32(value), "value": repr(value)}


def position_case(value):
    value = f32(value)
    units = quantize_axis(value, POSITION_UNITS, POSITION_MIN, POSITION_MAX)
    return {"input": float_input(value), "units": units, "decoded": hex32(f32(units / POSITION_UNITS))}


def velocity_case(value):
    value = f32(value)
    units = quantize_axis(value, VELOCITY_UNITS, VELOCITY_MIN, VELOCITY_MAX)
    return {"input": float_input(value), "units": units, "decoded": hex32(f32(units / VELOCITY_UNITS))}


def rotation_case(components):
    components = [f32(v) for v in components]
    packed = quantize_rotation(*components)
    return {
        "input": [float_input(v) for v in components],
        "bits": format(packed, "08x"),
        "decoded": [hex32(v) for v in dequantize_rotation(packed)],
    }


QUARTER = f32(math.sqrt(0.5))
HALF_STEP = (360.5 / HALF_RANGE) / SQRT2
rng = random.Random(20261013)
random_rotations = []
for _ in range(24):
    u1, u2, u3 = rng.random(), rng.random() * 2 * math.pi, rng.random() * 2 * math.pi
    a, b = math.sqrt(1 - u1), math.sqrt(u1)
    random_rotations.append([a * math.sin(u2), a * math.cos(u2), b * math.sin(u3), b * math.cos(u3)])

positions = [0.0, -0.0, 1.0 / 1024, -1.0 / 1024, 0.00097656, 12.345, -0.5, 1000.0009765625, -1000.0009765625,
             -4096.0, -4096.4, 4095.998046875, 4095.999, 4096.0, 1e9, -1e9, float("nan"), float("inf"),
             float("-inf"), 5e-45, 3.14159265, -273.15]
velocities = [0.0, 0.03125, -0.03125, 22.0, -9.81, 63.9375, 63.96875, 64.0, -64.0, -64.03, 250.0, -250.0,
              float("nan"), float("inf"), 1.0e-3]
rotations = [
    [0.0, 0.0, 0.0, 1.0],
    [0.0, 0.0, 0.0, -1.0],
    [0.5, 0.5, 0.5, 0.5],
    [-0.5, 0.5, -0.5, 0.5],
    [QUARTER, 0.0, 0.0, QUARTER],
    [0.0, QUARTER, 0.0, QUARTER],
    [0.0, 0.0, QUARTER, QUARTER],
    [1.0, 0.0, 0.0, 0.0],
    [0.0, -1.0, 0.0, 0.0],
    [0.2, -1.4, 0.6, 1.2806248],
    [0.0, 0.0, 0.0, 0.0],
    [float("nan"), 0.0, 0.0, 1.0],
    [1e-30, 2e-30, -3e-30, 4e-30],
    [HALF_STEP, HALF_STEP, HALF_STEP, math.sqrt(1.0 - 3.0 * HALF_STEP * HALF_STEP)],
] + random_rotations

transforms = []
for position, rotation, velocity in [
    ([-12.5, 3.25, 900.001], [0.0, 0.38268343, 0.0, 0.9238795], [1.5, -2.0, 30.0]),
    ([0.0, 0.0, 0.0], [0.0, 0.0, 0.0, 1.0], None),
    ([4095.998046875, -4096.0, 0.5], [0.5, -0.5, 0.5, 0.5], [-64.0, 63.9375, 0.0]),
    ([123.456, 7.89, -321.0], random_rotations[0], None),
]:
    position_units = [quantize_axis(f32(v), POSITION_UNITS, POSITION_MIN, POSITION_MAX) for v in position]
    packed = quantize_rotation(*[f32(v) for v in rotation])
    velocity_units = None if velocity is None else [quantize_axis(f32(v), VELOCITY_UNITS, VELOCITY_MIN, VELOCITY_MAX) for v in velocity]
    transforms.append({
        "position": [float_input(v) for v in position],
        "rotation": [float_input(v) for v in rotation],
        "velocity": None if velocity is None else [float_input(v) for v in velocity],
        "hex": transform_hex(position_units, packed, velocity_units),
    })

write("quantization.json", {
    "schema": 1,
    "about": "Transform quantization. Positions: round half away from zero to 1/512 m, clamp to 22 signed bits. "
             "Velocities: 1/16 unit per second in 11 signed bits. Rotations: smallest-three, 2 index bits then three "
             "10-bit codes centred on 511. Transforms pack position, rotation, a velocity flag and the velocity least "
             "significant bit first. Floats are float32 bit patterns.",
    "positions": [position_case(v) for v in positions],
    "velocities": [velocity_case(v) for v in velocities],
    "rotations": [rotation_case(r) for r in rotations],
    "transforms": transforms,
})

# ---------------------------------------------------------------- scene object keys

scene_cases = [
    ("Sandbox", [("World", 0), ("Props", 3), ("Crate", 12)], "UnityEngine.Rigidbody"),
    ("Sandbox", [("World", 0), ("Props", 3), ("Crate", 13)], "UnityEngine.Rigidbody"),
    ("Sandbox", [("World", 0)], "UnityEngine.Transform"),
    ("", [("", 0)], ""),
    ("A/B", [("x\\y/z#w\0v", 2)], "T#1"),
    ("S", [("a#0/b", 0)], "C"),
    ("S", [("a", 0), ("b", 0)], "C"),
    ("Café", [("Über", 1), ("日本", 7), ("\U0001F916", 4294967)], "Grüße.Ω"),
    ("OpenSandbox", [("Arena", 0), ("Ring", 2), ("Pillar (12)", 41)], "UnityEngine.MeshCollider"),
]
write("scene-object-key.json", {
    "schema": 1,
    "about": "Scene object keys: FNV-1a 64 over the UTF-8 bytes of scene NUL path NUL component, where the path joins "
             "name#siblingIndex segments root first with a slash, and every field escapes backslash, slash, hash and "
             "NUL with a backslash (NUL becomes \\0).",
    "fnv1a64": [
        {"utf8": text, "hash": format(fnv1a64(text.encode("utf-8")), "016x")}
        for text in ("", "a", "foobar", "TopiaForge")
    ],
    "cases": [
        {
            "scene": scene,
            "path": [{"name": name, "siblingIndex": sibling} for name, sibling in path],
            "component": component,
            "preimage": scene_preimage(scene, path, component),
            "key": format(fnv1a64(scene_preimage(scene, path, component).encode("utf-8")), "016x"),
        }
        for scene, path, component in scene_cases
    ],
})

# ---------------------------------------------------------------- scheduler


class Scheduler:
    def __init__(self, budget):
        self.budget = budget
        self.credit = 0
        self.sends = 0
        self.states = {}

    def schedule(self, candidates):
        ids = [c["id"] for c in candidates]
        assert len(set(ids)) == len(ids)
        for entity in ids:
            self.states.setdefault(entity, {"acc": 0, "rest": 0, "atRest": False, "last": 0})
        self.credit = min(self.credit, 0) + self.budget
        order = []
        for candidate in candidates:
            state = self.states[candidate["id"]]
            if candidate["atRest"]:
                if not state["atRest"]:
                    state["atRest"] = True
                    state["rest"] = REST_CONFIRMATION_SENDS
                if state["rest"] == 0:
                    state["acc"] = 0
                    continue
            else:
                state["atRest"] = False
                state["rest"] = 0
            state["acc"] = min(state["acc"] + candidate["priority"], (1 << 63) - 1)
            order.append((candidate["id"], state["acc"], candidate["priority"], state["last"], candidate["size"]))
        for entity in list(self.states):
            if entity not in ids:
                del self.states[entity]
        # Highest claim first; ties to the higher priority, then the entity sent longest ago, then the lower id.
        order.sort(key=lambda entry: (-entry[1], -entry[2], entry[3], entry[0]))
        selected = []
        for index, (entity, _, _, _, size) in enumerate(order):
            if self.credit <= 0:
                break
            if index > 0 and size > self.credit:
                continue
            self.credit -= size
            selected.append(entity)
            state = self.states[entity]
            state["acc"] = 0
            self.sends += 1
            state["last"] = self.sends
            if state["atRest"]:
                state["rest"] -= 1
        return selected


def candidate(entity, priority, size, at_rest=False):
    return {"id": entity, "priority": priority, "size": size, "atRest": at_rest}


def scenario(name, budget, ticks):
    scheduler = Scheduler(budget)
    played = []
    for tick in ticks:
        selected = scheduler.schedule(tick)
        played.append({
            "candidates": [[c["id"], c["priority"], c["size"], c["atRest"]] for c in tick],
            "selected": selected,
            "creditAfter": scheduler.credit,
        })
    return {"name": name, "budgetBytesPerTick": budget, "ticks": played}


churn_rng = random.Random(7)
churn = []
for _ in range(40):
    tick = []
    for entity in range(1, 13):
        if churn_rng.random() < 0.25:
            continue
        tick.append(candidate(entity, churn_rng.randint(1, 9), churn_rng.randint(16, 260), churn_rng.random() < 0.3))
    churn.append(tick)

scenarios = [
    scenario("equal priorities rotate", 250, [[candidate(i, 1, 100) for i in range(1, 6)]] * 10),
    scenario("shares follow priorities", 300, [[candidate(1, 1, 120), candidate(2, 2, 80), candidate(3, 5, 200)]] * 12),
    scenario("an oversized update overdraws", 400,
             [[candidate(1, 3, 1000), candidate(2, 1, 50), candidate(3, 1, 60)]] * 10),
    scenario("bodies at rest are confirmed, then free", 200,
             [[candidate(1, 4, 90, True), candidate(2, 1, 90), candidate(3, 2, 90, tick < 4 or tick > 7)]
              for tick in range(12)]),
    scenario("entities come and go", 500, churn),
]
write("scheduler.json", {
    "schema": 1,
    "about": "ReplicationScheduler choices. Each tick: credit = min(credit, 0) + budget; every candidate's accumulator "
             "grows by its priority, except a body at rest after three confirmations (accumulator held at zero); "
             "candidates sort by accumulator descending, then priority descending, then the scheduler's send count "
             "at their last send ascending (never sent is zero), then id ascending; the first is sent while credit "
             "is positive, later ones only if they fit; a sent accumulator resets. Entities missing from a tick are "
             "forgotten.",
    "restConfirmationSends": REST_CONFIRMATION_SENDS,
    "candidateFields": ["id", "priority", "sizeBytes", "atRest"],
    "scenarios": scenarios,
}, compact_depth=4)

# ---------------------------------------------------------------- token bucket


class Bucket:
    def __init__(self, capacity, rate, now):
        self.capacity = capacity * 1_000_000
        self.rate = rate
        self.level = self.capacity
        self.last = now

    def take(self, now, cost):
        if now > self.last:
            elapsed = now - self.last
            self.last = now
            missing = self.capacity - self.level
            if missing > 0 and self.rate > 0:
                useful = (missing + self.rate - 1) // self.rate
                self.level = min(self.capacity, self.level + min(elapsed, useful) * self.rate)
        needed = cost * 1_000_000
        if self.level < needed:
            return False
        self.level -= needed
        return True


def bucket_case(name, capacity, rate, start, steps):
    bucket = Bucket(capacity, rate, start)
    return {
        "name": name,
        "capacity": capacity,
        "tokensPerSecond": rate,
        "startMicros": start,
        "steps": [{"atMicros": at, "cost": cost, "allowed": bucket.take(at, cost)} for at, cost in steps],
    }


write("token-bucket.json", {
    "schema": 1,
    "about": "TokenBucket in integer micro-tokens: starts full, refills elapsed * tokensPerSecond micro-tokens per "
             "microsecond up to capacity, ignores time that runs backwards, and takes a cost only when all of it is "
             "there.",
    "cases": [
        bucket_case("burst then steady", 3, 2, 0, [(0, 1), (0, 1), (0, 1), (0, 1), (499_999, 1), (500_000, 1),
                                                  (750_000, 1), (1_000_000, 1), (1_000_000, 1), (2_500_000, 2),
                                                  (2_500_000, 1)]),
        bucket_case("time runs backwards", 1, 1, 1_000_000, [(1_000_000, 1), (500_000, 1), (1_999_999, 1),
                                                             (2_000_000, 1), (1_000, 1), (3_000_000, 1)]),
        bucket_case("time runs backwards with tokens left", 2, 1, 0, [(1_000_000, 1), (0, 1), (0, 1), (1_000_000, 1),
                                                                      (2_000_000, 1)]),
        bucket_case("a fixed allowance", 2, 0, 0, [(0, 1), (10, 1), (10**12, 1)]),
        bucket_case("multi-token costs", 5, 3, 0, [(0, 5), (333_333, 1), (333_334, 1), (2_000_000, 4),
                                                   (2_000_000, 2), (10**9, 5)]),
        bucket_case("presentation toasts", 4, 2, 0, [(0, 1), (11, 1), (11, 1), (11, 1), (11, 1), (499_999, 1),
                                                     (500_000, 1), (2_000_000, 1)]),
    ],
}, compact_depth=4)
