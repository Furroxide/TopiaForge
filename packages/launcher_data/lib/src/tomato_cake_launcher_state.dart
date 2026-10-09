import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;

/// Reads what the official Tomato Cake launcher records about its Windows
/// Robotopia install.
///
/// The launcher keeps its state in `%LOCALAPPDATA%\Tomato Cake\launcher`.
/// By default the game lives in that directory's `Robotopia` child, with
/// `installed-build.json` beside it. When a player moves the game in the
/// official launcher, `launcher-config.json` names the new parent directory
/// in `game_dir`. The game then lives at `<game_dir>\Robotopia` with its
/// current `filelist.json` beside it, while `installed-build.json` stays in
/// the state directory. A `filelist.json` left in the state directory
/// describes an earlier build, so nothing here reads it.
///
/// The relocation record is only trusted for two decisions: offering
/// `<game_dir>\Robotopia` as a discovery candidate, and attributing the
/// state directory's `installed-build.json` to that exact directory. Other
/// hosts report no state directory, which leaves the macOS and Proton
/// layouts unchanged.
final class TomatoCakeLauncherState {
  TomatoCakeLauncherState({
    Map<String, String>? environment,
    String? hostPlatform,
  }) : _environment = environment ?? Platform.environment,
       _hostPlatform = hostPlatform ?? Platform.operatingSystem;

  /// The relocation record inside the state directory.
  static const configFileName = 'launcher-config.json';

  /// The launcher-owned build marker inside the state directory.
  static const installedBuildFileName = 'installed-build.json';

  /// The game directory the launcher creates inside `game_dir`.
  static const gameDirectoryName = 'Robotopia';

  /// Upper bound for `launcher-config.json`. It matches the bound every
  /// TopiaForge reader applies to `installed-build.json`.
  static const maxConfigBytes = 64 * 1024;

  final Map<String, String> _environment;
  final String _hostPlatform;

  /// `%LOCALAPPDATA%\Tomato Cake\launcher` on Windows, or `null` on other
  /// hosts and when `LOCALAPPDATA` is not an absolute path.
  String? get stateDirectory {
    if (_hostPlatform != 'windows') return null;
    final localAppData = _environment['LOCALAPPDATA']?.trim() ?? '';
    if (localAppData.isEmpty || !p.isAbsolute(localAppData)) return null;
    return p.join(localAppData, 'Tomato Cake', 'launcher');
  }

  /// Reads `launcher-config.json` strictly.
  ///
  /// The file must be a regular file of at most [maxConfigBytes] bytes that
  /// holds one strict UTF-8 JSON object, with no byte order mark and no
  /// duplicate properties. Unknown properties are tolerated, as every
  /// `installed-build.json` reader already tolerates the launcher's extra
  /// `dev` field. `game_dir` must name an existing local directory by
  /// absolute path, and neither it nor any of its ancestors may be a link or
  /// other reparse point. Nothing here throws: an unusable record is
  /// reported as invalid.
  Future<TomatoCakeLauncherConfig> readConfig() async {
    final state = stateDirectory;
    if (state == null) return const TomatoCakeLauncherConfig.absent();
    final file = File(p.join(state, configFileName));
    try {
      final type = FileSystemEntity.typeSync(file.path, followLinks: false);
      if (type == FileSystemEntityType.notFound) {
        return const TomatoCakeLauncherConfig.absent();
      }
      if (type != FileSystemEntityType.file) {
        return const TomatoCakeLauncherConfig.invalid(
          '$configFileName is not a regular file.',
        );
      }
      final bytes = await _readStableBounded(file);
      // dart:convert silently drops a leading byte order mark; the C# and
      // PowerShell readers refuse one, so refuse it here too.
      if (bytes.length >= 3 &&
          bytes[0] == 0xef &&
          bytes[1] == 0xbb &&
          bytes[2] == 0xbf) {
        return const TomatoCakeLauncherConfig.invalid(
          '$configFileName must not start with a byte order mark.',
        );
      }
      final text = utf8.decode(bytes);
      rejectDuplicateJsonProperties(text, label: configFileName);
      final decoded = jsonDecode(text);
      if (decoded is! Map) {
        return const TomatoCakeLauncherConfig.invalid(
          '$configFileName must hold one JSON object.',
        );
      }
      final gameDirectory = decoded['game_dir'];
      if (gameDirectory is! String) {
        return const TomatoCakeLauncherConfig.invalid(
          '$configFileName has no string game_dir.',
        );
      }
      return TomatoCakeLauncherConfig.valid(
        _canonicalGameDirectory(gameDirectory),
      );
    } on FormatException catch (error) {
      return TomatoCakeLauncherConfig.invalid(error.message);
    } on FileSystemException catch (error) {
      return TomatoCakeLauncherConfig.invalid(
        '$configFileName could not be read: ${error.message}',
      );
    } on Object catch (error) {
      // Discovery drops an adapter that throws, which would also hide the
      // default install path. An unexpected failure is just an invalid record.
      return TomatoCakeLauncherConfig.invalid(
        '$configFileName could not be read: $error',
      );
    }
  }

  /// `<game_dir>\Robotopia` when `launcher-config.json` is valid and that
  /// directory exists as a real directory rather than a link.
  Future<String?> relocatedGameRoot() async {
    final gameDirectory = (await readConfig()).gameDirectory;
    if (gameDirectory == null) return null;
    final root = p.join(gameDirectory, gameDirectoryName);
    return FileSystemEntity.typeSync(root, followLinks: false) ==
            FileSystemEntityType.directory
        ? root
        : null;
  }

  /// The state directory's `installed-build.json`, but only for the install
  /// `launcher-config.json` points at.
  ///
  /// [gameRoot] must be `<game_dir>\Robotopia` itself: its canonical parent
  /// must equal the canonical `game_dir`, and no component of its path may
  /// be a link. Anything else returns `null`, so the marker can never be
  /// attributed to a different install.
  Future<File?> installedBuildMarkerFor(String gameRoot) async {
    final state = stateDirectory;
    if (state == null) return null;
    final gameDirectory = (await readConfig()).gameDirectory;
    if (gameDirectory == null) return null;
    final String root;
    try {
      root = _canonicalRealDirectory(gameRoot);
    } on Object {
      return null;
    }
    if (p.basename(root).toLowerCase() != gameDirectoryName.toLowerCase() ||
        !p.equals(p.dirname(root), gameDirectory)) {
      return null;
    }
    return File(p.join(state, installedBuildFileName));
  }

  /// Describes why [value] is not an absolute local directory path, or
  /// returns `null` when its shape is acceptable.
  ///
  /// With [windowsPaths], the path must start with a drive letter such as
  /// `D:\`. UNC shares and `\\?\` or `\\.\` device paths are refused, so
  /// reading the record can never reach out to a network host. Otherwise
  /// the path must start with `/`. Either way empty, `.` and `..` segments
  /// are refused. This only checks the text; [readConfig] also requires the
  /// directory to exist without links.
  static String? gameDirectoryProblem(
    String value, {
    required bool windowsPaths,
  }) {
    if (value.isEmpty) return 'game_dir is empty.';
    if (value.length > _maxGameDirectoryLength) {
      return 'game_dir exceeds $_maxGameDirectoryLength characters.';
    }
    if (value.trim() != value) return 'game_dir has surrounding whitespace.';
    if (value.codeUnits.any((unit) => unit < 0x20 || unit == 0x7f)) {
      return 'game_dir contains control characters.';
    }
    final List<String> segments;
    if (windowsPaths) {
      if (!RegExp(r'^[A-Za-z]:[\\/]').hasMatch(value)) {
        return 'game_dir must be an absolute path on a local drive letter.';
      }
      final rest = value.substring(3);
      if (RegExp(r'[:*?"<>|]').hasMatch(rest)) {
        return 'game_dir contains characters Windows paths cannot hold.';
      }
      segments = rest.split(RegExp(r'[\\/]'));
    } else {
      if (!value.startsWith('/')) return 'game_dir must be an absolute path.';
      segments = value.substring(1).split('/');
    }
    if (segments.isNotEmpty && segments.last.isEmpty) segments.removeLast();
    for (final segment in segments) {
      if (segment.isEmpty || segment == '.' || segment == '..') {
        return 'game_dir must not contain empty, "." or ".." segments.';
      }
      if (windowsPaths && (segment.endsWith('.') || segment.endsWith(' '))) {
        return 'game_dir segments must not end with a dot or a space.';
      }
    }
    return null;
  }

  String _canonicalGameDirectory(String value) {
    final problem = gameDirectoryProblem(
      value,
      windowsPaths: Platform.isWindows,
    );
    if (problem != null) throw FormatException(problem);
    try {
      return _canonicalRealDirectory(value);
    } on FileSystemException catch (error) {
      throw FormatException('game_dir ${error.message}: ${error.path}');
    }
  }
}

/// The outcome of [TomatoCakeLauncherState.readConfig].
final class TomatoCakeLauncherConfig {
  /// No state directory exists on this host, or it holds no record.
  const TomatoCakeLauncherConfig.absent()
    : gameDirectory = null,
      problem = null;

  /// A record exists but was refused for [problem].
  const TomatoCakeLauncherConfig.invalid(String this.problem)
    : gameDirectory = null;

  /// A record exists and names [gameDirectory], given canonically.
  const TomatoCakeLauncherConfig.valid(String this.gameDirectory)
    : problem = null;

  /// The canonical `game_dir` from a valid record.
  final String? gameDirectory;

  /// Why an existing record was refused.
  final String? problem;
}

const _maxGameDirectoryLength = 32767;

/// Returns the canonical form of an existing directory after checking that
/// no component from the volume root down to [path] is a link or other
/// reparse point. Canonicalising then only settles letter case and expands
/// Windows 8.3 short names, so the result names the same directory.
///
/// Throws a [FileSystemException] whose message reads as a predicate of the
/// offending path, such as "is not an existing directory".
String _canonicalRealDirectory(String path) {
  final normalized = p.normalize(Directory(path).absolute.path);
  final components = <String>[];
  for (var current = normalized; ; current = p.dirname(current)) {
    components.add(current);
    if (p.dirname(current) == current) break;
  }
  for (final component in components.reversed) {
    final type = FileSystemEntity.typeSync(component, followLinks: false);
    if (type != FileSystemEntityType.directory) {
      throw FileSystemException(
        type == FileSystemEntityType.link
            ? 'passes through a link or other reparse point'
            : 'is not an existing directory',
        component,
      );
    }
  }
  try {
    return p.normalize(Directory(normalized).resolveSymbolicLinksSync());
  } on FileSystemException {
    throw FileSystemException('could not be resolved', normalized);
  }
}

Future<Uint8List> _readStableBounded(File file) async {
  final label = TomatoCakeLauncherState.configFileName;
  final maxBytes = TomatoCakeLauncherState.maxConfigBytes;
  final before = file.statSync();
  if (before.size <= 0 || before.size > maxBytes) {
    throw FormatException('$label must be between 1 and $maxBytes bytes.');
  }
  final bytes = BytesBuilder(copy: false);
  await for (final chunk in file.openRead(0, maxBytes + 1)) {
    bytes.add(chunk);
    if (bytes.length > maxBytes) {
      throw FormatException('$label exceeds $maxBytes bytes.');
    }
  }
  final after = file.statSync();
  if (FileSystemEntity.typeSync(file.path, followLinks: false) !=
          FileSystemEntityType.file ||
      after.size != before.size ||
      after.modified != before.modified ||
      bytes.length != before.size) {
    throw FormatException('$label changed while it was read.');
  }
  return bytes.takeBytes();
}
