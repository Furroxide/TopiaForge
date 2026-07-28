import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  test(
    'equal versions use normalized path order, not enumeration order',
    () async {
      final fixture = await _InboxFixture.create();
      addTearDown(fixture.dispose);
      fixture.writePackage(
        fileName: 'Z-equal.topiaforgemod',
        id: 'inbox.path.tie',
        version: '1.0.0',
        marker: 'path-later',
      );
      fixture.writePackage(
        fileName: 'a-equal.topiaforgemod',
        id: 'inbox.path.tie',
        version: '1.0.0',
        marker: 'path-winner',
      );

      final outcome = await fixture.repository.installInboxPackages(
        fixture.install,
      );

      expect(outcome.status, PackageInboxInstallStatus.success);
      expect(outcome.installedCount, 1);
      expect(outcome.supersededCount, 1);
      expect(outcome.consumedCount, 2);
      expect(fixture.inboxPackages, isEmpty);
      expect(fixture.installedMarker('inbox.path.tie', '1.0.0'), 'path-winner');
    },
  );

  test(
    'highest compatible semantic version wins and lower is consumed',
    () async {
      final fixture = await _InboxFixture.create();
      addTearDown(fixture.dispose);
      fixture.writePackage(
        fileName: 'newest.topiaforgemod',
        id: 'inbox.highest',
        version: '2.0.0',
        marker: 'newest',
      );
      fixture.writePackage(
        fileName: 'older.topiaforgemod',
        id: 'inbox.highest',
        version: '1.9.9',
        marker: 'older',
      );

      final outcome = await fixture.repository.installInboxPackages(
        fixture.install,
      );

      expect(outcome.status, PackageInboxInstallStatus.success);
      expect(outcome.installedCount, 1);
      expect(outcome.supersededCount, 1);
      expect(outcome.consumedCount, 2);
      expect(fixture.installedMarker('inbox.highest', '2.0.0'), 'newest');
      expect(
        fixture.installedDirectory('inbox.highest', '1.9.9').existsSync(),
        isFalse,
      );
    },
  );

  test(
    'incompatible higher version is retained and compatible lower wins',
    () async {
      final fixture = await _InboxFixture.create();
      addTearDown(fixture.dispose);
      final lower = fixture.writePackage(
        fileName: 'compatible.topiaforgemod',
        id: 'inbox.compatibility',
        version: '1.0.0',
        gameRange: '0.0.2309',
        marker: 'compatible',
      );
      final higher = fixture.writePackage(
        fileName: 'incompatible.topiaforgemod',
        id: 'inbox.compatibility',
        version: '2.0.0',
        gameRange: '>=0.0.3000',
        marker: 'incompatible',
      );

      final outcome = await fixture.repository.installInboxPackages(
        fixture.install,
      );

      expect(outcome.status, PackageInboxInstallStatus.partial);
      expect(outcome.installedCount, 1);
      expect(outcome.invalidCount, 1);
      expect(outcome.consumedCount, 1);
      expect(lower.existsSync(), isFalse);
      expect(higher.existsSync(), isTrue);
      expect(
        fixture.installedMarker('inbox.compatibility', '1.0.0'),
        'compatible',
      );
      expect(outcome.issues.single.message, contains('supports Robotopia'));
    },
  );

  test(
    'corrupt higher managed assembly is rejected before selection',
    () async {
      final fixture = await _InboxFixture.create(
        metadataValidator: (root) async {
          final assemblies = root
              .listSync(recursive: true, followLinks: false)
              .whereType<File>()
              .where((file) => file.path.endsWith('.dll'));
          return assemblies.any((file) => file.readAsStringSync() == 'corrupt')
              ? const ['TFPKG160: managed PE is corrupt.']
              : const [];
        },
      );
      addTearDown(fixture.dispose);
      final valid = fixture.writePackage(
        fileName: 'valid-lower.topiaforgemod',
        id: 'inbox.corrupt',
        version: '1.0.0',
        marker: 'valid',
      );
      final corrupt = fixture.writePackage(
        fileName: 'corrupt-higher.topiaforgemod',
        id: 'inbox.corrupt',
        version: '2.0.0',
        marker: 'corrupt',
        assemblyBytes: 'corrupt',
      );

      final outcome = await fixture.repository.installInboxPackages(
        fixture.install,
      );

      expect(outcome.status, PackageInboxInstallStatus.partial);
      expect(outcome.installedCount, 1);
      expect(outcome.invalidCount, 1);
      expect(valid.existsSync(), isFalse);
      expect(corrupt.existsSync(), isTrue);
      expect(fixture.installedMarker('inbox.corrupt', '1.0.0'), 'valid');
      expect(outcome.issues.single.message, contains('managed PE is corrupt'));
    },
  );

  test('higher version with no complete dependency plan is retained', () async {
    final fixture = await _InboxFixture.create();
    addTearDown(fixture.dispose);
    final lower = fixture.writePackage(
      fileName: 'installable-lower.topiaforgemod',
      id: 'inbox.plan.fallback',
      version: '1.0.0',
      marker: 'installable',
    );
    final higher = fixture.writePackage(
      fileName: 'blocked-higher.topiaforgemod',
      id: 'inbox.plan.fallback',
      version: '2.0.0',
      marker: 'blocked',
      dependencies: const {'missing.provider': '>=1.0.0'},
    );

    final outcome = await fixture.repository.installInboxPackages(
      fixture.install,
    );

    expect(outcome.status, PackageInboxInstallStatus.partial);
    expect(outcome.installedCount, 1);
    expect(outcome.invalidCount, 1);
    expect(lower.existsSync(), isFalse);
    expect(higher.existsSync(), isTrue);
    expect(
      fixture.installedMarker('inbox.plan.fallback', '1.0.0'),
      'installable',
    );
    expect(outcome.issues.single.message, startsWith('TFINBOX115:'));
  });

  test('invalid candidate is reported and retained for inspection', () async {
    final fixture = await _InboxFixture.create();
    addTearDown(fixture.dispose);
    final invalid = File(p.join(fixture.inbox.path, 'broken.topiaforgemod'))
      ..writeAsBytesSync([0, 1, 2, 3]);

    final outcome = await fixture.repository.installInboxPackages(
      fixture.install,
    );

    expect(outcome.status, PackageInboxInstallStatus.failure);
    expect(outcome.candidateCount, 1);
    expect(outcome.invalidCount, 1);
    expect(outcome.installedCount, 0);
    expect(outcome.consumedCount, 0);
    expect(invalid.existsSync(), isTrue);
    expect(outcome.issues.single.message, startsWith('TFINBOX110:'));
  });

  test(
    'atomic install failure retains winner and valid superseded files',
    () async {
      final fixture = await _InboxFixture.create(
        commitHook: (_) => throw StateError('injected commit failure'),
      );
      addTearDown(fixture.dispose);
      final winner = fixture.writePackage(
        fileName: 'winner.topiaforgemod',
        id: 'inbox.commit.failure',
        version: '2.0.0',
        marker: 'winner',
      );
      final superseded = fixture.writePackage(
        fileName: 'superseded.topiaforgemod',
        id: 'inbox.commit.failure',
        version: '1.0.0',
        marker: 'superseded',
      );

      final outcome = await fixture.repository.installInboxPackages(
        fixture.install,
      );

      expect(outcome.status, PackageInboxInstallStatus.failure);
      expect(outcome.installFailureCount, 1);
      expect(outcome.supersededCount, 1);
      expect(outcome.consumedCount, 0);
      expect(winner.existsSync(), isTrue);
      expect(superseded.existsSync(), isTrue);
      expect(
        fixture
            .installedDirectory('inbox.commit.failure', '2.0.0')
            .existsSync(),
        isFalse,
      );
      expect(
        outcome.issues.single.message,
        contains('injected commit failure'),
      );
    },
  );

  test(
    'global selection downgrades a later provider to satisfy its consumer',
    () async {
      final fixture = await _InboxFixture.create();
      addTearDown(fixture.dispose);
      fixture.writePackage(
        fileName: 'consumer.topiaforgemod',
        id: 'a.inbox.consumer',
        version: '1.0.0',
        marker: 'consumer',
        dependencies: const {'z.inbox.provider': '<2.0.0'},
      );
      fixture.writePackage(
        fileName: 'provider-new.topiaforgemod',
        id: 'z.inbox.provider',
        version: '2.0.0',
        marker: 'incompatible-new',
      );
      fixture.writePackage(
        fileName: 'provider-compatible.topiaforgemod',
        id: 'z.inbox.provider',
        version: '1.0.0',
        marker: 'compatible',
      );

      final outcome = await fixture.repository.installInboxPackages(
        fixture.install,
      );

      expect(outcome.status, PackageInboxInstallStatus.success);
      expect(outcome.installedCount, 2);
      expect(outcome.supersededCount, 1);
      expect(outcome.consumedCount, 3);
      expect(fixture.inboxPackages, isEmpty);
      expect(fixture.installedMarker('a.inbox.consumer', '1.0.0'), 'consumer');
      expect(
        fixture.installedMarker('z.inbox.provider', '1.0.0'),
        'compatible',
      );
      expect(
        fixture.installedDirectory('z.inbox.provider', '2.0.0').existsSync(),
        isFalse,
      );
    },
  );

  test('changed superseded bytes are retained instead of consumed', () async {
    late File superseded;
    final fixture = await _InboxFixture.create(
      commitHook: (_) => superseded.writeAsBytesSync([9, 8, 7], flush: true),
    );
    addTearDown(fixture.dispose);
    fixture.writePackage(
      fileName: 'selected.topiaforgemod',
      id: 'inbox.toctou',
      version: '2.0.0',
      marker: 'selected',
    );
    superseded = fixture.writePackage(
      fileName: 'superseded.topiaforgemod',
      id: 'inbox.toctou',
      version: '1.0.0',
      marker: 'superseded',
    );

    final outcome = await fixture.repository.installInboxPackages(
      fixture.install,
    );

    expect(outcome.status, PackageInboxInstallStatus.partial);
    expect(outcome.installedCount, 1);
    expect(outcome.consumedCount, 1);
    expect(outcome.consumptionFailureCount, 1);
    expect(superseded.existsSync(), isFalse);
    final retained = fixture.inbox
        .listSync(recursive: true, followLinks: false)
        .whereType<File>()
        .single;
    expect(retained.readAsBytesSync(), [9, 8, 7]);
    expect(outcome.issues.single.message, contains('changed after preflight'));
  });

  test('changed winner bytes fail the SHA-pinned install closed', () async {
    late File selected;
    var validationCount = 0;
    final fixture = await _InboxFixture.create(
      metadataValidator: (_) async {
        validationCount += 1;
        if (validationCount == 2) {
          selected.writeAsBytesSync([4, 3, 2, 1], flush: true);
        }
        return const [];
      },
    );
    addTearDown(fixture.dispose);
    selected = fixture.writePackage(
      fileName: 'a-selected.topiaforgemod',
      id: 'a.inbox.changed',
      version: '1.0.0',
      marker: 'changed',
      dependencies: const {'b.inbox.provider': '*'},
    );
    final provider = fixture.writePackage(
      fileName: 'b-provider.topiaforgemod',
      id: 'b.inbox.provider',
      version: '2.0.0',
      marker: 'provider',
    );
    final providerAlternative = fixture.writePackage(
      fileName: 'b-provider-alternative.topiaforgemod',
      id: 'b.inbox.provider',
      version: '1.0.0',
      marker: 'provider-alternative',
    );

    final outcome = await fixture.repository.installInboxPackages(
      fixture.install,
    );

    expect(outcome.status, PackageInboxInstallStatus.partial);
    expect(outcome.installedCount, 1);
    expect(outcome.installFailureCount, 1);
    expect(outcome.consumedCount, 1);
    expect(selected.existsSync(), isTrue);
    expect(provider.existsSync(), isFalse);
    expect(providerAlternative.existsSync(), isTrue);
    expect(outcome.issues.single.message, contains('SHA-256 mismatch'));
  });

  test('inbox enumeration fails closed above the package limit', () async {
    final fixture = await _InboxFixture.create();
    addTearDown(fixture.dispose);
    for (var index = 0; index <= 256; index++) {
      File(
        p.join(fixture.inbox.path, '$index.topiaforgemod'),
      ).writeAsBytesSync(const [0]);
    }

    final outcome = await fixture.repository.installInboxPackages(
      fixture.install,
    );

    expect(outcome.status, PackageInboxInstallStatus.failure);
    expect(outcome.installedCount, 0);
    expect(outcome.consumedCount, 0);
    expect(outcome.candidateCount, 257);
    expect(outcome.issues.single.message, startsWith('TFINBOX103:'));
    expect(fixture.inboxPackages, hasLength(257));
  });
}

class _InboxFixture {
  _InboxFixture._({
    required this.root,
    required this.game,
    required this.inbox,
    required this.repository,
    required this.install,
  });

  final Directory root;
  final Directory game;
  final Directory inbox;
  final LocalLauncherRepository repository;
  final GameInstall install;

  static Future<_InboxFixture> create({
    PackageMetadataValidator? metadataValidator,
    PackageInstallCommitHook? commitHook,
  }) async {
    final root = Directory.systemTemp.createTempSync('package-inbox-');
    final game = Directory(p.join(root.path, 'Robotopia'))..createSync();
    File(p.join(game.path, 'Robotopia.exe')).writeAsStringSync('');
    File(
      p.join(game.path, 'installed-build.json'),
    ).writeAsStringSync('{"id":2309}');
    final managed = Directory(p.join(game.path, 'Robotopia_Data', 'Managed'))
      ..createSync(recursive: true);
    File(p.join(managed.path, 'UnityEngine.dll')).writeAsStringSync('');
    final inbox = Directory(
      p.join(game.path, 'BepInEx', 'TopiaForge', 'package-inbox'),
    )..createSync(recursive: true);
    final repository = LocalLauncherRepository(
      dataRoot: p.join(root.path, 'data'),
      repositoryRoot: root.path,
      packageMetadataValidator: metadataValidator ?? (_) async => const [],
      packageInstallCommitHook: commitHook,
    );
    final install = await repository.selectGameDirectory(game.path);
    return _InboxFixture._(
      root: root,
      game: game,
      inbox: inbox,
      repository: repository,
      install: install,
    );
  }

  List<File> get inboxPackages => inbox
      .listSync(followLinks: false)
      .whereType<File>()
      .where((file) => file.path.endsWith('.topiaforgemod'))
      .toList();

  File writePackage({
    required String fileName,
    required String id,
    required String version,
    required String marker,
    String gameRange = '*',
    String assemblyBytes = 'valid managed assembly fixture',
    Map<String, String> dependencies = const {},
  }) {
    final assemblyName = '${id.replaceAll('.', '_')}.dll';
    final manifest = <String, Object?>{
      'schemaVersion': 5,
      'name': id,
      'displayName': id,
      'version': version,
      'author': {'name': 'TopiaForge'},
      'entryAssembly': assemblyName,
      'entryType': '${id.replaceAll('.', '_')}.Entry',
      'supportedGameVersionRange': gameRange,
      'supportedLoaderVersionRange': '*',
      'supportedSdkVersionRange': '*',
      if (dependencies.isNotEmpty) 'dependencies': dependencies,
    };
    final archive = Archive()
      ..addFile(ArchiveFile.string('topiaforge.mod.json', jsonEncode(manifest)))
      ..addFile(ArchiveFile.string(assemblyName, assemblyBytes))
      ..addFile(ArchiveFile.string('marker.txt', marker));
    final file = File(p.join(inbox.path, fileName));
    file.writeAsBytesSync(ZipEncoder().encode(archive), flush: true);
    return file;
  }

  Directory installedDirectory(String id, String version) => Directory(
    p.join(game.path, 'BepInEx', 'TopiaForge', 'packages', id, version),
  );

  String installedMarker(String id, String version) => File(
    p.join(installedDirectory(id, version).path, 'marker.txt'),
  ).readAsStringSync();

  Future<void> dispose() async {
    await repository.dispose();
    if (root.existsSync()) root.deleteSync(recursive: true);
  }
}
