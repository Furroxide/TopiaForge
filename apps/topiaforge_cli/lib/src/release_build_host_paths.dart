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
  const BuildHostPathScanner({this.chunkSize = defaultChunkSize})
    : assert(chunkSize > 0, 'chunkSize must be positive');

  /// Bytes read per pass over a binary.
  ///
  /// Package binaries run to tens of megabytes, and reading one whole would
  /// usually be fine. A bounded window keeps the gate's memory independent of
  /// whatever the toolchain happens to emit, so an unexpectedly large file
  /// slows the scan instead of exhausting the process. Skipping large files
  /// was not an option: this is a privacy gate, and a size cap would be a
  /// bypass.
  static const int defaultChunkSize = 4 * 1024 * 1024;

  /// Bytes carried from one window into the next so a path straddling the
  /// boundary is still seen whole.
  ///
  /// The longest match is a drive prefix, the `Users` segment, and a
  /// 64-character account: under 80 bytes as Latin-1 and under 160 as
  /// UTF-16LE. 256 covers both with room to spare.
  static const int _overlap = 256;

  /// Bytes read per pass; see [defaultChunkSize].
  final int chunkSize;

  /// Account names that belong to the operating system rather than a person.
  static const _impersonalAccounts = {
    'public',
    'default',
    'defaultuser0',
    'all users',
    'shared',
  };

  /// Mach-O magic numbers, read big-endian from the first four bytes.
  ///
  /// Mach-O writes its magic in the file's own byte order, so every header is
  /// listed in both orders: the thin 32- and 64-bit headers, and the fat
  /// header in its 32- and 64-bit forms. A fat header is defined as
  /// big-endian, so its swapped forms should never occur on disk, but listing
  /// them costs nothing and a miss here skips the file silently.
  static const _machOMagic = <int>{
    0xFEEDFACE, 0xCEFAEDFE, // MH_MAGIC, MH_CIGAM
    0xFEEDFACF, 0xCFFAEDFE, // MH_MAGIC_64, MH_CIGAM_64
    0xCAFEBABE, 0xBEBAFECA, // FAT_MAGIC, FAT_CIGAM
    0xCAFEBABF, 0xBFBAFECA, // FAT_MAGIC_64, FAT_CIGAM_64
  };

  static final List<RegExp> _homeDirectoryPatterns = <RegExp>[
    // C:\Users\name  /  c:/users/name
    RegExp(
      r'[A-Za-z]:[\\/]Users[\\/]([A-Za-z0-9._\- ]{1,64})',
      caseSensitive: false,
    ),
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
      final magic =
          (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
      return _machOMagic.contains(magic);
    } on FileSystemException {
      return false;
    } finally {
      handle?.closeSync();
    }
  }

  /// Walks the file in [chunkSize] windows, each prefixed with the tail of the
  /// previous one, and returns the first match.
  String? _scan(File file) {
    RandomAccessFile? handle;
    try {
      handle = file.openSync();
      var carry = Uint8List(0);
      while (true) {
        final read = handle.readSync(chunkSize);
        if (read.isEmpty) return null;
        final Uint8List window;
        if (carry.isEmpty) {
          window = read;
        } else {
          window = Uint8List(carry.length + read.length)
            ..setRange(0, carry.length, carry)
            ..setRange(carry.length, carry.length + read.length, read);
        }
        final finding = _scanWindow(window);
        if (finding != null) return finding;
        carry = window.length > _overlap
            ? window.sublist(window.length - _overlap)
            : window;
      }
    } on FileSystemException {
      return null;
    } finally {
      handle?.closeSync();
    }
  }

  String? _scanWindow(Uint8List bytes) {
    // Latin-1 keeps a 1:1 byte-to-code-unit mapping, so no byte sequence is
    // lost to replacement characters.
    final raw = String.fromCharCodes(bytes);
    final finding = _match(raw);
    if (finding != null) return finding;
    // PE resources and some toolchains store paths as UTF-16LE; dropping NULs
    // exposes those to the same patterns without a second decode. Nearly every
    // binary contains NULs, so this pass is the rule rather than the
    // exception; the check only spares an all-text window the extra work.
    if (!bytes.contains(0)) return null;
    return _match(raw.replaceAll('\u0000', ''));
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
