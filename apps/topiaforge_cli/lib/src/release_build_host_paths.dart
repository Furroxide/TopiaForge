import 'dart:io';
import 'dart:typed_data';

import 'package:path/path.dart' as p;

/// Fails a package that carries the build machine's home directory inside a
/// shipped binary.
///
/// Compilers record where they ran. Roslyn writes the absolute PDB path into
/// each assembly's CodeView debug directory, and the Dart AOT snapshot records
/// a `file:///` URI for every library outside a `package:` root. Neither is
/// visible in the package listing, so the only thing that catches it is a scan
/// of the bytes.
///
/// `release/ReleaseOperations.md` requires that "credentials, usernames,
/// hostnames, local paths, and timestamps stay off GitHub", and a platform
/// archive is a published asset. A build that leaks the administrator's user
/// name therefore has to fail here rather than at a reviewer's discretion.
///
/// Only home directories are treated as a violation. A neutral absolute build
/// root such as `/_/` (Roslyn's `PathMap` target), `/build`, or the WSL
/// builder's `/root/topiaforge-build` identifies no person and is allowed, so
/// this stays a privacy gate rather than an unwinnable "no absolute paths"
/// rule.
class BuildHostPathScanner {
  const BuildHostPathScanner();

  /// Account names that belong to the operating system rather than a person.
  static const _impersonalAccounts = {
    'public',
    'default',
    'defaultuser0',
    'all users',
    'shared',
  };

  static final List<RegExp> _homeDirectoryPatterns = <RegExp>[
    // C:\Users\name  /  c:/users/name
    RegExp(r'[A-Za-z]:[\\/]Users[\\/]([A-Za-z0-9._\- ]{1,64})', caseSensitive: false),
    // /home/name/
    RegExp(r'/home/([A-Za-z0-9._\-]{1,64})/'),
    // /Users/name/ (macOS)
    RegExp(r'/Users/([A-Za-z0-9._\- ]{1,64})/'),
  ];

  /// Throws [StateError] naming the first offending file and the account it
  /// exposes. Reports one finding rather than a list, because the fix is a
  /// build-configuration change that clears every file at once.
  void assertClean(String payloadRoot) {
    final root = Directory(payloadRoot);
    if (!root.existsSync()) return;
    for (final entity in root.listSync(recursive: true, followLinks: false)) {
      if (entity is! File) continue;
      if (FileSystemEntity.typeSync(entity.path, followLinks: false) !=
          FileSystemEntityType.file) {
        continue;
      }
      if (!_looksCompiled(entity.path)) continue;
      final finding = _scan(entity);
      if (finding == null) continue;
      final relative = p.relative(entity.path, from: payloadRoot);
      throw StateError(
        'Package binary $relative records the build machine\'s home directory '
        '("$finding"). Shipping it exposes the build account and folder '
        'layout. Build .NET assemblies with the repository PathMap from '
        'Directory.Build.props, and compile Dart and Flutter output from a '
        'neutral build root, then rebuild the package.',
      );
    }
  }

  /// Only genuinely compiled output is scanned, decided by magic number rather
  /// than by extension.
  ///
  /// A compiler is the only thing that records these paths, and everything it
  /// emits carries a magic number. Extension is the wrong signal in both
  /// directions: the Linux CLI and launcher ship with no extension at all,
  /// while a shell stub named `.exe` is not compiled output. Documentation is
  /// skipped for free, which matters because `docs/` legitimately spells out
  /// example paths like `C:\Users\<you>\...`, and flagging those would train
  /// people to ignore this check.
  bool _looksCompiled(String path) => _hasExecutableMagic(File(path));

  bool _hasExecutableMagic(File file) {
    RandomAccessFile? handle;
    try {
      handle = file.openSync();
      final header = handle.readSync(4);
      if (header.length < 4) return false;
      // PE/COFF, via the DOS stub every Windows binary still carries.
      if (header[0] == 0x4D && header[1] == 0x5A) return true;
      // ELF
      if (header[0] == 0x7F &&
          header[1] == 0x45 &&
          header[2] == 0x4C &&
          header[3] == 0x46) {
        return true;
      }
      // Mach-O, both endiannesses and the fat header.
      const machO = <int>[0xFEEDFACF, 0xCFFAEDFE, 0xFEEDFACE, 0xCEFAEDFE, 0xCAFEBABE];
      final magic = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
      return machO.contains(magic);
    } on FileSystemException {
      return false;
    } finally {
      handle?.closeSync();
    }
  }

  String? _scan(File file) {
    final Uint8List bytes;
    try {
      bytes = file.readAsBytesSync();
    } on FileSystemException {
      return null;
    }
    // Latin-1 keeps a 1:1 byte-to-code-unit mapping, so offsets stay honest and
    // no byte sequence is lost to replacement characters.
    final raw = String.fromCharCodes(bytes);
    final finding = _match(raw);
    if (finding != null) return finding;
    // PE resources and some toolchains store paths as UTF-16LE; dropping NULs
    // exposes those to the same patterns without a second full decode pass.
    final widened = raw.replaceAll('\u0000', '');
    return identical(widened, raw) ? null : _match(widened);
  }

  String? _match(String haystack) {
    for (final pattern in _homeDirectoryPatterns) {
      for (final match in pattern.allMatches(haystack)) {
        final account = match.group(1);
        if (account == null) continue;
        if (_impersonalAccounts.contains(account.toLowerCase())) continue;
        return match.group(0)!;
      }
    }
    return null;
  }
}
