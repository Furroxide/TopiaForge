part of 'game_install_discovery.dart';

/// Discovers Robotopia only from Steam's declared libraries and app manifests.
///
/// Robotopia has no repository-owned Steam app id. This adapter therefore
/// enumerates `appmanifest_*.acf` files and requires both the exact app name
/// and install directory `Robotopia`; it never guesses from a folder name.
class SteamGameInstallDiscoveryAdapter implements GameInstallDiscoveryAdapter {
  SteamGameInstallDiscoveryAdapter({
    Map<String, String>? environment,
    String? hostPlatform,
    Iterable<String>? steamRoots,
    this.maxMetadataBytes = 1024 * 1024,
  }) : _environment = environment ?? Platform.environment,
       _hostPlatform = hostPlatform ?? Platform.operatingSystem,
       _configuredRoots = steamRoots == null
           ? null
           : List.unmodifiable(steamRoots);

  final Map<String, String> _environment;
  final String _hostPlatform;
  final List<String>? _configuredRoots;
  final int maxMetadataBytes;

  @override
  GameInstallDiscoverySource get source => GameInstallDiscoverySources.steam;

  @override
  Future<List<String>> discoverPaths() async {
    final installs = <String>{};
    for (final steamRoot in _steamRoots()) {
      for (final library in await _libraryRoots(steamRoot)) {
        final steamApps = Directory(p.join(library, 'steamapps'));
        final manifests = await _appManifests(steamApps);
        for (final manifest in manifests) {
          final fields = await _readKeyValues(manifest);
          if (fields == null ||
              fields['name'] != 'Robotopia' ||
              fields['installdir'] != 'Robotopia') {
            continue;
          }
          installs.add(p.join(steamApps.path, 'common', 'Robotopia'));
        }
      }
    }
    final result = installs.toList()..sort();
    return result;
  }

  List<String> _steamRoots() {
    final configured = _configuredRoots;
    if (configured != null) {
      return _normalizedUnique(configured);
    }

    final roots = <String>[];
    void addEnvironmentPath(String key) {
      final value = _environment[key]?.trim() ?? '';
      if (value.isNotEmpty) roots.add(value);
    }

    addEnvironmentPath('STEAM_PATH');
    addEnvironmentPath('STEAM_COMPAT_CLIENT_INSTALL_PATH');
    final home = _environment['HOME']?.trim() ?? '';
    if (_hostPlatform == 'windows') {
      for (final key in const ['PROGRAMFILES(X86)', 'PROGRAMFILES']) {
        final root = _environment[key]?.trim() ?? '';
        if (root.isNotEmpty) roots.add(p.join(root, 'Steam'));
      }
    } else if (_hostPlatform == 'macos' && home.isNotEmpty) {
      roots.add(p.join(home, 'Library', 'Application Support', 'Steam'));
    } else if (_hostPlatform == 'linux' && home.isNotEmpty) {
      roots.addAll([
        p.join(home, '.steam', 'steam'),
        p.join(home, '.local', 'share', 'Steam'),
        p.join(home, '.var', 'app', 'com.valvesoftware.Steam', 'data', 'Steam'),
      ]);
    }
    return _normalizedUnique(roots);
  }

  Future<List<String>> _libraryRoots(String steamRoot) async {
    final roots = <String>[steamRoot];
    final libraryFile = File(
      p.join(steamRoot, 'steamapps', 'libraryfolders.vdf'),
    );
    final text = await _readMetadata(libraryFile);
    if (text != null) {
      for (final pair in _parsePairs(text)) {
        final key = pair.$1.toLowerCase();
        final value = pair.$2.trim();
        final isOldLibraryEntry = RegExp(r'^\d+$').hasMatch(key);
        if ((key == 'path' || isOldLibraryEntry) && _isAbsolutePath(value)) {
          roots.add(value);
        }
      }
    }
    return _normalizedUnique(roots);
  }

  Future<List<File>> _appManifests(Directory steamApps) async {
    if (FileSystemEntity.typeSync(steamApps.path, followLinks: false) !=
        FileSystemEntityType.directory) {
      return const [];
    }
    try {
      final manifests = await steamApps
          .list(followLinks: false)
          .where(
            (entity) =>
                entity is File &&
                RegExp(
                  r'^appmanifest_\d+\.acf$',
                ).hasMatch(p.basename(entity.path)),
          )
          .cast<File>()
          .toList();
      manifests.sort((left, right) => left.path.compareTo(right.path));
      return manifests;
    } on FileSystemException {
      return const [];
    }
  }

  Future<Map<String, String>?> _readKeyValues(File file) async {
    final text = await _readMetadata(file);
    if (text == null) return null;
    return {
      for (final pair in _parsePairs(text)) pair.$1.toLowerCase(): pair.$2,
    };
  }

  Future<String?> _readMetadata(File file) async {
    try {
      if (FileSystemEntity.typeSync(file.path, followLinks: false) !=
          FileSystemEntityType.file) {
        return null;
      }
      final handle = await file.open();
      try {
        final bytes = await handle.read(maxMetadataBytes + 1);
        if (bytes.length > maxMetadataBytes) return null;
        return utf8.decode(bytes, allowMalformed: false);
      } finally {
        await handle.close();
      }
    } on Object {
      return null;
    }
  }

  Iterable<(String, String)> _parsePairs(String value) sync* {
    final pattern = RegExp(r'"((?:\\.|[^"\\])*)"\s*"((?:\\.|[^"\\])*)"');
    for (final match in pattern.allMatches(value)) {
      yield (_decodeVdf(match.group(1)!), _decodeVdf(match.group(2)!));
    }
  }

  String _decodeVdf(String value) {
    const marker = '\u0000';
    return value
        .replaceAll(r'\\', marker)
        .replaceAll(r'\"', '"')
        .replaceAll(marker, '\\');
  }

  bool _isAbsolutePath(String value) {
    return p.isAbsolute(value) ||
        RegExp(r'^[A-Za-z]:[\\/]').hasMatch(value) ||
        value.startsWith(r'\\');
  }

  List<String> _normalizedUnique(Iterable<String> values) {
    final seen = <String>{};
    final result = <String>[];
    for (final raw in values) {
      final value = raw.trim();
      if (value.isEmpty) continue;
      final normalized = p.normalize(Directory(value).absolute.path);
      final identity = _hostPlatform == 'windows'
          ? normalized.toLowerCase()
          : normalized;
      if (seen.add(identity)) result.add(normalized);
    }
    result.sort();
    return result;
  }
}
