import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  test(
    'stopping with no owned process preserves a detached publisher session',
    () async {
      final root = await Directory.systemTemp.createTemp(
        'topiaforge-publisher-',
      );
      addTearDown(() async {
        if (await root.exists()) {
          await root.delete(recursive: true);
        }
      });
      final dataRoot = Directory(p.join(root.path, 'data'));
      final sessionFile = File(p.join(dataRoot.path, 'ugc-session.json'));
      await sessionFile.create(recursive: true);
      await sessionFile.writeAsString('{"leaseToken":"stale"}');
      final repository = LocalLauncherRepository(
        dataRoot: dataRoot.path,
        repositoryRoot: root.path,
      );

      await repository.stopUgcPublisher(waitForExit: true);

      expect(await sessionFile.exists(), isTrue);

      await repository.revokeUgcPublisherSession();

      expect(await sessionFile.exists(), isFalse);
    },
  );
}
