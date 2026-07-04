part of '../local_launcher_repository.dart';

/// Resolves and describes how a Robotopia install is laid out on disk.
///
/// All platform-specific knowledge about the game payload lives here: where
/// the executable and managed assemblies are, which vendored BepInEx bundle
/// applies, which files prove BepInEx is installed, and what environment the
/// game needs so doorstop injects.
///
/// The launcher branches on the *install's* layout rather than the host OS so
/// that every layout is exercisable in tests on any development machine.
class GameLayout {
  const GameLayout._(this.kind, this.gameRoot);

  final GameInstallLayout kind;

  /// The directory the launcher treats as the install root: the folder
  /// containing `Robotopia.exe` (Windows/Proton) or the folder containing
  /// `Robotopia.app` (macOS). `BepInEx/` and the mod manager state always
  /// live directly under this root, so path helpers are layout-agnostic.
  final String gameRoot;

  static const _windowsExecutable = 'Robotopia.exe';
  static const _macBundleName = 'Robotopia.app';

  /// Resolves the layout of [path], or returns `null` when no game payload is
  /// recognised there.
  ///
  /// [hostPlatform] takes `Platform.operatingSystem` values and is injectable
  /// for tests. A `Robotopia.exe` folder on a non-Windows host is a
  /// Proton/Wine install (also covers CrossOver on macOS).
  static GameLayout? resolve(String path, {String? hostPlatform}) {
    final host = hostPlatform ?? Platform.operatingSystem;
    var root = Directory(path).absolute.path;
    // A file picker may hand us the .app bundle itself; the install root is
    // the directory that contains it.
    if (p.basename(root) == _macBundleName) {
      root = p.dirname(root);
    }

    final macExecutable = File(
      p.join(root, _macBundleName, 'Contents', 'MacOS', 'robotopia'),
    );
    if (macExecutable.existsSync()) {
      return GameLayout._(GameInstallLayout.macAppBundle, root);
    }

    if (File(p.join(root, _windowsExecutable)).existsSync()) {
      return GameLayout._(
        host == 'windows'
            ? GameInstallLayout.windowsNative
            : GameInstallLayout.linuxProton,
        root,
      );
    }

    return null;
  }

  String get executablePath => switch (kind) {
    GameInstallLayout.macAppBundle => p.join(
      gameRoot,
      _macBundleName,
      'Contents',
      'MacOS',
      'robotopia',
    ),
    _ => p.join(gameRoot, _windowsExecutable),
  };

  String get managedDirPath => switch (kind) {
    GameInstallLayout.macAppBundle => p.join(
      gameRoot,
      _macBundleName,
      'Contents',
      'Resources',
      'Data',
      'Managed',
    ),
    _ => p.join(gameRoot, 'Robotopia_Data', 'Managed'),
  };

  /// What to call the game payload in user-facing messages.
  String get executableDisplayName => switch (kind) {
    GameInstallLayout.macAppBundle => _macBundleName,
    _ => _windowsExecutable,
  };

  /// Directory name under `third_party/BepInEx/` holding the runtime bundle
  /// for this layout. Proton installs run the Windows game, so they take the
  /// Windows bundle.
  String get bepInExBundleDirName => switch (kind) {
    GameInstallLayout.macAppBundle =>
      'macos_universal_${LocalLauncherRepository._bepInExVersion}',
    _ => 'win_x64_${LocalLauncherRepository._bepInExVersion}',
  };

  /// Game-root-relative files whose presence marks a complete BepInEx
  /// install for this layout.
  List<String> get bepInExMarkerFiles => switch (kind) {
    GameInstallLayout.macAppBundle => const [
      'run_bepinex.sh',
      'libdoorstop.dylib',
      'BepInEx/core/BepInEx.dll',
    ],
    _ => const [
      'winhttp.dll',
      'doorstop_config.ini',
      'BepInEx/core/BepInEx.dll',
    ],
  };

  /// Files installed by [bepInExBundleDirName] that must keep their Unix
  /// executable bit after being copied (Dart's copySync drops it).
  List<String> get executableRuntimeFiles => switch (kind) {
    GameInstallLayout.macAppBundle => const [
      'run_bepinex.sh',
      'libdoorstop.dylib',
    ],
    _ => const [],
  };

  /// Environment the game process needs for the mod loader to inject,
  /// mirroring the vendored `run_bepinex.sh` (Doorstop 4 variable names).
  /// Launching the executable directly with these set avoids the script's
  /// `arch -e` workaround, which only exists because shells strip DYLD_*
  /// variables across the `arch` boundary.
  Map<String, String> launchEnvironment() => switch (kind) {
    GameInstallLayout.macAppBundle => {
      'DOORSTOP_ENABLED': '1',
      'DOORSTOP_TARGET_ASSEMBLY': p.join(
        gameRoot,
        'BepInEx',
        'core',
        'BepInEx.Preloader.dll',
      ),
      'DOORSTOP_IGNORE_DISABLED_ENV': '0',
      'DYLD_LIBRARY_PATH': gameRoot,
      'DYLD_INSERT_LIBRARIES': p.join(gameRoot, 'libdoorstop.dylib'),
    },
    GameInstallLayout.linuxProton => {'WINEDLLOVERRIDES': 'winhttp=n,b'},
    GameInstallLayout.windowsNative => const {},
  };
}
