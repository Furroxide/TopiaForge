part of '../local_launcher_repository.dart';

extension _ProfileLaunchHelpers on LocalLauncherRepository {
  InstalledMod _profileEnabledMod(InstalledMod mod) => InstalledMod(
    id: mod.id,
    name: mod.name,
    version: mod.version,
    enabled: true,
    restartRequired: mod.restartRequired,
    uninstallPending: mod.uninstallPending,
    packagePath: mod.packagePath,
    manifest: mod.manifest,
    installedAtUtc: mod.installedAtUtc,
    updatedAtUtc: mod.updatedAtUtc,
    errors: mod.errors,
    versionPinned: mod.versionPinned,
    requestedVersion: mod.requestedVersion,
    selectionReason: mod.selectionReason,
    installedVersions: mod.installedVersions,
    sourceSha256: mod.sourceSha256,
    trust: mod.trust,
    repairable: mod.repairable,
  );

  Map<String, String> _profileLaunchEnvironment(
    GameLayout layout,
    LauncherProfile profile,
    String configurationPath,
  ) {
    final required = layout.launchEnvironment();
    final reserved = {
      ProfileLaunchConfigurationV4.environmentVariable.toLowerCase(),
      AcceptanceIsolationContext.environmentVariable.toLowerCase(),
      ...required.keys.map((key) => key.toLowerCase()),
    };
    final environment = <String, String>{};
    for (final entry in profile.launchSettings.environment.entries) {
      final key = entry.key;
      if (key.isEmpty ||
          key.contains('=') ||
          key.contains('\u0000') ||
          entry.value.contains('\u0000')) {
        throw FormatException('Profile contains an invalid environment entry.');
      }
      if (reserved.contains(key.toLowerCase())) {
        throw FormatException(
          'Profile environment cannot replace required variable $key.',
        );
      }
      environment[key] = entry.value;
    }
    environment.addAll(required);
    environment[ProfileLaunchConfigurationV4.environmentVariable] =
        configurationPath;
    return environment;
  }
}

Future<Uint8List> _readLauncherFileBounded(File file, int maxBytes) async {
  if (maxBytes < 0) {
    throw ArgumentError.value(maxBytes, 'maxBytes', 'must not be negative');
  }
  final type = FileSystemEntity.typeSync(file.path, followLinks: false);
  if (type == FileSystemEntityType.link) {
    throw StateError('${p.basename(file.path)} cannot be a symbolic link.');
  }
  if (type != FileSystemEntityType.file) {
    throw StateError('${p.basename(file.path)} is not a regular file.');
  }
  if (await file.length() > maxBytes) {
    throw StateError('${p.basename(file.path)} exceeds $maxBytes bytes.');
  }
  final bytes = BytesBuilder(copy: false);
  var length = 0;
  await for (final chunk in file.openRead()) {
    length += chunk.length;
    if (length > maxBytes) {
      throw StateError('${p.basename(file.path)} exceeds $maxBytes bytes.');
    }
    bytes.add(chunk);
  }
  return bytes.takeBytes();
}
