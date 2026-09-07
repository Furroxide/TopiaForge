import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory root;
  late LocalLauncherRepository repository;
  setUp(() {
    root = Directory.systemTemp.createTempSync('topiaforge-profile-selection-');
    repository = LocalLauncherRepository(
      dataRoot: p.join(root.path, 'data'),
      repositoryRoot: root.path,
    );
  });
  tearDown(() async {
    await repository.dispose();
    if (root.existsSync()) root.deleteSync(recursive: true);
  });
  File exported() => File(p.join(root.path, 'profile.topiaforgeprofile.json'));
  test(
    'legacy profile import and export preserve raw values in versioned selection',
    () async {
      final raw = <String, Object?>{
        'worldId': null,
        'gamemodeId': 'retired.sandbox',
        'loadMode': 'future-mode',
        'launchIntoGamemode': false,
        'future': {
          'items': [null, 1.5, false],
        },
      };
      final file = exported()
        ..writeAsStringSync(
          jsonEncode({
            'schemaVersion': 2,
            'profile': {
              'id': 'legacy',
              'name': 'Legacy',
              'enabledMods': <String>[],
              'selectedVersions': <String, String>{},
              'worldSelection': raw,
            },
          }),
        );
      final profile = await repository.importProfile(file.path);
      expect(
        profile.launchSelection.kind,
        LaunchSelectionKind.unresolvedLegacy,
      );
      expect(profile.launchSelection.legacy, {'worldSelection': raw});
      await repository.exportProfile(profile, file.path);
      final stored = jsonDecode(file.readAsStringSync()) as Map;
      expect(stored['schemaVersion'], 3);
      expect((stored['profile'] as Map).containsKey('worldSelection'), isFalse);
      final reread = await repository.importProfile(file.path);
      expect(reread.launchSelection.legacy, {'worldSelection': raw});
    },
  );

  test(
    'repository assigns revisions and rejects stale mutation without writing',
    () async {
      var profiles = await repository.saveProfiles([
        const LauncherProfile(id: 'profile', name: 'Initial'),
      ], 'profile');
      expect(profiles.single.revision, 0);
      final initial = profiles.single;
      profiles = await repository.saveProfiles([
        initial.copyWith(name: 'Edited'),
      ], 'profile');
      expect(profiles.single.revision, 1);
      final file = File(p.join(repository.dataRoot, 'profiles.json'));
      final before = file.readAsStringSync();
      final unchanged = await repository.saveProfiles(profiles, 'profile');
      expect(unchanged.single.revision, 1);
      await expectLater(
        repository.saveProfiles([initial.copyWith(name: 'Stale')], 'profile'),
        throwsStateError,
      );
      expect(file.readAsStringSync(), before);
    },
  );

  test(
    'duplicate profile gets fresh revision and same unresolved selection',
    () async {
      final legacy = LaunchSelection.unresolvedLegacy({
        'worldSelection': {'loadMode': 'unknown'},
      });
      var profiles = await repository.saveProfiles([
        LauncherProfile(
          id: 'original',
          name: 'Original',
          launchSelection: legacy,
        ),
      ], 'original');
      profiles = await repository.saveProfiles([
        profiles.single.copyWith(name: 'Edited'),
      ], 'original');
      profiles = await repository.saveProfiles([
        ...profiles,
        profiles.single.copyWith(id: 'copy', name: 'Copy'),
      ], 'copy');
      expect(profiles.first.revision, 1);
      expect(profiles.last.revision, 0);
      expect(profiles.last.launchSelection, legacy);
    },
  );

  LocalLauncherRepository peer() {
    final value = LocalLauncherRepository(
      dataRoot: repository.dataRoot,
      repositoryRoot: root.path,
    );
    addTearDown(value.dispose);
    return value;
  }

  test(
    'stale full-list save cannot delete an unseen profile added by another repository',
    () async {
      final initial = await repository.saveProfiles([
        const LauncherProfile(id: 'a', name: 'A'),
        const LauncherProfile(id: 'b', name: 'B'),
      ], 'a');
      final other = peer();
      await other.saveProfiles(initial, 'a');
      await other.saveProfiles([
        ...initial,
        const LauncherProfile(id: 'new', name: 'Added elsewhere'),
      ], 'new');
      final file = File(p.join(repository.dataRoot, 'profiles.json'));
      final settings = File(p.join(repository.dataRoot, 'settings.json'));
      final before = file.readAsStringSync();
      final beforeSettings = settings.readAsStringSync();
      await expectLater(
        repository.saveProfiles(initial, 'a'),
        throwsStateError,
      );
      expect(file.readAsStringSync(), before);
      expect(settings.readAsStringSync(), beforeSettings);
    },
  );

  test(
    'stale deletion cannot remove a profile edited by another repository',
    () async {
      final initial = await repository.saveProfiles([
        const LauncherProfile(id: 'a', name: 'A'),
        const LauncherProfile(id: 'b', name: 'B'),
      ], 'a');
      final other = peer();
      await other.saveProfiles(initial, 'a');
      await other.saveProfiles([
        initial.first,
        initial.last.copyWith(name: 'Changed elsewhere'),
      ], 'b');
      final file = File(p.join(repository.dataRoot, 'profiles.json'));
      final before = file.readAsStringSync();
      await expectLater(
        repository.saveProfiles([initial.first], 'a'),
        throwsStateError,
      );
      expect(file.readAsStringSync(), before);
    },
  );

  test('explicit deletion of an observed unchanged profile succeeds', () async {
    final initial = await repository.saveProfiles([
      const LauncherProfile(id: 'a', name: 'A'),
      const LauncherProfile(id: 'b', name: 'B'),
    ], 'a');
    final result = await repository.saveProfiles([initial.first], 'a');
    expect(result.map((item) => item.id), ['a']);
    expect(result.single.revision, 0);
    final contents =
        jsonDecode(
              File(
                p.join(repository.dataRoot, 'profiles.json'),
              ).readAsStringSync(),
            )
            as Map;
    expect((contents['profiles'] as List).length, 1);
  });

  test(
    'stale full-list save cannot resurrect a profile removed elsewhere',
    () async {
      final initial = await repository.saveProfiles([
        const LauncherProfile(id: 'a', name: 'A'),
        const LauncherProfile(id: 'b', name: 'B'),
      ], 'a');
      final other = peer();
      await other.saveProfiles(initial, 'a');
      await other.saveProfiles([initial.first], 'a');
      final file = File(p.join(repository.dataRoot, 'profiles.json'));
      final before = file.readAsStringSync();
      await expectLater(
        repository.saveProfiles(initial, 'a'),
        throwsStateError,
      );
      expect(file.readAsStringSync(), before);
    },
  );

  test(
    'malformed legacy profile arrays are refused without rewriting originals',
    () async {
      final data = Directory(repository.dataRoot)..createSync(recursive: true);
      final file = File(p.join(data.path, 'profiles.json'));
      const source =
          '{"schemaVersion":2,"profiles":[{"id":"ok","name":"OK"},17]}';
      file.writeAsStringSync(source);
      await expectLater(repository.loadSnapshot(), throwsFormatException);
      expect(file.readAsStringSync(), source);
    },
  );
}
