import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory root;
  late Directory dataRoot;
  late LocalLauncherRepository repository;

  setUp(() async {
    root = await Directory.systemTemp.createTemp(
      'topiaforge-persistence-test-',
    );
    dataRoot = Directory(p.join(root.path, 'data'));
    repository = LocalLauncherRepository(
      dataRoot: dataRoot.path,
      repositoryRoot: root.path,
      knownGamePath: p.join(root.path, 'missing-game'),
    );
  });

  tearDown(() async {
    await repository.dispose();
    if (await root.exists()) {
      await root.delete(recursive: true);
    }
  });

  test('concurrent setting mutations preserve independent fields', () async {
    final profile = LauncherProfile.defaultProfile().copyWith(id: 'selected');

    await Future.wait([
      repository.setDeveloperMode(true),
      repository.saveLauncherUpdateSettings(
        const LauncherUpdateSettings(
          enabled: false,
          checkAutomatically: false,
          channel: LauncherUpdateChannel.nightly,
        ),
      ),
      repository.saveProfiles([profile], profile.id),
    ]);

    final decoded =
        jsonDecode(
              await File(p.join(dataRoot.path, 'settings.json')).readAsString(),
            )
            as Map<String, Object?>;
    final updates = decoded['launcherUpdates'] as Map<String, Object?>;
    expect(decoded['developerMode'], isTrue);
    expect(decoded['selectedProfileId'], profile.id);
    expect(updates['enabled'], isFalse);
    expect(updates['channel'], 'nightly');
    expect(
      dataRoot
          .listSync(recursive: true)
          .whereType<File>()
          .where((file) => file.path.endsWith('.tmp')),
      isEmpty,
    );
  });

  test('oversized persisted settings fail before JSON decoding', () async {
    await dataRoot.create(recursive: true);
    await File(
      p.join(dataRoot.path, 'settings.json'),
    ).writeAsBytes(List<int>.filled(1024 * 1024 + 1, 0x20));

    await expectLater(
      repository.loadSnapshot(),
      throwsA(
        predicate((error) => error.toString().contains('exceeds 1048576')),
      ),
    );
  });

  test(
    'recovers an interrupted atomic settings swap from its backup',
    () async {
      await dataRoot.create(recursive: true);
      final backup = File(p.join(dataRoot.path, 'settings.json.42.bak'));
      await backup.writeAsString(
        jsonEncode({'selectedProfileId': 'preserved'}),
      );

      await repository.setDeveloperMode(true);

      final live = File(p.join(dataRoot.path, 'settings.json'));
      final decoded =
          jsonDecode(await live.readAsString()) as Map<String, Object?>;
      expect(decoded['selectedProfileId'], 'preserved');
      expect(decoded['developerMode'], isTrue);
      expect(backup.existsSync(), isFalse);
    },
  );

  test('package-source persistence rejects plaintext remote URLs', () async {
    await expectLater(
      repository.savePackageSources(const [
        PackageSource(
          id: 'unsafe',
          name: 'Unsafe',
          url: 'http://packages.example/index.json',
        ),
      ]),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('HTTPS'),
        ),
      ),
    );
  });

  test('update settings never persist an untrusted archive URL', () async {
    await repository.saveLauncherUpdateSettings(
      const LauncherUpdateSettings(
        archiveUrl: 'http://updates.example/manual-releases.json',
      ),
    );

    final decoded =
        jsonDecode(
              await File(p.join(dataRoot.path, 'settings.json')).readAsString(),
            )
            as Map<String, Object?>;
    final updates = decoded['launcherUpdates'] as Map<String, Object?>;
    expect(updates['archiveUrl'], LauncherUpdateSettings.defaultArchiveUrl);
  });

  test('profile import is bounded and rejects symbolic links', () async {
    final oversized = File(
      p.join(root.path, 'oversized.topiaforgeprofile.json'),
    );
    await oversized.writeAsBytes(List<int>.filled(4 * 1024 * 1024 + 1, 0x20));
    await expectLater(
      repository.importProfile(oversized.path),
      throwsA(predicate((error) => error.toString().contains('exceeds'))),
    );

    if (!Platform.isWindows) {
      final target = File(p.join(root.path, 'profile.topiaforgeprofile.json'));
      await target.writeAsString(
        jsonEncode({
          'schemaVersion': 2,
          'profile': LauncherProfile.defaultProfile().toJson(),
        }),
      );
      final link = Link(
        p.join(root.path, 'profile-link.topiaforgeprofile.json'),
      );
      await link.create(target.path);
      await expectLater(
        repository.importProfile(link.path),
        throwsA(
          predicate((error) => error.toString().contains('symbolic link')),
        ),
      );
    }
  });

  test('profile export uses the canonical versioned contract', () async {
    final output = File(p.join(root.path, 'exported.topiaforgeprofile.json'));
    final profile = LauncherProfile.defaultProfile().copyWith(name: 'Portable');

    await repository.exportProfile(profile, output.path);

    final decoded = jsonDecode(await output.readAsString());
    expect(decoded, isA<Map<String, Object?>>());
    expect((decoded as Map<String, Object?>)['schemaVersion'], 2);
    expect(((decoded['profile'] as Map<String, Object?>)['name']), 'Portable');
    expect(
      root.listSync().whereType<File>().where(
        (file) => file.path.endsWith('.tmp'),
      ),
      isEmpty,
    );
  });

  test('profile writes reject unsafe in-memory ecosystem ids', () async {
    final retired =
        'robo'
        'topia.world.retired';
    final profile = LauncherProfile(
      id: 'unsafe-world',
      name: 'Unsafe world',
      worldSelection: WorldSelection(worldId: retired),
    );
    final output = File(p.join(root.path, 'unsafe.topiaforgeprofile.json'));

    await expectLater(
      repository.saveProfiles([profile], profile.id),
      throwsFormatException,
    );
    await expectLater(
      repository.exportProfile(profile, output.path),
      throwsFormatException,
    );
    expect(output.existsSync(), isFalse);
  });

  test(
    'profile import rejects old versions and non-canonical suffixes',
    () async {
      final oldVersion = File(p.join(root.path, 'old.topiaforgeprofile.json'))
        ..writeAsStringSync(
          jsonEncode({
            'schemaVersion': 1,
            'profile': LauncherProfile.defaultProfile().toJson(),
          }),
        );
      final wrongSuffix = File(p.join(root.path, 'profile.json'))
        ..writeAsStringSync('{}');

      await expectLater(
        repository.importProfile(oldVersion.path),
        throwsFormatException,
      );
      await expectLater(
        repository.importProfile(wrongSuffix.path),
        throwsFormatException,
      );
    },
  );

  test('v2 profile stores and imports reject retired mod ids', () async {
    final retired = String.fromCharCodes(const [
      114,
      111,
      98,
      111,
      116,
      111,
      112,
      105,
      97,
      46,
      109,
      111,
      100,
    ]);
    final profile = {
      ...LauncherProfile.defaultProfile().toJson(),
      'enabledMods': [retired],
    };
    await dataRoot.create(recursive: true);
    File(p.join(dataRoot.path, 'profiles.json')).writeAsStringSync(
      jsonEncode({
        'schemaVersion': 2,
        'profiles': [profile],
      }),
    );
    final imported = File(p.join(root.path, 'retired.topiaforgeprofile.json'))
      ..writeAsStringSync(jsonEncode({'schemaVersion': 2, 'profile': profile}));

    await expectLater(repository.loadSnapshot(), throwsFormatException);
    await expectLater(
      repository.importProfile(imported.path),
      throwsFormatException,
    );
  });

  test('launcher log link cannot fail a durable source save', () async {
    if (Platform.isWindows) {
      return;
    }
    final target = File(p.join(root.path, 'outside.log'))
      ..writeAsStringSync('preserve me');
    final log = Link(p.join(dataRoot.path, 'logs', 'launcher.log'));
    await log.parent.create(recursive: true);
    await log.create(target.path);

    final saved = await repository.savePackageSources([
      PackageSource(
        id: 'local',
        name: 'Local',
        url: Uri.directory(root.path).toString(),
      ),
    ]);

    expect(saved.single.id, 'local');
    expect(await target.readAsString(), 'preserve me');
    expect(
      File(p.join(dataRoot.path, 'package_sources.json')).existsSync(),
      isTrue,
    );
  });

  test('launcher log is rotated before it can grow without bound', () async {
    final log = File(p.join(dataRoot.path, 'logs', 'launcher.log'));
    await log.create(recursive: true);
    await log.writeAsBytes(List<int>.filled(8 * 1024 * 1024 + 1, 0x61));

    await repository.savePackageSources([
      PackageSource(
        id: 'local',
        name: 'Local',
        url: Uri.directory(root.path).toString(),
      ),
    ]);

    expect(await log.length(), lessThan(5 * 1024 * 1024));
    expect(await log.readAsString(), contains('Saved 1 package sources.'));
  });
}
