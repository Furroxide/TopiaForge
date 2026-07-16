part of '../local_developer_repository.dart';

final class _SourceSdkProject {
  const _SourceSdkProject({
    required this.name,
    required this.projectFile,
    required this.targetFramework,
    required this.dependencies,
  });

  final String name;
  final File projectFile;
  final String targetFramework;
  final List<String> dependencies;

  bool get isAnalyzer => name == 'TopiaForge.Mods.Analyzers';

  File get implementation => File(
    p.join(
      projectFile.parent.path,
      'bin',
      'Release',
      targetFramework,
      '$name.dll',
    ),
  );

  File get documentation => File(
    p.join(
      projectFile.parent.path,
      'bin',
      'Release',
      targetFramework,
      '$name.xml',
    ),
  );

  File get reference => File(
    p.join(
      projectFile.parent.path,
      'obj',
      'Release',
      targetFramework,
      'ref',
      '$name.dll',
    ),
  );
}

final class _DeveloperSdkLock {
  _DeveloperSdkLock({
    required this.sdkVersion,
    required this.gameVersion,
    required this.manifestSha256,
    required this.dotnetSdkVersion,
    required this.toolVersion,
  });

  final String sdkVersion;
  final String gameVersion;
  final String manifestSha256;
  final String dotnetSdkVersion;
  final String toolVersion;

  void validate() {
    final semver = RegExp(
      r'^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$',
    );
    if (!semver.hasMatch(sdkVersion) ||
        !semver.hasMatch(gameVersion) ||
        !semver.hasMatch(dotnetSdkVersion) ||
        (toolVersion.isNotEmpty && !semver.hasMatch(toolVersion)) ||
        !RegExp(r'^[0-9a-f]{64}$').hasMatch(manifestSha256)) {
      throw StateError('Invalid TopiaForge SDK lock file.');
    }
  }
}
