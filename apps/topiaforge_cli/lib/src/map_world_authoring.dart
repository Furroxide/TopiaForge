import 'dart:convert';
import 'dart:io';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;

/// Pure manifest plan. Existing extension fields survive; ambiguous targets need
/// an explicit selection rather than silently changing a different game.
Map<String, dynamic> addMapWorld(
  Map<String, dynamic> source, {
  required MapReference reference,
  required String scene,
  required String name,
  String? worldId,
  String? targetId,
  String? markerName,
}) {
  if (source['schemaVersion'] != 6) {
    throw StateError(
      'Migrate the manifest to schema 6 before adding a map world.',
    );
  }
  if (!MapReference.isId(scene) || name.trim().isEmpty || name.length > 256) {
    throw StateError(
      'Supply a scene id and a display name of at most 256 characters.',
    );
  }
  final result = jsonDecode(jsonEncode(source)) as Map<String, dynamic>;
  final package = result['name'] as String;
  final id = worldId ?? '$package.map';
  final contributions =
      (result['contributions'] ??= <String, dynamic>{}) as Map<String, dynamic>;
  final worlds = (contributions['worlds'] ??= <dynamic>[]) as List;
  if (worlds.any((world) => (world as Map)['id'] == id)) {
    throw StateError(
      'That world id already exists. Choose a new --world id or edit its declaration.',
    );
  }
  final modes = (contributions['gamemodes'] ?? const <dynamic>[]) as List;
  final targets = (contributions['launchTargets'] ??= <dynamic>[]) as List;
  final candidates = targets
      .where((target) => targetId == null || (target as Map)['id'] == targetId)
      .toList();
  Map target;
  if (candidates.length == 1) {
    target = candidates.single as Map;
  } else if (targets.isEmpty && modes.length == 1 && targetId == null) {
    target = <String, dynamic>{
      'id': '$package.play',
      'title': result['displayName'] ?? name,
      'gamemode': (modes.single as Map)['id'],
    };
    targets.add(target);
  } else {
    throw StateError(
      'Choose one launch target with --target, or add one gamemode before adding its map world.',
    );
  }
  final selectedModes = modes
      .where((mode) => (mode as Map)['id'] == target['gamemode'])
      .toList();
  if (selectedModes.length != 1) {
    throw StateError(
      'The selected target must use a gamemode declared by this package.',
    );
  }
  final mode = selectedModes.single as Map;
  final requirements =
      (mode['worldRequirements'] ??= <String, dynamic>{}) as Map;
  requirements['transitions'] = ['scene-replacement'];
  worlds.add({
    'id': id,
    'name': name,
    'content': {'kind': 'map', 'map': reference.id, 'scene': scene},
    if (markerName != null)
      'spawn': {'kind': 'authored-marker', 'markerName': markerName},
  });
  target['world'] = {'policy': 'fixed', 'default': id};
  target['transition'] = 'scene-replacement';
  if (target['description'] == 'Play the custom gamemode in Open Sandbox.') {
    target['description'] = 'Play the custom gamemode in $name.';
  }
  final dependencies = (result['dependencies'] ??= <String, dynamic>{}) as Map;
  dependencies['io.github.furroxide.topiaforge.worlds'] = _floor(
    dependencies['io.github.furroxide.topiaforge.worlds'],
  );
  result['supportedLoaderVersionRange'] = _floor(
    result['supportedLoaderVersionRange'],
  );
  result['supportedSdkVersionRange'] = _floor(
    result['supportedSdkVersionRange'],
  );
  final manifest = ModManifest.fromJson(result);
  final issues = [
    ...manifest.validate(),
    ...ManifestFeatureRequirements.current.validate(manifest),
  ].where((issue) => issue.isBlocking).toList();
  if (issues.isNotEmpty) {
    throw StateError(
      'The map declaration is not valid: ${issues.map((issue) => issue.message).join(' ')}',
    );
  }
  return result;
}

String _floor(Object? raw) {
  if (raw == null) return '>=0.2.0 <0.3.0';
  if (raw is! String) throw StateError('A version range must be text.');
  final range = VersionRange.parse(raw);
  final minimum = SemanticVersion.parse('0.2.0');
  if (range.min != null && range.min!.compareTo(minimum) >= 0) return raw;
  // A scaffold's 0.1 ceiling must advance when it adopts a 0.2 feature.
  final upper = range.max;
  return '>=0.2.0 ${upper != null && upper.compareTo(minimum) > 0 ? '${range.includeMax ? '<=' : '<'}$upper' : '<0.3.0'}';
}

/// Compare source bytes again at replacement; never follow a linked project path.
Future<void> writeMapManifest(
  File file,
  String original,
  Map<String, dynamic> value,
) async {
  ordinaryMapPath(file.path);
  final output = '${const JsonEncoder.withIndent('  ').convert(value)}\n';
  if (utf8.encode(output).length > 1024 * 1024) {
    throw StateError('The manifest exceeds the 1 MiB limit.');
  }
  final stage = File(
    '${file.path}.map-$pid-${DateTime.now().microsecondsSinceEpoch}.tmp',
  );
  try {
    await stage.create(exclusive: true);
    await stage.writeAsString(output, flush: true);
    ordinaryMapPath(file.path);
    if (await file.readAsString() != original) {
      throw StateError(
        'The manifest changed during map authoring. Run the command again.',
      );
    }
    await stage.rename(file.path);
  } finally {
    if (await stage.exists()) await stage.delete();
  }
}

void ordinaryMapPath(String path) {
  for (var current = p.absolute(path); ;) {
    if (FileSystemEntity.typeSync(current, followLinks: false) ==
        FileSystemEntityType.link) {
      throw StateError(
        'Map authoring requires an ordinary project path, without links.',
      );
    }
    final parent = p.dirname(current);
    if (parent == current) break;
    current = parent;
  }
}
