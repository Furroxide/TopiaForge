import 'dart:convert';
import 'dart:io';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;

import '../bounded_process.dart';

/// Runs one command without a shell and returns its result.
typedef SessionLinkCommandRunner =
    Future<BoundedProcessResult> Function(
      String executable,
      List<String> arguments,
    );

/// Registers this launcher for `topiaforge://` links the freedesktop way: a
/// hidden desktop entry for `x-scheme-handler/topiaforge` in the user's data
/// directory, made the default handler with `xdg-mime`.
///
/// It writes only when the entry is missing, differs, or is not the default.
/// The desktop launcher passes the link to the launcher as one argument
/// (`%u`); no shell sees it. Removal deletes the entry only while it names
/// this launcher. A snap or Flatpak sandbox cannot register for the host,
/// so there it reports [SessionLinkRegistration.unsupported].
final class LinuxSessionLinkRegistrar implements SessionLinkRegistrar {
  LinuxSessionLinkRegistrar({
    required this.executable,
    Map<String, String>? environment,
    SessionLinkCommandRunner? runCommand,
  }) : _environment = environment ?? Platform.environment,
       _runCommand = runCommand ?? _runBounded;

  static const desktopFileName = 'io.github.furroxide.topiaforge.links.desktop';
  static const mimeType = 'x-scheme-handler/topiaforge';
  static const _maximumEntryBytes = 16 * 1024;

  /// The launcher the entry names.
  final String executable;

  final Map<String, String> _environment;
  final SessionLinkCommandRunner _runCommand;

  /// The directory the entry goes in, or null when there is no usable one.
  String? get applicationsDirectory {
    final dataHome = _environment['XDG_DATA_HOME'] ?? '';
    // The base directory specification ignores a relative path.
    if (p.isAbsolute(dataHome)) return p.join(dataHome, 'applications');
    final home = _environment['HOME'] ?? '';
    if (!p.isAbsolute(home)) return null;
    return p.join(home, '.local', 'share', 'applications');
  }

  bool get _sandboxed =>
      (_environment['SNAP'] ?? '').isNotEmpty ||
      (_environment['FLATPAK_ID'] ?? '').isNotEmpty;

  /// The desktop entry for [executable].
  String get desktopEntry => [
    '[Desktop Entry]',
    'Type=Application',
    'Name=TopiaForge',
    'Comment=Opens topiaforge:// session links in the TopiaForge launcher',
    'Exec="$executable" %u',
    'Terminal=false',
    'NoDisplay=true',
    'MimeType=$mimeType;',
    '',
  ].join('\n');

  @override
  Future<SessionLinkRegistrationResult> register() async {
    if (_sandboxed) return _unsupported;
    final directory = applicationsDirectory;
    if (directory == null) return _noDataDirectory;
    final problem = executableProblem(executable);
    if (problem != null) {
      return SessionLinkRegistrationResult(
        SessionLinkRegistration.failed,
        detail: problem,
      );
    }
    try {
      final entry = File(p.join(directory, desktopFileName));
      final entryCurrent = _read(entry) == desktopEntry;
      if (entryCurrent && await _queryDefault() == desktopFileName) {
        return const SessionLinkRegistrationResult(
          SessionLinkRegistration.alreadyRegistered,
        );
      }
      if (!entryCurrent) _write(entry, desktopEntry);
      final result = await _runCommand('xdg-mime', [
        'default',
        desktopFileName,
        mimeType,
      ]);
      if (result.exitCode != 0) {
        return const SessionLinkRegistrationResult(
          SessionLinkRegistration.failed,
          detail: 'xdg-mime did not make TopiaForge the link handler.',
        );
      }
      return const SessionLinkRegistrationResult(
        SessionLinkRegistration.registered,
      );
    } on ProcessException {
      return const SessionLinkRegistrationResult(
        SessionLinkRegistration.failed,
        detail: 'xdg-mime is not installed, so links cannot be registered.',
      );
    } on Object catch (error) {
      return SessionLinkRegistrationResult(
        SessionLinkRegistration.failed,
        detail: 'The link registration could not be written: $error',
      );
    }
  }

  @override
  Future<SessionLinkRegistrationResult> unregister() async {
    if (_sandboxed) return _unsupported;
    final directory = applicationsDirectory;
    if (directory == null) return _noDataDirectory;
    try {
      final entry = File(p.join(directory, desktopFileName));
      if (_read(entry) != desktopEntry) {
        return const SessionLinkRegistrationResult(
          SessionLinkRegistration.notRegistered,
        );
      }
      entry.deleteSync();
      return const SessionLinkRegistrationResult(
        SessionLinkRegistration.removed,
      );
    } on Object catch (error) {
      return SessionLinkRegistrationResult(
        SessionLinkRegistration.failed,
        detail: 'The link registration could not be removed: $error',
      );
    }
  }

  Future<String?> _queryDefault() async {
    try {
      final result = await _runCommand('xdg-mime', [
        'query',
        'default',
        mimeType,
      ]);
      return result.exitCode == 0 ? result.stdout.trim() : null;
    } on ProcessException {
      rethrow;
    } on Object {
      return null;
    }
  }

  /// The entry's text, or null when it is missing, not a regular file, too
  /// large or not UTF-8.
  static String? _read(File entry) {
    if (FileSystemEntity.typeSync(entry.path, followLinks: false) !=
            FileSystemEntityType.file ||
        entry.lengthSync() > _maximumEntryBytes) {
      return null;
    }
    try {
      return utf8.decode(entry.readAsBytesSync());
    } on FormatException {
      return null;
    }
  }

  static void _write(File entry, String text) {
    entry.parent.createSync(recursive: true);
    final temporary = File(
      p.join(entry.parent.path, '.$desktopFileName.tmp-$pid'),
    );
    temporary.writeAsStringSync(text, flush: true);
    temporary.renameSync(entry.path);
  }

  /// Why [executable] cannot go into the entry's quoted `Exec` line, or
  /// null. Quotes, backquotes, dollar signs, backslashes and percent signs
  /// have meanings there, and a control character would end the line.
  static String? executableProblem(String executable) {
    if (!p.posix.isAbsolute(executable)) {
      return 'The launcher path is not absolute.';
    }
    for (final unit in executable.codeUnits) {
      if (unit < 0x20 ||
          unit == 0x7F ||
          unit == 0x22 ||
          unit == 0x24 ||
          unit == 0x25 ||
          unit == 0x5C ||
          unit == 0x60) {
        return 'The launcher path holds a character a desktop entry cannot '
            'quote.';
      }
    }
    return null;
  }

  static const _unsupported = SessionLinkRegistrationResult(
    SessionLinkRegistration.unsupported,
    detail: 'A sandboxed package registers links through its own package.',
  );

  static const _noDataDirectory = SessionLinkRegistrationResult(
    SessionLinkRegistration.failed,
    detail: 'No data directory is set (HOME or XDG_DATA_HOME).',
  );

  static Future<BoundedProcessResult> _runBounded(
    String executable,
    List<String> arguments,
  ) => runBoundedProcess(
    executable,
    arguments,
    timeout: const Duration(seconds: 10),
    maxStdoutBytes: 64 * 1024,
    maxStderrBytes: 64 * 1024,
  );
}
