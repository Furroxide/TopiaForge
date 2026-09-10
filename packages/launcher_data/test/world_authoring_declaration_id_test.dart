import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory root;
  late LocalDeveloperRepository repository;
  setUp(() {
    root = Directory.systemTemp.createTempSync('declaration-authoring-');
    repository = LocalDeveloperRepository(dataRoot: p.join(root.path, 'data'));
  });
  tearDown(() => root.deleteSync(recursive: true));

  for (final length in [65, 96]) {
    test(
      'paired world IDs allow $length characters on read and write',
      () async {
        final id = 'mode.${'x' * (length - 5)}';
        final config = WorldAuthoringConfig(worldId: id);
        await repository.writeWorldAuthoringConfig(root.path, config);
        expect(
          (await repository.readWorldAuthoringConfig(root.path))!.worldId,
          id,
        );
      },
    );
  }

  for (final id in ['mode.${'x' * 92}', 'mode.é']) {
    test('invalid paired ID $id fails without writing', () async {
      final config = WorldAuthoringConfig(worldId: id);
      final file = File(p.join(root.path, WorldAuthoringConfig.fileName));
      await expectLater(
        repository.writeWorldAuthoringConfig(root.path, config),
        throwsFormatException,
      );
      expect(file.existsSync(), isFalse);
      file.writeAsStringSync(jsonEncode(config.toJson()));
      await expectLater(
        repository.readWorldAuthoringConfig(root.path),
        throwsFormatException,
      );
    });
  }
}
