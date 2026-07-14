enum ReleasePackagePlatform {
  windows('windows'),
  linux('linux'),
  macos('macos');

  const ReleasePackagePlatform(this.id);

  final String id;

  static ReleasePackagePlatform parse(String value) {
    for (final platform in values) {
      if (platform.id == value) {
        return platform;
      }
    }
    throw ArgumentError.value(
      value,
      'platform',
      'Expected windows, linux, or macos.',
    );
  }
}

const macCliArm64FileName = 'topiaforge-arm64';
const macCliX64FileName = 'topiaforge-x64';

extension ReleasePackagePlatformPaths on ReleasePackagePlatform {
  String get archiveName => switch (this) {
    ReleasePackagePlatform.windows => 'TopiaForge-windows-x64.zip',
    ReleasePackagePlatform.linux => 'TopiaForge-linux-x64.zip',
    ReleasePackagePlatform.macos => 'TopiaForge-macos-universal.zip',
  };

  List<String> get gameCompatExtractorRuntimeIds => switch (this) {
    ReleasePackagePlatform.windows => const ['win-x64'],
    ReleasePackagePlatform.linux => const ['linux-x64'],
    ReleasePackagePlatform.macos => const ['osx-x64', 'osx-arm64'],
  };

  String get cliFileName =>
      this == ReleasePackagePlatform.windows ? 'topiaforge.exe' : 'topiaforge';

  String get gameCompatExtractorFileName =>
      this == ReleasePackagePlatform.windows
      ? 'TopiaForge.GameCompat.Extractor.exe'
      : 'TopiaForge.GameCompat.Extractor';

  String get bepInExBundleName => this == ReleasePackagePlatform.macos
      ? 'macos_universal_5.4.23.5'
      : 'win_x64_5.4.23.5';
}
