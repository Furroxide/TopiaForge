import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:archive/archive.dart';
import 'package:crypto/crypto.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory root;
  late Directory install;
  late LauncherInstallationLayout layout;

  setUp(() async {
    root = await Directory.systemTemp.createTemp('topiaforge-update-stage-');
    install = Directory(p.join(root.path, 'TopiaForge'))
      ..createSync(recursive: true);
    File(
      p.join(install.path, 'topiaforge.exe'),
    ).writeAsStringSync('old helper');
    File(p.join(install.path, 'launcher', 'topiaforge_launcher.exe'))
      ..parent.createSync(recursive: true)
      ..writeAsStringSync('old launcher');
    layout = LauncherInstallationLayout(
      platformId: 'windows-x64',
      installLayout: 'portable-root',
      targetRoot: install.path,
      launcherRelativePath: p.join('launcher', 'topiaforge_launcher.exe'),
      helperRelativePaths: const ['topiaforge.exe'],
      helperExecutableName: 'topiaforge-update-helper.exe',
    );
  });

  tearDown(() async {
    if (root.existsSync()) await root.delete(recursive: true);
  });

  test('streams, verifies, extracts, and plans a complete package', () async {
    final archive = _archive();
    final repository = LocalLauncherUpdateRepository(
      dataRoot: p.join(root.path, 'data'),
      installation: layout,
      transport: _ArchiveTransport(archive),
    );
    addTearDown(repository.dispose);

    final status = await repository.stageUpdate(_candidate(archive));

    expect(status.phase, LauncherUpdatePhase.staged);
    final plan = LauncherUpdateTransactionPlan.read(
      File(status.stagedPlanPath),
    );
    expect(plan.targetRoot, install.path);
    expect(
      File(
        p.join(plan.stagedRoot, 'launcher', 'topiaforge_launcher.exe'),
      ).readAsStringSync(),
      'new launcher',
    );
    expect(status.progress, 1);
  });

  test('rejects a staged plan modified after verification', () async {
    final archive = _archive();
    final repository = LocalLauncherUpdateRepository(
      dataRoot: p.join(root.path, 'data'),
      installation: layout,
      transport: _ArchiveTransport(archive),
    );
    addTearDown(repository.dispose);
    final status = await repository.stageUpdate(_candidate(archive));
    final planFile = File(status.stagedPlanPath);
    final json = Map<String, Object?>.from(
      jsonDecode(planFile.readAsStringSync()) as Map,
    );
    json['launcherRelativePath'] = 'other.exe';
    planFile.writeAsStringSync(jsonEncode(json));

    await expectLater(
      repository.applyStagedUpdate(status),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('does not match this installation'),
        ),
      ),
    );
    expect(
      Directory(p.join(planFile.parent.path, 'helper')).existsSync(),
      isFalse,
    );
  });

  test('wrong root layout fails with a verified manual fallback', () async {
    final archive = _archive(includeLauncher: false);
    final repository = LocalLauncherUpdateRepository(
      dataRoot: p.join(root.path, 'data'),
      installation: layout,
      transport: _ArchiveTransport(archive),
    );
    addTearDown(repository.dispose);

    final status = await repository.stageUpdate(_candidate(archive));

    expect(status.phase, LauncherUpdatePhase.failed);
    expect(status.message, contains('missing its launcher'));
    expect(status.message, contains('/releases/tag/'));
    expect(
      File(p.join(install.path, 'topiaforge.exe')).readAsStringSync(),
      'old helper',
    );
  });

  test(
    'traversal is rejected before any staged installation is usable',
    () async {
      final source = Archive()
        ..addFile(ArchiveFile.string('topiaforge.exe', 'new helper'))
        ..addFile(
          ArchiveFile.string(
            'launcher/topiaforge_launcher.exe',
            'new launcher',
          ),
        )
        ..addFile(ArchiveFile.string('../outside.txt', 'escape'));
      final archive = Uint8List.fromList(ZipEncoder().encode(source));
      final repository = LocalLauncherUpdateRepository(
        dataRoot: p.join(root.path, 'data'),
        installation: layout,
        transport: _ArchiveTransport(archive),
      );
      addTearDown(repository.dispose);

      final status = await repository.stageUpdate(
        _candidate(archive, entryCount: 3, expandedSize: archive.length + 1024),
      );

      expect(status.phase, LauncherUpdatePhase.failed);
      expect(status.message, contains('unsafe'));
      expect(File(p.join(root.path, 'outside.txt')).existsSync(), isFalse);
    },
  );
}

Uint8List _archive({bool includeLauncher = true}) {
  final source = Archive()
    ..addFile(ArchiveFile.string('topiaforge.exe', 'new helper'))
    ..addFile(
      ArchiveFile.bytes('payload.bin', List<int>.filled(16 * 1024, 0x41)),
    );
  if (includeLauncher) {
    source.addFile(
      ArchiveFile.string('launcher/topiaforge_launcher.exe', 'new launcher'),
    );
  }
  return Uint8List.fromList(ZipEncoder().encode(source));
}

LauncherUpdateCandidate _candidate(
  Uint8List archive, {
  int? entryCount,
  int? expandedSize,
}) {
  final decoded = entryCount == null || expandedSize == null
      ? SafeZipArchive.decode(archive)
      : null;
  final expanded =
      expandedSize ??
      decoded!.entries.fold<int>(0, (total, entry) => total + entry.size);
  final artifact = (
    hash: sha256.convert(archive).toString(),
    size: archive.length,
    entries: entryCount ?? decoded!.entries.length,
    expanded: expanded,
  );
  const version = '1.0.0-rc.2';
  const payloadHash =
      'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';
  return LauncherUpdateCandidate(
    version: version,
    tag: 'v$version',
    channel: LauncherUpdateChannel.beta,
    minimumUpdaterVersion: '1.0.0-rc.1',
    releaseUrl:
        'https://github.com/furroxide/TopiaForge/releases/tag/v$version',
    signingKeyId: 'ed25519:0123456789abcdef',
    payloadSha256: payloadHash,
    platforms: {
      for (final entry in const {
        'windows-x64': (
          name: 'TopiaForge-windows-x64.zip',
          layout: 'portable-root',
        ),
        'linux-x64': (
          name: 'TopiaForge-linux-x64.zip',
          layout: 'portable-root',
        ),
        'macos-universal': (
          name: 'TopiaForge-macos-universal.zip',
          layout: 'app-bundle',
        ),
      }.entries)
        entry.key: LauncherUpdateArtifact(
          platform: entry.key,
          assetName: entry.value.name,
          url:
              'https://github.com/furroxide/TopiaForge/releases/download/'
              'v$version/${entry.value.name}',
          sha256: artifact.hash,
          size: artifact.size,
          entryCount: artifact.entries,
          expandedSize: artifact.expanded,
          installLayout: entry.value.layout,
        ),
    },
  );
}

final class _ArchiveTransport implements LauncherUpdateTransport {
  _ArchiveTransport(this.archive);

  final Uint8List archive;

  @override
  Future<LauncherUpdateDownloadResult> download(
    Uri uri, {
    required File partialFile,
    required int expectedSize,
    required String expectedSha256,
    void Function(double progress)? onProgress,
  }) async {
    partialFile.parent.createSync(recursive: true);
    partialFile.writeAsBytesSync(archive);
    onProgress?.call(1);
    return LauncherUpdateDownloadResult(
      path: partialFile.path,
      size: archive.length,
      sha256: sha256.convert(archive).toString(),
    );
  }

  @override
  Future<Uint8List> fetch(
    Uri uri, {
    required int maxBytes,
    required String label,
  }) => throw UnimplementedError();

  @override
  void close() {}
}
