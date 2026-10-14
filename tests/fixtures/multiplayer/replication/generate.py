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
    # Truncation toward zero keeping the sign, as IEEE-754 does, then the value minus it: exact for every double,
    # unlike the value minus its floor, which rounds for small negatives such as -0.49999999999999994.
    whole = math.copysign(float(math.trunc(value)), value)
    fraction = value - whole
    if fraction >= 0.5:
        return whole + 1.0
    if fraction <= -0.5:
        return whole - 1.0
    return whole


def hex64(value):
    return format(struct.unpack("<Q", struct.pack("<d", value))[0], "016x")


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

rounding_inputs = [0.0, -0.0, 0.3, -0.3, 0.49999999999999994, -0.49999999999999994, 0.5, -0.5, 0.5000000000000001,
                   -0.5000000000000001, 0.9999999999999999, -0.9999999999999999, 1.5, -1.5, 2.5, -2.5,
                   2.4999999999999996, -2.4999999999999996, -1.4999999999999998, 361.33, -361.5, 1023.5, -1024.5,
                   -1072693.4999999998, -1072693.5, 4503599627370497.0, -4503599627370495.5, 1e300, -1e300, 5e-324,
                   -5e-324]

write("quantization.json", {
    "schema": 1,
    "about": "Transform quantization. Rounding is half away from zero on doubles given as bit patterns: the fraction "
             "is the value minus its truncation toward zero (which keeps the sign, so a zero result keeps the input's "
             "sign), and a fraction of at least one half in size rounds away from zero. Positions: 1/512 m, clamped to "
             "22 signed bits. Velocities: 1/16 unit per second in 11 signed bits. Rotations: smallest-three, 2 index "
             "bits then three 10-bit codes centred on 511. Transforms pack position, rotation, a velocity flag and the "
             "velocity least significant bit first. Floats are float32 bit patterns.",
    "rounding": [{"input": hex64(v), "value": repr(v), "rounded": hex64(round_half_away(v))} for v in rounding_inputs],
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

# ---------------------------------------------------------------- snapshot interpolation

CAPACITY = 8
MIN_DELAY = 80_000
MAX_DELAY = 350_000
EXTRAPOLATION_LIMIT = 250_000
DISCONTINUITY = 1_000_000
JITTER_MARGIN = 3
PLAYBACK_SLEW_PERCENT = 10


class InterpolationBuffer:
    """One replica's samples as (server time, x, x velocity or None); y and z stay zero and rotations identity."""

    def __init__(self, nominal):
        self.samples = []
        self.started = False
        self.offset_scaled = 0
        self.jitter_scaled = 0
        self.interval_scaled = nominal << 3
        self.last_transit = 0
        self.playback = 0
        self.rendered = False
        self.last_render = 0
        self.last_local = 0

    def jitter(self):
        return (self.jitter_scaled + 8) >> 4

    def interval(self):
        return (self.interval_scaled + 4) >> 3

    def offset(self):
        return (self.offset_scaled + 4) >> 3

    def largest_gap(self):
        return max((later[0] - earlier[0] for earlier, later in zip(self.samples, self.samples[1:])), default=0)

    def target_delay(self):
        delay = max(self.interval(), self.largest_gap()) + JITTER_MARGIN * self.jitter()
        return max(MIN_DELAY, min(MAX_DELAY, delay))

    def start(self, sample, offset):
        self.samples = [sample]
        self.started = True
        self.offset_scaled = offset << 3
        self.last_transit = -offset
        self.rendered = False
        self.last_render = 0

    def is_stale(self, server):
        if len(self.samples) == CAPACITY and server < self.samples[0][0]:
            return True
        if not self.rendered:
            return False
        for sample in reversed(self.samples):
            if sample[0] <= self.last_render:
                return server < sample[0]
        return False

    def add(self, sample, arrival):
        server = sample[0]
        offset = server - arrival
        if not self.started:
            self.start(sample, offset)
            return "Accepted"
        if any(existing[0] == server for existing in self.samples):
            return "Duplicate"
        deviation = offset - self.offset()
        if deviation > DISCONTINUITY or deviation < -DISCONTINUITY:
            if server <= self.samples[-1][0]:
                return "Stale"
            self.start(sample, offset)
            return "Reset"
        transit = -offset
        difference = min(abs(transit - self.last_transit), DISCONTINUITY)
        self.jitter_scaled += difference - ((self.jitter_scaled + 8) >> 4)
        self.last_transit = transit
        self.offset_scaled += offset - ((self.offset_scaled + 4) >> 3)
        if self.is_stale(server):
            return "Stale"
        newest = self.samples[-1][0]
        if server > newest:
            gap = min(server - newest, DISCONTINUITY)
            self.interval_scaled += gap - ((self.interval_scaled + 4) >> 3)
        if len(self.samples) == CAPACITY:
            self.samples.pop(0)
        position = len(self.samples)
        while position > 0 and self.samples[position - 1][0] > server:
            position -= 1
        self.samples.insert(position, sample)
        return "Accepted"

    def sample(self, now):
        if not self.samples:
            return None
        target = self.offset() - self.target_delay()
        if not self.rendered:
            self.playback = target
            self.last_local = now
        elif now > self.last_local:
            step = min(now - self.last_local, DISCONTINUITY) * PLAYBACK_SLEW_PERCENT // 100
            if self.playback < target:
                self.playback = min(target, self.playback + step)
            else:
                self.playback = max(target, self.playback - step)
            self.last_local = now
        render = now + self.playback
        if self.rendered and render < self.last_render:
            render = self.last_render
        if len(self.samples) == CAPACITY and render < self.samples[0][0]:
            render = self.samples[0][0]
        self.playback = render - now
        self.rendered = True
        self.last_render = render
        return (render,) + self.evaluate(render)

    def evaluate(self, render):
        oldest = self.samples[0]
        if render < oldest[0]:
            return "Holding", oldest[1]
        newest = self.samples[-1]
        if render >= newest[0]:
            ahead = render - newest[0]
            if ahead == 0:
                return "Interpolating", newest[1]
            seconds = min(ahead, EXTRAPOLATION_LIMIT) / 1_000_000.0
            mode = "ExtrapolationCapped" if ahead > EXTRAPOLATION_LIMIT else "Extrapolating"
            return mode, f32(newest[1] + self.extrapolation_velocity() * seconds)
        for index in range(len(self.samples) - 2, -1, -1):
            earlier = self.samples[index]
            if render < earlier[0]:
                continue
            later = self.samples[index + 1]
            t = (render - earlier[0]) / (later[0] - earlier[0])
            return "Interpolating", f32(earlier[1] + (later[1] - earlier[1]) * t)
        raise AssertionError("unordered samples")

    def extrapolation_velocity(self):
        newest = self.samples[-1]
        if newest[2] is not None:
            return newest[2]
        if len(self.samples) < 2:
            return 0.0
        previous = self.samples[-2]
        return (newest[1] - previous[1]) / ((newest[0] - previous[0]) / 1_000_000.0)


INTERPOLATION_START = 1_000_000
INTERPOLATION_FRAME = 33_333


def interpolation_scenario(name, arrivals, seconds, nominal=100_000):
    """arrivals: (server time, x, x velocity or None, local arrival), applied in this order before each frame."""
    buffer = InterpolationBuffer(nominal)
    pending = sorted(arrivals, key=lambda arrival: arrival[3])
    steps = []
    next_arrival = 0
    now = INTERPOLATION_START
    while now < INTERPOLATION_START + seconds * 1_000_000:
        while next_arrival < len(pending) and pending[next_arrival][3] <= now:
            server, x, velocity, arrival = pending[next_arrival]
            sample = (server, f32(x), None if velocity is None else f32(velocity))
            result = buffer.add(sample, arrival)
            steps.append(["add", server, arrival, hex32(sample[1]), None if velocity is None else hex32(sample[2]), result])
            next_arrival += 1
        out = buffer.sample(now)
        if out is None:
            steps.append(["frame", now, 0, "Empty", hex32(0.0), buffer.target_delay(), buffer.playback])
        else:
            render, mode, x = out
            steps.append(["frame", now, render, mode, hex32(x), buffer.target_delay(), buffer.playback])
        now += INTERPOLATION_FRAME
    return {
        "name": name,
        "nominalIntervalMicros": nominal,
        "steps": steps,
        "final": {"offset": buffer.offset(), "jitter": buffer.jitter(), "interval": buffer.interval(),
                  "largestGap": buffer.largest_gap()},
    }


def steady(index, late=0):
    server = INTERPOLATION_START + index * 100_000
    return server, float(index), 10.0, server + 50_000 + late


late_sample = [steady(index, 700_000 if index == 30 else 0) for index in range(60)]

uneven_scheduler = Scheduler(400)
uneven = []
for tick in range(60):
    if 1 in uneven_scheduler.schedule([candidate(entity, 1, 100) for entity in range(1, 11)]):
        server = INTERPOLATION_START + tick * 100_000
        uneven.append((server, server / 100_000, None, server + 50_000))

jitter_rng = random.Random(20261014)
rough = []
for index in range(40):
    server = INTERPOLATION_START + index * 100_000
    rough.append((server, float(index), None, server + 50_000 + jitter_rng.randrange(0, 120_000)))
# A duplicate; a resend older than the full buffer; and one so late that its offset jumps by over a second.
rough.append((INTERPOLATION_START + 10 * 100_000, 10.0, None, rough[10][3] + 30_000))
rough.append((INTERPOLATION_START + 14 * 100_000, 14.0, None, rough[23][3]))
rough.append((INTERPOLATION_START + 3 * 100_000, 3.0, None, rough[20][3]))
for index in range(40, 50):
    server = INTERPOLATION_START + 5_000_000 + index * 100_000
    rough.append((server, float(index), 2.0, server - 5_000_000 + 50_000 + jitter_rng.randrange(0, 20_000)))

write("interpolation.json", {
    "schema": 1,
    "about": "SnapshotInterpolationBuffer, replayed step by step: each add gives the sample's server time, local "
             "arrival, x and x velocity (float32 bits, velocity null when absent) and the result; each frame gives the "
             "local time, render time, mode, x, target delay and playback offset after the frame. Delay: the longer of "
             "the interval estimate and the largest gap between buffered samples, plus three jitters, clamped to "
             "80-350 ms. The playback offset (render time minus local time) moves toward offset minus delay by at most "
             "10% of elapsed local time, then the render time is floored at the last render time and, for a full "
             "buffer, at the oldest sample, and the playback offset follows the floored render time. Y and z are zero "
             "and rotations identity.",
    "capacity": CAPACITY,
    "scenarios": [
        interpolation_scenario("a late sample never pauses playback", late_sample, 5),
        interpolation_scenario("uneven cadence is covered by the delay", uneven, 5),
        interpolation_scenario("jitter, a duplicate, a stale sample and a timeline reset", rough, 6),
    ],
}, compact_depth=4)

# ---------------------------------------------------------------- motion ownership

RELEASE_WINDOW = 1_000_000
SILENCE_TIMEOUT = 1_000_000
SPEED_UNITS_PER_SECOND = 64 * POSITION_UNITS
SPEED_BURST = 250_000
TOLERANCE = 256
BURST_CREDIT = SPEED_UNITS_PER_SECOND * SPEED_BURST // 1_000_000 + TOLERANCE
HOLD_RANGE = 100 * POSITION_UNITS + TOLERANCE
FIRST_SAMPLE_TIMEOUT = 250_000
YIELD_COOLDOWN = 1_000_000
MAXIMUM_SPEED = 64
MAXIMUM_ANGULAR_SPEED = 64


def distance_squared(a, b):
    return sum((p - q) * (p - q) for p, q in zip(a, b))


def ceiling_sqrt(value):
    root = math.isqrt(value)
    return root if root * root == value else root + 1


def clamp_magnitude(vector, maximum):
    if not all(math.isfinite(component) for component in vector):
        return 0.0, 0.0, 0.0
    length_squared = (vector[0] * vector[0] + vector[1] * vector[1]) + vector[2] * vector[2]
    if length_squared <= float(maximum) * maximum:
        return vector
    scale = maximum / math.sqrt(length_squared)
    return tuple(f32(component * scale) for component in vector)


class OwnershipMachine:
    def __init__(self, epoch=0):
        self.state = "Server"
        self.owner = 0
        self.epoch = epoch
        self.last_validated = (0, 0, 0)
        self.last_stream = 0
        self.release_deadline = 0
        self.credit = 0
        self.last_validated_micros = 0
        self.awaiting = False
        self.first_deadline = 0
        self.yielding = 0
        self.yield_until = 0

    def report(self, result, reason="None", changed=False):
        return [result, reason, self.state, self.owner, self.epoch, changed]

    def in_range(self, body, avatar):
        return distance_squared(body, avatar) <= HOLD_RANGE * HOLD_RANGE

    def is_yielding(self, participant, now):
        return participant == self.yielding and participant != 0 and now < self.yield_until

    def request(self, participant, observed, body, avatar, now):
        if self.state == "Server":
            if observed != self.epoch:
                return self.report("StaleEpoch")
            if not self.in_range(body, avatar):
                return self.report("OutOfRange")
            if self.is_yielding(participant, now):
                return self.report("Yielding")
            return self.grant(participant, body, now)
        if self.state == "Owned":
            if self.owner != participant:
                return self.report("Denied")
            return self.report("AlreadyOwned" if observed == self.epoch else "StaleEpoch")
        if observed != self.epoch:
            return self.report("StaleEpoch")
        if not self.in_range(body, avatar):
            return self.report("OutOfRange")
        if self.owner != participant:
            return self.report("Yielding") if self.is_yielding(participant, now) else self.grant(participant, body, now)
        self.state = "Owned"
        self.last_stream = max(self.last_stream, now)
        return self.report("Granted")

    def check_sender(self, participant, epoch):
        if epoch != self.epoch:
            return "StaleEpoch"
        if self.state == "Server" or self.owner != participant:
            return "NotOwner"
        return None

    def stream(self, participant, epoch, position, holder, now):
        refusal = self.check_sender(participant, epoch)
        if refusal:
            return self.report(refusal)
        held = self.state == "Owned"
        if (held and not self.in_range(position, holder)) or not self.spend(position, now):
            return self.take_back("ValidationFailed", "Rejected")
        self.awaiting = False
        self.last_stream = max(self.last_stream, now)
        return self.report("Accepted")

    def release(self, participant, epoch, holder, now):
        refusal = self.check_sender(participant, epoch)
        if refusal:
            return self.report(refusal)
        if self.state == "ReleaseWindow":
            return self.report("Releasing")
        if not self.in_range(self.last_validated, holder):
            return self.take_back("ValidationFailed", "Rejected")
        self.state = "ReleaseWindow"
        self.release_deadline = now + RELEASE_WINDOW
        self.last_stream = max(self.last_stream, now)
        return self.report("Releasing")

    def handback(self, participant, epoch, position, linear, angular, holder, now):
        refusal = self.check_sender(participant, epoch)
        if refusal:
            return self.report(refusal), None
        held = self.state == "Owned"
        if (held and not self.in_range(position, holder)) or not self.spend(position, now):
            return self.take_back("ValidationFailed", "Rejected"), None
        accepted = clamp_magnitude(linear, MAXIMUM_SPEED), clamp_magnitude(angular, MAXIMUM_ANGULAR_SPEED)
        self.to_server()
        return self.report("HandedBack", "None", True), accepted

    def disconnect(self, participant):
        if self.yielding == participant:
            self.yielding = 0
            self.yield_until = 0
        if self.state == "Server" or self.owner != participant:
            return self.report("NoChange")
        return self.take_back("Disconnected", "Reclaimed")

    def reclaim(self):
        if self.state == "Server":
            return self.report("NoChange")
        return self.take_back("Host", "Reclaimed")

    def poll(self, now):
        if self.state == "Server":
            return self.report("NoChange")
        if self.awaiting and now > self.first_deadline:
            return self.lapse("NoFirstSample", now)
        if self.state == "ReleaseWindow" and now - self.release_deadline > SILENCE_TIMEOUT:
            return self.lapse("HandbackTimeout", now)
        if now - self.last_stream > SILENCE_TIMEOUT:
            return self.lapse("Silence", now)
        return self.report("NoChange")

    def grant(self, participant, body, now):
        self.epoch = (self.epoch + 1) & 0xFFFFFFFF
        self.owner = participant
        self.state = "Owned"
        self.last_validated = body
        self.last_stream = now
        self.last_validated_micros = now
        self.credit = BURST_CREDIT
        self.awaiting = True
        self.first_deadline = now + FIRST_SAMPLE_TIMEOUT
        if self.yielding == participant:
            self.yielding = 0
            self.yield_until = 0
        return self.report("Granted", "None", True)

    def spend(self, position, now):
        if now > self.last_validated_micros:
            elapsed = min(now - self.last_validated_micros, SPEED_BURST)
            self.credit = min(BURST_CREDIT, self.credit + SPEED_UNITS_PER_SECOND * elapsed // 1_000_000)
            self.last_validated_micros = now
        squared = distance_squared(self.last_validated, position)
        if squared > self.credit * self.credit:
            return False
        self.credit -= ceiling_sqrt(squared)
        self.last_validated = position
        return True

    def lapse(self, reason, now):
        participant = self.owner
        outcome = self.take_back(reason, "Reclaimed")
        self.yielding = participant
        self.yield_until = now + YIELD_COOLDOWN
        return outcome

    def take_back(self, reason, result):
        self.to_server()
        return self.report(result, reason, True)

    def to_server(self):
        self.epoch = (self.epoch + 1) & 0xFFFFFFFF
        self.owner = 0
        self.state = "Server"
        self.credit = 0
        self.awaiting = False


def ownership_scenario(name, calls, initial_epoch=0):
    machine = OwnershipMachine(initial_epoch)
    steps = []
    for call in calls:
        operation, arguments = call[0], call[1:]
        accepted = None
        if operation == "handback":
            participant, epoch, position, linear, angular, holder, now = arguments
            outcome, accepted = machine.handback(participant, epoch, tuple(position), tuple(f32(v) for v in linear),
                                                 tuple(f32(v) for v in angular), tuple(holder), now)
            arguments = [participant, epoch, position, [hex32(f32(v)) for v in linear],
                         [hex32(f32(v)) for v in angular], holder, now]
        elif operation in ("request", "stream"):
            participant, epoch, position, avatar, now = arguments
            outcome = getattr(machine, operation)(participant, epoch, tuple(position), tuple(avatar), now)
        elif operation == "release":
            participant, epoch, holder, now = arguments
            outcome = machine.release(participant, epoch, tuple(holder), now)
        elif operation == "disconnect":
            outcome = machine.disconnect(arguments[0])
        elif operation == "reclaim":
            outcome = machine.reclaim()
        else:
            outcome = machine.poll(arguments[0])
        step = {"call": [operation] + list(arguments),
                "outcome": outcome + [machine.credit, machine.yielding, machine.yield_until, machine.awaiting]}
        if accepted is not None:
            step["accepted"] = [[hex32(v) for v in accepted[0]], [hex32(v) for v in accepted[1]]]
        steps.append(step)
    return {"name": name, "initialEpoch": initial_epoch, "steps": steps}


O = [0, 0, 0]
R = HOLD_RANGE
FAR = [-80 * POSITION_UNITS, 0, 0]
hold_range = [
    ["request", 2, 0, O, [R + 1, 0, 0], 1_000],
    ["request", 2, 0, O, [30_876, -41_168, 0], 2_000],
    ["request", 2, 0, O, [30_870, -41_160, 0], 3_000],
    ["stream", 2, 1, [512, 0, 0], [R, 0, 0], 36_333],
    ["release", 2, 1, [-R, 0, 0], 40_000],
    ["request", 3, 2, [512, 0, 0], [512, 0, 0], 50_000],
    ["stream", 3, 3, [1_024, 0, 0], O, 83_333],
    ["release", 3, 3, O, 90_000],
    ["stream", 3, 3, [2_048, 0, 0], FAR, 120_000],
    ["release", 3, 3, FAR, 125_000],
    ["request", 2, 3, [2_048, 0, 0], [2_048 + R + 1, 0, 0], 130_000],
    ["request", 3, 3, [2_048, 0, 0], [2_048 - R - 1, 0, 0], 140_000],
    ["request", 2, 3, [2_048, 0, 0], [2_048, 0, 0], 150_000],
    ["stream", 2, 4, [2_560, 0, 0], [2_560 + R, 0, 0], 183_333],
    ["stream", 2, 4, [3_072, 0, 0], [3_072 + R + 1, 0, 0], 216_666],
]
quiet_owners = [
    ["request", 2, 0, O, O, 0],
    ["poll", 250_000],
    ["poll", 250_001],
    ["request", 2, 2, O, O, 300_000],
    ["request", 3, 2, O, O, 300_001],
    ["stream", 3, 3, O, O, 330_000],
    ["handback", 3, 3, O, [100.0, 0.0, 0.0], [0.0, 80.0, 60.0], O, 400_000],
    ["request", 2, 4, O, O, 1_250_000],
    ["request", 2, 4, O, O, 1_250_001],
    ["stream", 2, 5, O, O, 1_250_001],
    ["poll", 2_250_001],
    ["poll", 2_250_002],
    ["disconnect", 3],
    ["disconnect", 2],
    ["request", 2, 6, O, O, 2_300_000],
    ["stream", 2, 7, O, O, 2_300_000],
    ["release", 2, 7, O, 2_300_000],
    ["stream", 2, 7, [512, 0, 0], FAR, 2_333_333],
    ["stream", 2, 7, [512, 0, 0], FAR, 2_800_000],
    ["stream", 2, 7, [512, 0, 0], FAR, 3_300_000],
    ["stream", 2, 7, [512, 0, 0], FAR, 3_800_000],
    ["stream", 2, 7, [512, 0, 0], FAR, 4_300_000],
    ["poll", 4_300_001],
    ["request", 2, 8, [512, 0, 0], [512, 0, 0], 4_400_000],
    ["reclaim"],
    ["request", 3, 8, [512, 0, 0], [512, 0, 0], 4_400_001],
    ["reclaim"],
    ["request", 3, 10, [512, 0, 0], [512, 0, 0], 4_500_000],
    ["disconnect", 3],
]

ownership_rng = random.Random(97)
random_calls = []
shadow = OwnershipMachine()
clock = 0
for _ in range(300):
    clock += ownership_rng.randrange(0, 60_000)
    participant = ownership_rng.randrange(1, 4)
    epoch = (shadow.epoch - 1) & 0xFFFFFFFF if ownership_rng.randrange(4) == 0 else shadow.epoch
    position = [ownership_rng.randrange(-3 * POSITION_UNITS, 3 * POSITION_UNITS), 0, 0]
    avatar = [ownership_rng.randrange(-110 * POSITION_UNITS, 110 * POSITION_UNITS), 0, 0]
    kind = ownership_rng.randrange(7)
    if kind == 0:
        call = ["request", participant, epoch, position, avatar, clock]
        shadow.request(participant, epoch, tuple(position), tuple(avatar), clock)
    elif kind in (1, 2):
        call = ["stream", participant, epoch, position, avatar, clock]
        shadow.stream(participant, epoch, tuple(position), tuple(avatar), clock)
    elif kind == 3:
        call = ["release", participant, epoch, avatar, clock]
        shadow.release(participant, epoch, tuple(avatar), clock)
    elif kind == 4:
        call = ["handback", participant, epoch, position, [0.0, 0.0, 0.0], [0.0, 0.0, 0.0], avatar, clock]
        shadow.handback(participant, epoch, tuple(position), (0.0, 0.0, 0.0), (0.0, 0.0, 0.0), tuple(avatar), clock)
    elif kind == 5:
        call = ["disconnect", participant]
        shadow.disconnect(participant)
    else:
        call = ["poll", clock]
        shadow.poll(clock)
    random_calls.append(call)

write("ownership.json", {
    "schema": 1,
    "about": "OwnershipStateMachine with the default policy, replayed call by call. Positions are 1/512 m grid units "
             "and velocities float32 bits. Each outcome is the result, reclaim reason, state, owner slot (0 is the "
             "server), epoch and whether it changed, then the machine's distance credit, yielding participant, yield "
             "deadline and whether a first sample is still owed. A grab, steal or catch needs the requester's avatar "
             "within the hold range of the body; a throw needs the body's last validated position within it of the "
             "holder; a grant with no sample by its deadline, a silent owner and a missing hand-back are reclaimed and "
             "make that participant yield for the cooldown; a disconnect forgets its cooldown.",
    "policy": {"holdRangeUnits": HOLD_RANGE, "burstCreditUnits": BURST_CREDIT,
               "firstSampleTimeoutMicros": FIRST_SAMPLE_TIMEOUT, "yieldCooldownMicros": YIELD_COOLDOWN,
               "silenceTimeoutMicros": SILENCE_TIMEOUT, "releaseWindowMicros": RELEASE_WINDOW},
    "scenarios": [
        ownership_scenario("the hold range", hold_range),
        ownership_scenario("quiet owners yield", quiet_owners),
        ownership_scenario("seeded events", random_calls),
    ],
}, compact_depth=4)
