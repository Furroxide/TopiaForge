part of '../local_launcher_repository.dart';

const _bepInExVersion = '5.4.23.5';
const _loaderVersion = TopiaForgeRuntimeVersions.loaderVersion;
const _sdkVersion = TopiaForgeRuntimeVersions.sdkVersion;

/// One managed assembly installed beside the TopiaForge BepInEx loader.
final class TopiaForgeRuntimeAssembly {
  /// Creates a first-party assembly entry or a pinned NuGet dependency entry.
  const TopiaForgeRuntimeAssembly({
    required this.fileName,
    this.packageId = '',
    this.packageVersion = '',
    this.assemblyVersion = '',
    this.sha256 = '',
    this.thirdPartyNoticesSha256 = '',
    this.repositoryCommit = '',
  });

  /// File name used in the loader directory.
  final String fileName;

  /// NuGet package identity when this is a third-party runtime dependency.
  final String packageId;

  /// Exact restored NuGet package version.
  final String packageVersion;

  /// Exact managed assembly identity version.
  final String assemblyVersion;

  /// SHA-256 of the shipped netstandard2.0 DLL.
  final String sha256;

  /// SHA-256 of the package's redistributed third-party notices.
  final String thirdPartyNoticesSha256;

  /// Upstream dotnet/dotnet commit recorded by the signed NuGet package.
  final String repositoryCommit;

  /// Whether this entry is a pinned third-party package assembly.
  bool get isPinnedPackage => packageId.isNotEmpty;
}

/// A system assembly supplied by Robotopia's pinned Unity/Mono player profile.
final class TopiaForgeRuntimeProfileAssembly {
  /// Creates an exact build-2227 player-profile requirement.
  const TopiaForgeRuntimeProfileAssembly({
    required this.fileName,
    required this.assemblyVersion,
    required this.sha256,
  });

  /// File name in Robotopia's Managed directory.
  final String fileName;

  /// Managed assembly identity shipped by build 2227.
  final String assemblyVersion;

  /// SHA-256 shared by the pinned Windows and macOS build-2227 archives.
  final String sha256;
}

/// Complete managed payload installed beside the TopiaForge BepInEx loader.
///
/// Keep runtime detection, atomic repair, and release packaging on this one
/// inventory. The two System assemblies are the package validator dependencies
/// absent from Robotopia and BepInEx. Unity's own Memory/Buffers/Unsafe facades
/// remain player-owned and are verified separately below.
const topiaForgeRuntimeLoaderAssemblies = <TopiaForgeRuntimeAssembly>[
  TopiaForgeRuntimeAssembly(
    fileName: 'System.Collections.Immutable.dll',
    packageId: 'System.Collections.Immutable',
    packageVersion: '10.0.9',
    assemblyVersion: '10.0.0.0',
    sha256: '98de9f34c748b709f26c07fd4df54b2509218511dfaf741fa61aa2adb74e8c8c',
    thirdPartyNoticesSha256:
        '6d15e10a101c6bfff2ab4429ed061bf76c456fc4b23ad6b03e0d0f8377148a21',
    repositoryCommit: '901ca941248413c79832d2fdbd709da0c4386353',
  ),
  TopiaForgeRuntimeAssembly(
    fileName: 'System.Reflection.Metadata.dll',
    packageId: 'System.Reflection.Metadata',
    packageVersion: '10.0.9',
    assemblyVersion: '10.0.0.0',
    sha256: 'a0f6273f959a1ae587de408464aaad8cab9b6ae262a650d7b33a87208052ad3b',
    thirdPartyNoticesSha256:
        '6d15e10a101c6bfff2ab4429ed061bf76c456fc4b23ad6b03e0d0f8377148a21',
    repositoryCommit: '901ca941248413c79832d2fdbd709da0c4386353',
  ),
  TopiaForgeRuntimeAssembly(fileName: 'TopiaForge.ModManager.dll'),
  TopiaForgeRuntimeAssembly(fileName: 'TopiaForge.ModManager.Core.dll'),
  TopiaForgeRuntimeAssembly(fileName: 'TopiaForge.Mods.Abstractions.dll'),
  TopiaForgeRuntimeAssembly(fileName: 'TopiaForge.Mods.Chronos.dll'),
  TopiaForgeRuntimeAssembly(fileName: 'TopiaForge.Mods.Interop.Unity.dll'),
  TopiaForgeRuntimeAssembly(fileName: 'TopiaForge.Mods.Prompts.dll'),
  TopiaForgeRuntimeAssembly(fileName: 'TopiaForge.Mods.RobotKit.dll'),
  TopiaForgeRuntimeAssembly(fileName: 'TopiaForge.Mods.Ugc.dll'),
  TopiaForgeRuntimeAssembly(fileName: 'TopiaForge.Mods.UnityUi.dll'),
  TopiaForgeRuntimeAssembly(fileName: 'TopiaForge.Mods.Worlds.dll'),
];

/// File-name view retained for loader detection and repair call sites.
final topiaForgeRuntimeLoaderDlls = List<String>.unmodifiable(
  topiaForgeRuntimeLoaderAssemblies.map((assembly) => assembly.fileName),
);

/// Transitive metadata-reader dependencies supplied by Robotopia build 2227.
const topiaForgeRuntimeProfileAssemblies = <TopiaForgeRuntimeProfileAssembly>[
  TopiaForgeRuntimeProfileAssembly(
    fileName: 'System.Buffers.dll',
    assemblyVersion: '4.0.99.0',
    sha256: '762f8fdbe975e05b76be5fe996c53ce7c75e4a2830f2f50b02a5948ef6ba0aeb',
  ),
  TopiaForgeRuntimeProfileAssembly(
    fileName: 'System.Memory.dll',
    assemblyVersion: '4.0.99.0',
    sha256: 'c4f030a2cba7da7cdcf493257c24560e203d355904aee490d645a935842f834a',
  ),
  TopiaForgeRuntimeProfileAssembly(
    fileName: 'System.Runtime.CompilerServices.Unsafe.dll',
    assemblyVersion: '6.0.0.0',
    sha256: 'c0c628ecea65b4261cb88a1c322a3596bbde1dc2df102b88d63bab8c1a48d57a',
  ),
];

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
    GameInstallLayout.macAppBundle => 'macos_universal_$_bepInExVersion',
    _ => 'win_x64_$_bepInExVersion',
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
