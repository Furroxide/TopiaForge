import 'dart:convert';

import 'json_duplicate_properties.dart';
import 'map_world_document.dart';

/// One explicitly saved scene from a public map. Listing never contacts a server.
final class PublishedMapSource {
  PublishedMapSource({
    required this.map,
    required this.scene,
    required this.name,
  }) {
    if (!MapReference.isId(map, maximum: 64) ||
        !MapReference.isId(scene) ||
        name.trim().isEmpty ||
        name.length > 128 ||
        name.codeUnits.any((c) => c < 32 || c >= 127 && c <= 159) ||
        utf8.decode(utf8.encode(name)) != name) {
      throw const FormatException(
        'Use a valid map id, explicit scene id and a name of at most 128 characters.',
      );
    }
  }
  factory PublishedMapSource.fromReference({
    required String reference,
    required String scene,
    required String name,
  }) => PublishedMapSource(
    map: MapReference.parse(reference.trim()).id,
    scene: scene.trim(),
    name: name.trim(),
  );
  final String map, scene, name;
  String get key => '$map\n$scene';
  Map<String, Object?> toJson() => {'map': map, 'scene': scene, 'name': name};
}

abstract final class PublishedMapSources {
  static const maximumBytes = 1024 * 1024;
  static const maximumSources = 4096;
  static List<PublishedMapSource> read(List<int> bytes) {
    try {
      if (bytes.isEmpty || bytes.length > maximumBytes) {
        throw const FormatException();
      }
      final text = utf8.decode(bytes);
      rejectDuplicateJsonProperties(text, label: 'Saved map list');
      final root = jsonDecode(text);
      if (root is! Map ||
          root.length != 2 ||
          root['schemaVersion'] != 1 ||
          root['sources'] is! List) {
        throw const FormatException();
      }
      final rows = root['sources'] as List;
      if (rows.length > maximumSources) throw const FormatException();
      final sources = <PublishedMapSource>[];
      final keys = <String>{};
      for (final row in rows) {
        if (row is! Map ||
            row.length != 3 ||
            row['map'] is! String ||
            row['scene'] is! String ||
            row['name'] is! String) {
          throw const FormatException();
        }
        final source = PublishedMapSource(
          map: row['map'],
          scene: row['scene'],
          name: row['name'],
        );
        if (!keys.add(source.key)) throw const FormatException();
        sources.add(source);
      }
      return List.unmodifiable(sources);
    } on FormatException {
      throw const FormatException(
        'The saved map list is invalid. Repair it in launcher Setup or with world source.',
      );
    }
  }

  static List<int> write(List<PublishedMapSource> sources) {
    final bytes = utf8.encode(
      '${jsonEncode({'schemaVersion': 1, 'sources': sources.map((s) => s.toJson()).toList()})}\n',
    );
    read(bytes); // One validation contract for stored and projected lists.
    return bytes;
  }
}

/// Optional launcher repository capability; existing hosts need not implement it.
abstract interface class PublishedMapSourceRepository {
  Future<List<PublishedMapSource>> loadPublishedMapSources();
  Future<List<PublishedMapSource>> savePublishedMapSource(
    PublishedMapSource source,
  );
  Future<List<PublishedMapSource>> removePublishedMapSource(
    String map,
    String scene,
  );
}
