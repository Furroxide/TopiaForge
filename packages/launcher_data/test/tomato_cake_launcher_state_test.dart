import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'tomato_cake_launcher_fixture.dart';

void main() {
  late TomatoCakeFixture fixture;

  setUp(() => fixture = TomatoCakeFixture.create());
  tearDown(() => fixture.dispose());

  group('launcher-config.json', () {
    test('an absent record leaves the default layout alone', () async {
      final state = fixture.launcherState();

      expect(state.stateDirectory, fixture.stateDirectory.path);
      final config = await state.readConfig();
      expect(config.gameDirectory, isNull);
      expect(config.problem, isNull);
      expect(await state.relocatedGameRoot(), isNull);
      expect(
        await state.installedBuildMarkerFor(fixture.defaultGameRoot.path),
        isNull,
      );
    });

    test('a valid record names the canonical relocated game', () async {
      final parent = fixture.relocatedParent;
      fixture.relocatedGameRoot.createSync(recursive: true);
      fixture.writeConfig(
        jsonEncode({
          // A different spelling of the same directory, plus a trailing slash.
          'game_dir': '${fixture.alternateSpelling(parent)}${p.separator}',
          'future_setting': {'nested': true},
        }),
      );
      final state = fixture.launcherState();

      expect((await state.readConfig()).gameDirectory, parent.path);
      expect(await state.relocatedGameRoot(), fixture.relocatedGameRoot.path);
    });

    test(
      'a relocated parent without a Robotopia folder offers nothing',
      () async {
        fixture.relocatedParent.createSync(recursive: true);
        fixture.writeGameDirectory(fixture.relocatedParent.path);

        expect(await fixture.launcherState().relocatedGameRoot(), isNull);
      },
    );

    final malformed = <String, String Function(TomatoCakeFixture)>{
      'truncated JSON': (fixture) => '{"game_dir":',
      'a duplicate game_dir': (fixture) {
        final value = jsonEncode(fixture.relocatedParent.path);
        return '{"game_dir":$value,"game_dir":$value}';
      },
      'a duplicate nested property': (fixture) =>
          '{"game_dir":${jsonEncode(fixture.relocatedParent.path)},'
          '"extra":{"a":1,"a":2}}',
      'an array root': (fixture) => jsonEncode([fixture.relocatedParent.path]),
      'a missing game_dir': (fixture) => '{"gameDir":"ignored"}',
      'a numeric game_dir': (fixture) => '{"game_dir":7}',
      'an empty game_dir': (fixture) => '{"game_dir":""}',
      'a relative game_dir': (fixture) => '{"game_dir":"Games/Tomato Cake"}',
      'a dot-dot segment': (fixture) => jsonEncode({
        'game_dir': p.join(fixture.relocatedParent.path, '..', 'x'),
      }),
      'a padded game_dir': (fixture) =>
          jsonEncode({'game_dir': ' ${fixture.relocatedParent.path}'}),
      'a stale game_dir that no longer exists': (fixture) =>
          jsonEncode({'game_dir': p.join(fixture.root.path, 'moved-away')}),
    };
    for (final entry in malformed.entries) {
      test('refuses ${entry.key}', () async {
        fixture.relocatedGameRoot.createSync(recursive: true);
        fixture.writeConfig(entry.value(fixture));
        final state = fixture.launcherState();

        final config = await state.readConfig();
        expect(config.gameDirectory, isNull);
        expect(config.problem, isNotEmpty);
        expect(await state.relocatedGameRoot(), isNull);
        expect(
          await state.installedBuildMarkerFor(fixture.relocatedGameRoot.path),
          isNull,
        );
      });
    }

    test('refuses records that are not bounded strict UTF-8 files', () async {
      fixture.relocatedGameRoot.createSync(recursive: true);
      final valid = utf8.encode(
        jsonEncode({'game_dir': fixture.relocatedParent.path}),
      );
      final state = fixture.launcherState();
      Future<String?> problemFor(List<int> bytes) async {
        fixture.writeConfigBytes(bytes);
        return (await state.readConfig()).problem;
      }

      expect(await problemFor(valid), isNull);
      expect(
        await problemFor([0xef, 0xbb, 0xbf, ...valid]),
        contains('byte order mark'),
      );
      expect(await problemFor([...valid.take(3), 0xc3, 0x28]), isNotNull);
      expect(await problemFor(const []), contains('between 1 and'));
      expect(
        await problemFor([
          ...valid,
          ...List.filled(TomatoCakeLauncherState.maxConfigBytes, 0x20),
        ]),
        contains('between 1 and'),
      );

      File(fixture.configPath).deleteSync();
      Directory(fixture.configPath).createSync();
      expect((await state.readConfig()).problem, contains('regular file'));
    });

    test('refuses a game_dir that passes through a link', () async {
      final real = Directory(p.join(fixture.root.path, 'real'))..createSync();
      Directory(p.join(real.path, 'Robotopia')).createSync();
      final alias = p.join(fixture.root.path, 'alias');
      linkDirectory(alias, real.path);
      final state = fixture.launcherState();

      fixture.writeGameDirectory(alias);
      expect((await state.readConfig()).problem, contains('link'));
      expect(await state.relocatedGameRoot(), isNull);

      Directory(p.join(real.path, 'nested')).createSync();
      fixture.writeGameDirectory(p.join(alias, 'nested'));
      expect((await state.readConfig()).problem, contains('link'));
    });

    test('never offers a Robotopia folder that is itself a link', () async {
      final elsewhere = Directory(p.join(fixture.root.path, 'elsewhere'))
        ..createSync();
      fixture.relocatedParent.createSync(recursive: true);
      linkDirectory(fixture.relocatedGameRoot.path, elsewhere.path);
      fixture.writeGameDirectory(fixture.relocatedParent.path);
      final state = fixture.launcherState();

      expect(
        (await state.readConfig()).gameDirectory,
        fixture.relocatedParent.path,
      );
      expect(await state.relocatedGameRoot(), isNull);
      expect(
        await state.installedBuildMarkerFor(fixture.relocatedGameRoot.path),
        isNull,
      );
    });

    test(
      'only a Windows host with an absolute LOCALAPPDATA reads it',
      () async {
        fixture.relocatedGameRoot.createSync(recursive: true);
        fixture.writeGameDirectory(fixture.relocatedParent.path);

        for (final host in ['macos', 'linux']) {
          final state = fixture.launcherState(hostPlatform: host);
          expect(state.stateDirectory, isNull, reason: host);
          expect((await state.readConfig()).problem, isNull, reason: host);
          expect(await state.relocatedGameRoot(), isNull, reason: host);
        }
        final relative = TomatoCakeLauncherState(
          environment: const {'LOCALAPPDATA': 'AppData'},
          hostPlatform: 'windows',
        );
        expect(relative.stateDirectory, isNull);
      },
    );
  });

  group('installed-build marker attribution', () {
    test('applies the state marker only to <game_dir>/Robotopia', () async {
      fixture.relocatedGameRoot.createSync(recursive: true);
      final sibling = Directory(
        p.join(fixture.relocatedParent.path, 'Robotopia Backup'),
      )..createSync();
      final otherRoot = Directory(
        p.join(fixture.root.path, 'other', 'Robotopia'),
      )..createSync(recursive: true);
      fixture.defaultGameRoot.createSync(recursive: true);
      fixture.writeGameDirectory(fixture.relocatedParent.path);
      final state = fixture.launcherState();
      final marker = p.join(
        fixture.stateDirectory.path,
        TomatoCakeLauncherState.installedBuildFileName,
      );

      expect(
        (await state.installedBuildMarkerFor(
          fixture.relocatedGameRoot.path,
        ))?.path,
        marker,
      );
      expect(
        (await state.installedBuildMarkerFor(
          fixture.alternateSpelling(fixture.relocatedGameRoot),
        ))?.path,
        marker,
      );
      for (final unrelated in [sibling, otherRoot, fixture.defaultGameRoot]) {
        expect(
          await state.installedBuildMarkerFor(unrelated.path),
          isNull,
          reason: unrelated.path,
        );
      }
      expect(
        await state.installedBuildMarkerFor(
          p.join(fixture.relocatedParent.path, 'missing', 'Robotopia'),
        ),
        isNull,
      );
    });
  });

  group('game_dir shape', () {
    String? windows(String value) =>
        TomatoCakeLauncherState.gameDirectoryProblem(value, windowsPaths: true);
    String? posix(String value) => TomatoCakeLauncherState.gameDirectoryProblem(
      value,
      windowsPaths: false,
    );

    test('accepts absolute local drive paths on Windows', () {
      for (final value in [
        r'D:\Users\player\Local\Tomato Cake\launcher',
        r'd:/Games/Tomato Cake/',
        r'D:\',
      ]) {
        expect(windows(value), isNull, reason: value);
      }
    });

    test('refuses network, device, relative and ambiguous Windows paths', () {
      for (final value in [
        r'\\server\share\Games',
        r'//server/share/Games',
        r'\\?\D:\Games',
        r'\\.\D:\Games',
        r'D:Games',
        r'\Games',
        r'Games\Tomato Cake',
        r'D:\Games\..\Other',
        r'D:\Games\.\Tomato Cake',
        r'D:\Games\\Tomato Cake',
        r'D:\Games:stream',
        r'D:\Games\Tomato*',
        r'D:\Games.\Tomato Cake',
        r'D:\Games \Tomato Cake',
        ' D:\\Games',
        'D:\\Games\u0001',
        '',
      ]) {
        expect(windows(value), isNotNull, reason: value);
      }
    });

    test('applies the same segment rules to POSIX paths', () {
      for (final value in ['/home/player/Games', '/', '/srv/games/']) {
        expect(posix(value), isNull, reason: value);
      }
      for (final value in [
        'Games',
        r'D:\Games',
        '/home/../etc',
        '/home/./player',
        '//server/share',
        '/home//player',
        '',
      ]) {
        expect(posix(value), isNotNull, reason: value);
      }
    });
  });
}
