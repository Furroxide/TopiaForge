import 'dart:convert';
import 'dart:io';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  test('UGC Go Live runtime config matches the game-side payload fixture', () {
    final payload = const UgcLiveSyncSettings(
      transport: 'automerge',
      watchFolder: r'C:\Robotopia\ugc-watch',
      documentUrl: 'automerge:captured-doc',
      sceneId: 'neon-rooftops',
      autoConnectOnStart: true,
      maxSnapshotBytes: 4194304,
      debounceMilliseconds: 350,
    ).toRuntimeConfig();

    expect(
      payload,
      equals(
        _readJsonFixture(
          'tests/fixtures/ugc/live-sync-app-automerge-config.json',
        ),
      ),
    );
  });
}

Map<String, Object?> _readJsonFixture(String relativePath) {
  var dir = Directory.current.absolute;
  while (true) {
    final file = File('${dir.path}/$relativePath');
    if (file.existsSync()) {
      return (jsonDecode(file.readAsStringSync()) as Map)
          .cast<String, Object?>();
    }

    final parent = dir.parent;
    if (parent.path == dir.path) {
      throw StateError(
        'Could not find fixture $relativePath from ${Directory.current.path}.',
      );
    }

    dir = parent;
  }
}
