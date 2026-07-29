part of 'launcher_bloc.dart';

String? _launcherGameArchitecture(GameInstall? install) {
  final architecture = install?.architecture ?? '';
  return architecture.isEmpty ? null : architecture;
}

String? _launcherGamePlatform(GameInstall? install) =>
    switch (install?.layout) {
      GameInstallLayout.macAppBundle => 'macos',
      GameInstallLayout.windowsNative ||
      GameInstallLayout.linuxProton => 'windows',
      null => null,
    };

List<String> _launcherGameContentTargets(GameInstall? install) =>
    switch (install?.layout) {
      GameInstallLayout.macAppBundle => const ['code', 'standaloneosx'],
      GameInstallLayout.windowsNative ||
      GameInstallLayout.linuxProton => const ['code', 'standalonewindows64'],
      null => const [],
    };
