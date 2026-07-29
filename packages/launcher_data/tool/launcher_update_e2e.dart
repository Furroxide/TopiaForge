import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:crypto/crypto.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;

Future<void> main(List<String> arguments) async {
  final platform = _requiredValue(arguments, '--platform');
  final archiveFile = File(_requiredOption(arguments, '--archive'));
  final healthFixture = File(_requiredOption(arguments, '--health-fixture'));
  final noHealthFixture = File(
    _requiredOption(arguments, '--no-health-fixture'),
  );
  if (!archiveFile.existsSync() ||
      !healthFixture.existsSync() ||
      !noHealthFixture.existsSync()) {
    throw StateError('Update E2E inputs are missing.');
  }

  final descriptor = _PlatformDescriptor.forName(platform);
  final archiveBytes = archiveFile.readAsBytesSync();
  final decoded = SafeZipArchive.decode(
    archiveBytes,
    policy: const SafeArchivePolicy(
      maxArchiveBytes: 512 * 1024 * 1024,
      maxEntries: 20000,
      maxEntryBytes: 512 * 1024 * 1024,
      maxExpandedBytes: 2 * 1024 * 1024 * 1024,
    ),
    label: 'Packaged launcher E2E archive',
    allowContainedLinks: descriptor.platformId == 'macos-universal',
  );
  final expandedSize = decoded.entries.fold<int>(
    0,
    (total, entry) => total + entry.size,
  );
  final root = await Directory.systemTemp.createTemp(
    'topiaforge-packaged-update-e2e-',
  );
  try {
    final extracted = Directory(p.join(root.path, 'installed'))..createSync();
    decoded.extractTo(extracted, preserveExecutableMode: true);
    final targetRoot = descriptor.platformId == 'macos-universal'
        ? p.join(extracted.path, 'TopiaForge.app')
        : extracted.path;
    final layout = descriptor.layout(targetRoot);
    layout.validateCurrent();
    File(
      p.join(targetRoot, 'update-e2e-version.txt'),
    ).writeAsStringSync('rc.1');

    final externalHelper = Directory(p.join(root.path, 'external-helper'))
      ..createSync();
    for (final source in layout.helperSourcePaths) {
      final destination = File(p.join(externalHelper.path, p.basename(source)));
      File(source).copySync(destination.path);
      _makeExecutable(destination.path);
    }
    final helperPath = p.join(
      externalHelper.path,
      p.basename(layout.helperSourcePaths.first),
    );

    final key = await LauncherUpdateKeyMaterial.generate();
    final transport = _FixtureTransport(archiveBytes);
    final repository = LocalLauncherUpdateRepository(
      dataRoot: p.join(root.path, 'data'),
      transport: transport,
      trustStore: LauncherUpdateTrustStore([key.publicKey]),
      installation: layout,
      cooldown: Duration.zero,
    );
    try {
      await _publish(
        transport,
        key,
        version: '1.0.0-rc.2',
        archive: archiveBytes,
        entryCount: decoded.entries.length,
        expandedSize: expandedSize,
      );
      final rc2 = await _discover(repository, currentVersion: '1.0.0-rc.1');
      final rc2Plan = await _stageForExecution(
        repository,
        rc2,
        fixture: healthFixture,
        versionMarker: 'rc.2',
      );
      final applied = await _runHelper(helperPath, externalHelper, rc2Plan);
      _expect(applied.exitCode == 0, 'Packaged rc.2 update did not commit.');
      _expect(
        _versionAt(targetRoot) == 'rc.2',
        'Packaged rc.2 payload was not installed.',
      );
      _expect(
        _journalPhase(rc2Plan) == 'complete',
        'Packaged rc.2 transaction did not complete.',
      );
      _expect(
        !Directory(rc2Plan.backupRoot).existsSync(),
        'Committed update retained its backup.',
      );
      if (descriptor.platformId == 'macos-universal') {
        _expect(
          !Directory(p.dirname(rc2Plan.stagedRoot)).existsSync(),
          'Committed macOS update retained its staging container.',
        );
      }

      await _publish(
        transport,
        key,
        version: '1.0.0-rc.3',
        archive: archiveBytes,
        entryCount: decoded.entries.length,
        expandedSize: expandedSize,
      );
      final rc3 = await _discover(repository, currentVersion: '1.0.0-rc.2');
      final rc3Plan = await _stageForExecution(
        repository,
        rc3,
        fixture: noHealthFixture,
        versionMarker: 'rc.3',
      );
      final rolledBack = await _runHelper(helperPath, externalHelper, rc3Plan);
      _expect(
        rolledBack.exitCode != 0,
        'A launcher without a health handshake was accepted.',
      );
      _expect(
        _versionAt(targetRoot) == 'rc.2',
        'Failed update did not restore rc.2.',
      );
      _expect(
        _versionAt(rc3Plan.failedRoot) == 'rc.3',
        'Failed payload was not retained for recovery.',
      );
      _expect(
        _journalPhase(rc3Plan) == 'rolled-back',
        'Failed update did not record rollback.',
      );
      if (descriptor.platformId == 'macos-universal') {
        _expect(
          !Directory(p.dirname(rc3Plan.stagedRoot)).existsSync(),
          'Rolled-back macOS update retained its staging container.',
        );
      }
    } finally {
      await repository.dispose();
    }
    stdout.writeln(
      'Packaged ${descriptor.platformId} rc.1 -> rc.2 update and rollback '
      'E2E passed.',
    );
  } finally {
    if (root.existsSync()) await root.delete(recursive: true);
  }
}

Future<LauncherUpdateCandidate> _discover(
  LocalLauncherUpdateRepository repository, {
  required String currentVersion,
}) async {
  final status = await repository.checkForUpdate(
    currentVersion: currentVersion,
    channel: LauncherUpdateChannel.beta,
    force: true,
  );
  _expect(
    status.phase == LauncherUpdatePhase.available && status.candidate != null,
    'Locally signed update was not discovered: ${status.message}',
  );
  return status.candidate!;
}

Future<LauncherUpdateTransactionPlan> _stageForExecution(
  LocalLauncherUpdateRepository repository,
  LauncherUpdateCandidate candidate, {
  required File fixture,
  required String versionMarker,
}) async {
  final status = await repository.stageUpdate(candidate);
  _expect(
    status.phase == LauncherUpdatePhase.staged,
    'Locally signed update did not stage: ${status.message}',
  );
  final planFile = File(status.stagedPlanPath);
  final original = LauncherUpdateTransactionPlan.read(planFile);
  final launcher = File(
    p.join(original.stagedRoot, original.launcherRelativePath),
  );
  fixture.copySync(launcher.path);
  _makeExecutable(launcher.path);
  File(
    p.join(original.stagedRoot, 'update-e2e-version.txt'),
  ).writeAsStringSync(versionMarker);
  final executable = LauncherUpdateTransactionPlan(
    transactionId: original.transactionId,
    platformId: original.platformId,
    targetRoot: original.targetRoot,
    stagedRoot: original.stagedRoot,
    backupRoot: original.backupRoot,
    failedRoot: original.failedRoot,
    launcherRelativePath: original.launcherRelativePath,
    launcherPid: 2147483000,
    healthNonce: original.healthNonce,
    healthFile: original.healthFile,
    journalFile: original.journalFile,
    healthTimeoutSeconds: 5,
  );
  executable.write(planFile);
  return executable;
}

Future<ProcessResult> _runHelper(
  String helperPath,
  Directory helperRoot,
  LauncherUpdateTransactionPlan plan,
) => Process.run(helperPath, [
  'launcher',
  'apply-update',
  '--plan',
  p.join(p.dirname(plan.journalFile), 'plan.json'),
], workingDirectory: helperRoot.path).timeout(const Duration(seconds: 45));

Future<void> _publish(
  _FixtureTransport transport,
  LauncherUpdateKeyMaterial key, {
  required String version,
  required Uint8List archive,
  required int entryCount,
  required int expandedSize,
}) async {
  final tag = 'v$version';
  final hash = sha256.convert(archive).toString();
  final artifacts = {
    'windows-x64': (
      name: 'TopiaForge-windows-x64.zip',
      layout: 'portable-root',
    ),
    'linux-x64': (name: 'TopiaForge-linux-x64.zip', layout: 'portable-root'),
    'macos-universal': (
      name: 'TopiaForge-macos-universal.zip',
      layout: 'app-bundle',
    ),
  };
  final payload = Uint8List.fromList(
    utf8.encode(
      '${const JsonEncoder.withIndent('  ').convert({
        r'$schema': 'https://raw.githubusercontent.com/furroxide/TopiaForge/main/schemas/topiaforge.launcher-update-v1.schema.json',
        'formatVersion': 1,
        'product': 'TopiaForge',
        'version': version,
        'tag': tag,
        'channel': 'beta',
        'minimumUpdaterVersion': '1.0.0-rc.1',
        'releaseUrl': 'https://github.com/furroxide/TopiaForge/releases/tag/$tag',
        'platforms': {
          for (final artifact in artifacts.entries) artifact.key: {'assetName': artifact.value.name, 'url': _assetUri(tag, artifact.value.name).toString(), 'sha256': hash, 'size': archive.length, 'entryCount': entryCount, 'expandedSize': expandedSize, 'installLayout': artifact.value.layout},
        },
      })}\n',
    ),
  );
  final signature = await key.sign(payload);
  transport.publish(
    tag: tag,
    payload: payload,
    signature: signature,
    artifacts: artifacts.values.map((value) => value.name).toList(),
  );
}

Uri _assetUri(String tag, String name) => Uri.parse(
  'https://github.com/furroxide/TopiaForge/releases/download/$tag/$name',
);

String _versionAt(String root) =>
    File(p.join(root, 'update-e2e-version.txt')).readAsStringSync();

String _journalPhase(LauncherUpdateTransactionPlan plan) =>
    (jsonDecode(File(plan.journalFile).readAsStringSync()) as Map)['phase']
        as String;

void _makeExecutable(String path) {
  if (Platform.isWindows) return;
  final result = Process.runSync('/bin/chmod', ['755', path]);
  _expect(result.exitCode == 0, 'Could not mark $path executable.');
}

void _expect(bool condition, String message) {
  if (!condition) throw StateError(message);
}

String _requiredOption(List<String> arguments, String name) {
  return p.normalize(p.absolute(_requiredValue(arguments, name)));
}

String _requiredValue(List<String> arguments, String name) {
  final index = arguments.indexOf(name);
  if (index < 0 || index + 1 >= arguments.length) {
    throw FormatException('Missing $name.');
  }
  return arguments[index + 1];
}

final class _PlatformDescriptor {
  const _PlatformDescriptor({
    required this.platformId,
    required this.installLayout,
    required this.launcherRelativePath,
    required this.helperRelativePaths,
    required this.helperExecutableName,
  });

  factory _PlatformDescriptor.forName(String value) => switch (value) {
    'windows' => const _PlatformDescriptor(
      platformId: 'windows-x64',
      installLayout: 'portable-root',
      launcherRelativePath: 'launcher/topiaforge_launcher.exe',
      helperRelativePaths: ['topiaforge.exe'],
      helperExecutableName: 'topiaforge-update-helper.exe',
    ),
    'linux' => const _PlatformDescriptor(
      platformId: 'linux-x64',
      installLayout: 'portable-root',
      launcherRelativePath: 'launcher/topiaforge_launcher',
      helperRelativePaths: ['topiaforge'],
      helperExecutableName: 'topiaforge-update-helper',
    ),
    'macos' => const _PlatformDescriptor(
      platformId: 'macos-universal',
      installLayout: 'app-bundle',
      launcherRelativePath: 'Contents/MacOS/topiaforge_launcher',
      helperRelativePaths: [
        'Contents/Resources/TopiaForge/topiaforge',
        'Contents/Resources/TopiaForge/topiaforge-arm64',
        'Contents/Resources/TopiaForge/topiaforge-x64',
      ],
      helperExecutableName: 'topiaforge',
    ),
    _ => throw FormatException('Unsupported update E2E platform: $value'),
  };

  final String platformId;
  final String installLayout;
  final String launcherRelativePath;
  final List<String> helperRelativePaths;
  final String helperExecutableName;

  LauncherInstallationLayout layout(String targetRoot) =>
      LauncherInstallationLayout(
        platformId: platformId,
        installLayout: installLayout,
        targetRoot: targetRoot,
        launcherRelativePath: p.normalize(launcherRelativePath),
        helperRelativePaths: [
          for (final value in helperRelativePaths) p.normalize(value),
        ],
        helperExecutableName: helperExecutableName,
      );
}

final class _FixtureTransport implements LauncherUpdateTransport {
  _FixtureTransport(this.archive);

  final Uint8List archive;
  final Map<Uri, Uint8List> resources = {};

  void publish({
    required String tag,
    required Uint8List payload,
    required Uint8List signature,
    required List<String> artifacts,
  }) {
    resources
      ..clear()
      ..[_assetUri(tag, 'topiaforge-update-v1.json')] = payload
      ..[_assetUri(tag, 'topiaforge-update-v1.json.sig')] = signature;
    resources[LocalLauncherUpdateRepository.releasesApi] = Uint8List.fromList(
      utf8.encode(
        jsonEncode([
          {
            'tag_name': tag,
            'html_url':
                'https://github.com/furroxide/TopiaForge/releases/tag/$tag',
            'draft': false,
            'prerelease': true,
            'assets': [
              {
                'name': 'topiaforge-update-v1.json',
                'browser_download_url': _assetUri(
                  tag,
                  'topiaforge-update-v1.json',
                ).toString(),
                'size': payload.length,
              },
              {
                'name': 'topiaforge-update-v1.json.sig',
                'browser_download_url': _assetUri(
                  tag,
                  'topiaforge-update-v1.json.sig',
                ).toString(),
                'size': signature.length,
              },
              for (final name in artifacts)
                {
                  'name': name,
                  'browser_download_url': _assetUri(tag, name).toString(),
                  'size': archive.length,
                },
            ],
          },
        ]),
      ),
    );
  }

  @override
  Future<Uint8List> fetch(
    Uri uri, {
    required int maxBytes,
    required String label,
  }) async {
    final value = resources[uri];
    if (value == null) throw StateError('Missing E2E resource: $uri');
    if (value.length > maxBytes) throw StateError('$label is oversized.');
    return Uint8List.fromList(value);
  }

  @override
  Future<LauncherUpdateDownloadResult> download(
    Uri uri, {
    required File partialFile,
    required int expectedSize,
    required String expectedSha256,
    void Function(double progress)? onProgress,
  }) async {
    final actualHash = sha256.convert(archive).toString();
    if (expectedSize != archive.length || expectedSha256 != actualHash) {
      throw StateError('E2E archive metadata mismatch.');
    }
    partialFile.createSync(exclusive: true);
    partialFile.writeAsBytesSync(archive, flush: true);
    onProgress?.call(1);
    return LauncherUpdateDownloadResult(
      path: partialFile.path,
      size: archive.length,
      sha256: actualHash,
    );
  }

  @override
  void close() {}
}
