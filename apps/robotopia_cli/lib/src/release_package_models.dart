import 'dart:io';

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

extension ReleasePackagePlatformPaths on ReleasePackagePlatform {
  String get archiveName => switch (this) {
    ReleasePackagePlatform.windows => 'QuantumWorks-windows-x64.zip',
    ReleasePackagePlatform.linux => 'QuantumWorks-linux-x64.zip',
    ReleasePackagePlatform.macos => 'QuantumWorks-macos-universal.zip',
  };

  String get dotnetRuntimeId => switch (this) {
    ReleasePackagePlatform.windows => 'win-x64',
    ReleasePackagePlatform.linux => 'linux-x64',
    ReleasePackagePlatform.macos => _processIsArm64 ? 'osx-arm64' : 'osx-x64',
  };

  String get cliFileName =>
      this == ReleasePackagePlatform.windows ? 'robotopia.exe' : 'robotopia';

  String get gameCompatExtractorFileName =>
      this == ReleasePackagePlatform.windows
      ? 'Robotopia.GameCompat.Extractor.exe'
      : 'Robotopia.GameCompat.Extractor';

  String get bepInExBundleName => this == ReleasePackagePlatform.macos
      ? 'macos_universal_5.4.23.5'
      : 'win_x64_5.4.23.5';
}

bool get _processIsArm64 {
  final arch = Platform.version.toLowerCase();
  final envArch =
      Platform.environment['PROCESSOR_ARCHITECTURE'] ??
      Platform.environment['HOSTTYPE'] ??
      '';
  return arch.contains('arm64') ||
      arch.contains('aarch64') ||
      envArch.toLowerCase().contains('arm64') ||
      envArch.toLowerCase().contains('aarch64');
}
