import 'dart:convert';
import 'authored_map.dart';

import 'strict_map_json.dart';

/// Public map references never accept an arbitrary download URL.
class MapReference {
  MapReference._(this.id);
  final String id;
  static final _id = RegExp(r'^[A-Za-z0-9_-]{1,64}$');
  static bool isId(String value, {int maximum = 128}) =>
      value.isNotEmpty &&
      value.length <= maximum &&
      RegExp(r'^[A-Za-z0-9_-]+$').hasMatch(value);

  factory MapReference.parse(String value) {
    if (_id.hasMatch(value)) return MapReference._(value);
    final uri = Uri.tryParse(value);
    final segments = uri?.pathSegments.toList() ?? <String>[];
    if (segments.lastOrNull == '') segments.removeLast();
    if (uri == null ||
        uri.scheme != 'https' ||
        uri.host != 'robotopia.gg' ||
        uri.port != 443 ||
        uri.userInfo.isNotEmpty ||
        uri.hasQuery ||
        uri.hasFragment ||
        segments.length != 2 ||
        segments[0] != 'map' ||
        !_id.hasMatch(segments[1]) ||
        value.contains('%')) {
      throw const FormatException(
        'Use a robotopia.gg map page link or its map id.',
      );
    }
    return MapReference._(segments[1]);
  }
  Uri get page => Uri.https('robotopia.gg', '/map/$id/');
}

/// Unity-free bounded project survey, validated before native parsing or authoring.
class MapWorldDocument {
  MapWorldDocument._(this.name, this.modified, this.scenes);
  static const maximumPackedBytes = 8 * 1024 * 1024;
  static const maximumJsonBytes = 32 * 1024 * 1024;
  static const maximumEntities = 50000;
  final String name;
  final String modified;
  final Map<String, MapWorldScene> scenes;

  factory MapWorldDocument.read(List<int> jsonBytes) {
    if (jsonBytes.isEmpty || jsonBytes.length > maximumJsonBytes) {
      throw const FormatException(
        'The map is empty or exceeds the 32 MiB expanded limit.',
      );
    }
    try {
      final text = utf8.decode(jsonBytes, allowMalformed: false);
      final raw = decodeStrictMapJson(text);
      final project = _object(raw);
      final scenes = _object(project['scenes']);
      if (scenes.isEmpty || scenes.length > 256) throw const FormatException();
      final result = <String, MapWorldScene>{};
      var count = 0;
      for (final entry in scenes.entries) {
        final scene = _object(entry.value);
        _identity(entry.key, scene);
        final entities = _object(scene['entities']);
        count += entities.length;
        if (count > maximumEntities) throw const FormatException();
        final parents = <String, String>{};
        final spawns = <String, String>{};
        for (final entity in entities.entries) {
          final data = _object(entity.value);
          _identity(entity.key, data);
          parents[entity.key] = _text(data, 'parent', 128);
          final components = _object(data['components']);
          if (components['spawn-location'] != null) {
            _object(components['spawn-location']);
            spawns[entity.key] = _text(data, 'name', 256);
          }
        }
        for (final entity in parents.entries) {
          var parent = entity.value;
          final seen = {entity.key};
          while (parent.isNotEmpty) {
            if (seen.length > 128 ||
                !seen.add(parent) ||
                !parents.containsKey(parent)) {
              throw const FormatException();
            }
            parent = parents[parent]!;
          }
        }
        result[entry.key] = MapWorldScene(
          entry.key,
          _text(scene, 'name', 128),
          Map.unmodifiable(entities),
          Map.unmodifiable(spawns),
          environment: MapEditorVector.text(scene, 'environment', 128),
        );
      }
      return MapWorldDocument._(
        _text(project, 'name', 256),
        _text(project, 'modified', 128),
        Map.unmodifiable(result),
      );
    } on FormatException {
      throw const FormatException(
        'The map has invalid or overly complex JSON, scenes, entities or parent links.',
      );
    }
  }

  MapWorldScene select(String? id) {
    if (id == null) {
      if (scenes.length == 1) return scenes.values.single;
      throw StateError(
        'The map has several scenes. Choose one with --scene: ${scenes.keys.join(', ')}.',
      );
    }
    return scenes[id] ??
        (throw StateError(
          'The map no longer contains the selected scene. Ask its creator for an update.',
        ));
  }

  static Map<String, dynamic> _object(Object? value) =>
      value is Map<String, dynamic> ? value : throw const FormatException();
  static String _text(Map<String, dynamic> value, String key, int maximum) {
    final raw = value[key];
    if (raw == null) return '';
    if (raw is! String ||
        raw.length > maximum ||
        raw.codeUnits.any((c) => c < 32 || c >= 127 && c <= 159)) {
      throw const FormatException();
    }
    return raw;
  }

  static void _identity(String key, Map<String, dynamic> value) {
    final id = _text(value, 'id', 128);
    if (!MapReference.isId(key) || (id.isNotEmpty && id != key)) {
      throw const FormatException();
    }
  }
}

class MapWorldScene {
  MapWorldScene(
    this.id,
    this.name,
    this.entities,
    this.spawns, {
    this.environment = '',
  }) : authored = MapAuthoredScene.read(id, entities);
  final String id;
  final String name;
  final String environment;
  final MapAuthoredScene authored;
  final Map<String, dynamic> entities;
  final Map<String, String> spawns;
  void requireSpawn([String? marker]) {
    final matches = marker == null
        ? spawns.values
        : spawns.values.where((name) => name == marker);
    if (matches.length != 1) {
      throw StateError(
        matches.isEmpty
            ? 'The map has no matching spawn location. Add one in the editor.'
            : 'The map has several spawn locations. Give one a unique editor name and set spawn.markerName.',
      );
    }
  }
}
