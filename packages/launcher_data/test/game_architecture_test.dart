import 'dart:io';
import 'dart:typed_data';

import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  for (final fixture in const <(int, String)>[
    (0x8664, 'x64'),
    (0xaa64, 'arm64'),
  ]) {
    test(
      'game compatibility uses ${fixture.$2} from the PE executable',
      () async {
        final root = Directory.systemTemp.createTempSync('game-architecture-');
        addTearDown(() => root.deleteSync(recursive: true));
        final game = Directory(p.join(root.path, 'Robotopia'))..createSync();
        _writePe(File(p.join(game.path, 'Robotopia.exe')), fixture.$1);
        Directory(
          p.join(game.path, 'Robotopia_Data', 'Managed'),
        ).createSync(recursive: true);
        File(
          p.join(game.path, 'Robotopia_Data', 'Managed', 'UnityEngine.dll'),
        ).writeAsBytesSync(const [0]);
        final repository = LocalLauncherRepository(
          dataRoot: p.join(root.path, 'data'),
          repositoryRoot: root.path,
        );
        addTearDown(repository.dispose);

        final install = await repository.selectGameDirectory(game.path);

        expect(install.architecture, fixture.$2);
      },
    );
  }

  test(
    'unknown executable architecture remains unknown and fails closed',
    () async {
      final root = Directory.systemTemp.createTempSync(
        'game-architecture-bad-',
      );
      addTearDown(() => root.deleteSync(recursive: true));
      final game = Directory(p.join(root.path, 'Robotopia'))..createSync();
      File(p.join(game.path, 'Robotopia.exe')).writeAsBytesSync(const [0, 1]);
      Directory(
        p.join(game.path, 'Robotopia_Data', 'Managed'),
      ).createSync(recursive: true);
      File(
        p.join(game.path, 'Robotopia_Data', 'Managed', 'UnityEngine.dll'),
      ).writeAsBytesSync(const [0]);
      final repository = LocalLauncherRepository(
        dataRoot: p.join(root.path, 'data'),
        repositoryRoot: root.path,
      );
      addTearDown(repository.dispose);

      final install = await repository.selectGameDirectory(game.path);

      expect(install.architecture, isEmpty);
    },
  );
}

void _writePe(File file, int machine) {
  final bytes = Uint8List(0x86);
  bytes[0] = 0x4d;
  bytes[1] = 0x5a;
  final data = ByteData.sublistView(bytes);
  data.setUint32(0x3c, 0x80, Endian.little);
  bytes[0x80] = 0x50;
  bytes[0x81] = 0x45;
  data.setUint16(0x84, machine, Endian.little);
  file.writeAsBytesSync(bytes, flush: true);
}
