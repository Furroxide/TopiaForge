import 'dart:math' as math;
import 'dart:typed_data';

/// Editor coordinates, before the game's axis/root conversion.
class MapEditorVector {
  const MapEditorVector(this.x, this.y, this.z);
  final double x, y, z;
  static const zero = MapEditorVector(0, 0, 0);
  static const one = MapEditorVector(1, 1, 1);
  double distance(MapEditorVector other) => math.sqrt(
    math.pow(x - other.x, 2) +
        math.pow(y - other.y, 2) +
        math.pow(z - other.z, 2),
  );
  Map<String, double> toJson() => {'x': x, 'y': y, 'z': z};
  static MapEditorVector read(
    Object? raw, {
    MapEditorVector fallback = zero,
    bool four = false,
  }) {
    if (raw == null) return fallback;
    if (raw is List && (raw.length == 3 || four && raw.length == 4)) {
      return MapEditorVector(number(raw[0]), number(raw[1]), number(raw[2]));
    }
    final data = object(raw);
    return MapEditorVector(
      number(data['x'] ?? 0),
      number(data['y'] ?? 0),
      number(data['z'] ?? 0),
    );
  }

  static double number(Object? raw) {
    if (raw is! num || !raw.isFinite || raw.abs() > 3.4028234663852886e38) {
      throw const FormatException('Invalid map coordinate.');
    }
    // The runtime stores editor values as Single; round before distance tests too.
    final bytes = ByteData(4)..setFloat32(0, raw.toDouble());
    return bytes.getFloat32(0);
  }

  static Map<String, dynamic> object(Object? raw) => raw is Map<String, dynamic>
      ? raw
      : throw const FormatException('Invalid authored map object.');
  static String text(Map<String, dynamic> data, String field, int maximum) {
    final raw = data[field];
    if (raw == null) return '';
    if (raw is! String ||
        raw.length > maximum ||
        raw.codeUnits.any((c) => c < 32 || c >= 127 && c <= 159)) {
      throw FormatException('Invalid map field: $field.');
    }
    return raw;
  }

  static bool isId(String value, [int maximum = 64]) =>
      value.isNotEmpty &&
      value.length <= maximum &&
      RegExp(r'^[A-Za-z0-9_-]+$').hasMatch(value);
}

class MapEditorTransform {
  MapEditorTransform(this.position, this.rotation, this.w, this.scale);
  final MapEditorVector position, rotation, scale;

  /// Null preserves Euler degrees; a value preserves quaternion rotation.
  final double? w;
  factory MapEditorTransform.read(Object? raw) {
    final data = raw == null
        ? <String, dynamic>{}
        : MapEditorVector.object(raw);
    final rotation = data['rotation'];
    final xyz = MapEditorVector.read(rotation, four: true);
    final rawW = rotation is List && rotation.length == 4
        ? rotation[3]
        : rotation is Map
        ? rotation['w']
        : null;
    final w = rawW == null ? null : MapEditorVector.number(rawW);
    if (w != null &&
        xyz.x * xyz.x + xyz.y * xyz.y + xyz.z * xyz.z + w * w <= 0.000001) {
      throw const FormatException('Invalid map rotation.');
    }
    return MapEditorTransform(
      MapEditorVector.read(data['position']),
      xyz,
      w,
      MapEditorVector.read(data['scale'], fallback: MapEditorVector.one),
    );
  }
}

class MapAuthoredEntity {
  MapAuthoredEntity(
    this.id,
    this.sceneId,
    this.name,
    this.parentId,
    this.modelId,
    this.prefabUri,
    this.transform,
    this.components,
  );
  final String id, sceneId, name;
  final String? parentId, modelId, prefabUri;
  final MapEditorTransform transform;

  /// Same flag values as the runtime SDK's AuthoredComponents.
  final int components;
  Map<String, Object?> toSurveyRecord() => {
    'id': id,
    'name': name,
    'parentId': parentId,
    'modelId': modelId,
    'prefabUri': prefabUri,
    'components': components,
    'editorPosition': transform.position.toJson(),
  };
}

/// The same bounded, parent-first authored records used by the runtime index.
class MapAuthoredScene {
  MapAuthoredScene._(this.entities);
  final List<MapAuthoredEntity> entities;
  factory MapAuthoredScene.read(String sceneId, Map<String, dynamic> raw) {
    if (raw.length > 50000) {
      throw const FormatException('Too many map entities.');
    }
    final records = <String, MapAuthoredEntity>{};
    for (final item in raw.entries) {
      final data = MapEditorVector.object(item.value);
      final id = MapEditorVector.text(data, 'id', 64);
      if (!MapEditorVector.isId(item.key) || id.isNotEmpty && id != item.key) {
        throw const FormatException('Invalid map entity id.');
      }
      final parent = MapEditorVector.text(data, 'parent', 64);
      if (parent.isNotEmpty && !MapEditorVector.isId(parent)) {
        throw const FormatException('Invalid parent id.');
      }
      final components = MapEditorVector.object(data['components']);
      var flags = 0;
      String? model, prefab;
      const bits = {
        'model-renderer': 1,
        'prefab-instance': 2,
        'spawn-location': 4,
        'poi': 8,
        'aoi': 16,
        'agent': 32,
      };
      for (final component in components.entries) {
        if (component.value == null) continue;
        final value = MapEditorVector.object(component.value);
        flags |= bits[component.key] ?? 0;
        if (component.key == 'model-renderer') {
          model = MapEditorVector.text(value, 'model', 256);
        }
        if (component.key == 'prefab-instance') {
          prefab = MapEditorVector.text(value, 'uri', 512);
        }
      }
      records[item.key] = MapAuthoredEntity(
        item.key,
        sceneId,
        MapEditorVector.text(data, 'name', 128),
        parent.isEmpty ? null : parent,
        model == '' ? null : model,
        prefab == '' ? null : prefab,
        MapEditorTransform.read(components['transform']),
        flags,
      );
    }
    final ordered = <MapAuthoredEntity>[];
    final visited = <String>{}, visiting = <String>{};
    final parentDepth = <String, int>{};
    void visit(MapAuthoredEntity entity, int depth) {
      if (visited.contains(entity.id)) return;
      if (depth > 128 || !visiting.add(entity.id)) {
        throw const FormatException('Invalid map parent links.');
      }
      var height = 0;
      if (entity.parentId != null) {
        final parent = records[entity.parentId];
        if (parent == null) {
          throw const FormatException('Invalid map parent links.');
        }
        visit(parent, depth + 1);
        height = parentDepth[parent.id]! + 1;
        if (height > 128) {
          throw const FormatException('Invalid map parent links.');
        }
      }
      visiting.remove(entity.id);
      parentDepth[entity.id] = height;
      visited.add(entity.id);
      ordered.add(entity);
    }

    for (final key in records.keys.toList()..sort()) {
      visit(records[key]!, 0);
    }
    return MapAuthoredScene._(List.unmodifiable(ordered));
  }
}
