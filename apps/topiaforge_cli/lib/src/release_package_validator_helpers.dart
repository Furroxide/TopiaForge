part of 'release_package_validator.dart';

extension _ReleasePackageValidationHelpers on ReleasePackageValidator {
  void _assertRuntimeLoaderProvenance(String payloadRoot) {
    final noticesRoot = p.join(
      payloadRoot,
      'third_party',
      'dotnet',
      'runtime-loader',
    );
    for (final name in runtimeLoaderNoticeNames) {
      _assertPath(
        p.join(noticesRoot, name),
        'Package must include managed loader dependency notices.',
      );
    }
    final provenance = readBoundedJsonObjectSync(
      File(p.join(noticesRoot, 'PROVENANCE.json')),
      maxBytes: CliFileLimits.metadata,
    );
    if (provenance['schemaVersion'] != 1 || provenance['packages'] is! List) {
      throw StateError('Managed loader dependency provenance is invalid.');
    }
    final packages = (provenance['packages'] as List)
        .whereType<Map<String, dynamic>>()
        .toList(growable: false);
    final loaderDir = p.join(
      payloadRoot,
      'src',
      'TopiaForge.ModManager',
      'bin',
      'Release',
      'netstandard2.1',
    );
    for (final assembly in releaseLoaderAssemblies.where(
      (entry) => entry.isPinnedPackage,
    )) {
      final entry = packages.where(
        (candidate) => candidate['id'] == assembly.packageId,
      );
      if (entry.length != 1) {
        throw StateError(
          'Managed loader provenance must contain ${assembly.packageId} exactly once.',
        );
      }
      final package = entry.single;
      if (package['version'] != assembly.packageVersion ||
          package['assembly'] != assembly.fileName ||
          package['assemblyVersion'] != assembly.assemblyVersion ||
          package['sha256'] != assembly.sha256 ||
          package['license'] != 'MIT' ||
          package['repositoryCommit'] != assembly.repositoryCommit) {
        throw StateError(
          'Managed loader provenance drifted for ${assembly.packageId}.',
        );
      }
      final dllHash = sha256
          .convert(File(p.join(loaderDir, assembly.fileName)).readAsBytesSync())
          .toString();
      if (dllHash != assembly.sha256) {
        throw StateError(
          'Packaged ${assembly.fileName} does not match its pinned SHA-256.',
        );
      }
      final noticesName = package['thirdPartyNotices'];
      if (noticesName is! String) {
        throw StateError('${assembly.packageId} notices path is invalid.');
      }
      final noticesHash = sha256
          .convert(File(p.join(noticesRoot, noticesName)).readAsBytesSync())
          .toString();
      if (noticesHash != assembly.thirdPartyNoticesSha256) {
        throw StateError(
          '${assembly.packageId} third-party notices do not match the pinned package.',
        );
      }
    }
    if (packages.length !=
        releaseLoaderAssemblies
            .where((entry) => entry.isPinnedPackage)
            .length) {
      throw StateError(
        'Managed loader provenance contains an unknown package.',
      );
    }
    final profileValues = provenance['playerProfileDependencies'];
    if (profileValues is! List) {
      throw StateError('Managed loader player-profile provenance is invalid.');
    }
    final profile = profileValues.whereType<Map<String, dynamic>>().toList();
    for (final assembly in topiaForgeRuntimeProfileAssemblies) {
      final entry = profile.where(
        (candidate) => candidate['assembly'] == assembly.fileName,
      );
      if (entry.length != 1 ||
          entry.single['assemblyVersion'] != assembly.assemblyVersion ||
          entry.single['sha256'] != assembly.sha256) {
        throw StateError(
          'Managed loader player-profile provenance drifted for ${assembly.fileName}.',
        );
      }
    }
    if (profile.length != topiaForgeRuntimeProfileAssemblies.length) {
      throw StateError(
        'Managed loader player-profile provenance contains an unknown assembly.',
      );
    }
  }

  Future<void> _requireSuccess(
    String executable,
    List<String> arguments, {
    required String label,
  }) async {
    final result = await processRunner.runResult(executable, arguments);
    if (result.exitCode != 0) {
      throw StateError('$label failed with exit ${result.exitCode}.');
    }
  }

  void _assertFlutterNotices(String launcherRoot) {
    final root = Directory(launcherRoot);
    final found =
        root.existsSync() &&
        root
            .listSync(recursive: true, followLinks: false)
            .whereType<File>()
            .any((file) => p.basename(file.path) == 'NOTICES.Z');
    if (!found) {
      throw StateError(
        'Flutter launcher must include its generated NOTICES.Z bundle.',
      );
    }
  }

  Future<void> _assertMacTrust(String appPath) async {
    if (!requireMacTrust) return;
    if (!Platform.isMacOS) {
      throw StateError('Final macOS trust validation must run on macOS.');
    }
    final teamId = expectedMacTeamId.trim().isNotEmpty
        ? expectedMacTeamId.trim()
        : (Platform.environment['MACOS_NOTARY_TEAM_ID'] ?? '').trim();
    if (teamId.isEmpty) {
      throw StateError('Expected macOS Developer Team ID is required.');
    }
    await _requireSuccess('codesign', [
      '--verify',
      '--deep',
      '--strict',
      '--verbose=4',
      appPath,
    ], label: 'app signature');
    for (final entity in Directory(
      appPath,
    ).listSync(recursive: true, followLinks: false).whereType<File>()) {
      if (!_hasMachOMagic(entity)) continue;
      await _requireSuccess('codesign', [
        '--verify',
        '--strict',
        '--verbose=4',
        entity.path,
      ], label: p.relative(entity.path, from: appPath));
      final details = await processRunner.runResult('codesign', [
        '-d',
        '--verbose=4',
        entity.path,
      ]);
      final output = '${details.stdout}\n${details.stderr}';
      if (details.exitCode != 0 ||
          output.contains('Signature=adhoc') ||
          !output.contains('Authority=Developer ID Application:') ||
          !output.contains('TeamIdentifier=$teamId')) {
        throw StateError(
          'macOS code-signing identity or Team ID is invalid for '
          '${p.relative(entity.path, from: appPath)}.',
        );
      }
    }
    await _requireSuccess('xcrun', [
      'stapler',
      'validate',
      appPath,
    ], label: 'notarization ticket');
    await _requireSuccess('xattr', [
      '-w',
      'com.apple.quarantine',
      '0081;00000000;TopiaForge release validation;',
      appPath,
    ], label: 'quarantine simulation');
    await _requireSuccess('spctl', [
      '--assess',
      '--type',
      'execute',
      '--verbose=4',
      appPath,
    ], label: 'Gatekeeper assessment');
  }

  void _assertPath(String path, String message) {
    if (!FileSystemEntity.typeSync(path).exists) {
      throw StateError('$message Missing path: $path');
    }
  }
}

extension on FileSystemEntityType {
  bool get exists => this != FileSystemEntityType.notFound;
}
