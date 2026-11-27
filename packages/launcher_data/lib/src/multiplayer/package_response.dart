/// A host's answer to `TFD1 package <sha256>`, read as it arrives
/// (`docs/internal/MultiplayerLiveSessions.md`, section 15).
library;

import 'dart:math';
import 'dart:typed_data';

import 'package:launcher_domain/launcher_domain.dart';

/// How a package answer ended, once [Tfd1PackageReader] finished it.
sealed class Tfd1PackageAnswer {
  const Tfd1PackageAnswer();
}

/// `TFD1 200 <length>` and exactly that many bytes, every one of them
/// handed out by [Tfd1PackageReader.add].
final class Tfd1PackageComplete extends Tfd1PackageAnswer {
  const Tfd1PackageComplete(this.length);

  final int length;
}

/// A status without a body, such as `TFD1 404`: [status] is the three bytes
/// after `TFD1 `, as Latin-1 text.
final class Tfd1PackageStatus extends Tfd1PackageAnswer {
  const Tfd1PackageStatus(this.status);

  final String status;
}

/// Why a package answer broke the protocol or its bounds.
enum Tfd1PackageProblem {
  /// The first 64 bytes held no status line, or the connection ended before
  /// one arrived.
  noStatusLine,

  /// The status line is not a TFD1 status.
  badStatusLine,

  /// The announced length is zero, or not a plain decimal of at most the 19
  /// digits of the game's 64-bit length.
  impossibleLength,

  /// The announced length is larger than the reader's bound.
  tooLarge,

  /// The connection ended before the announced length.
  endedEarly,
}

final class Tfd1PackageMalformed extends Tfd1PackageAnswer {
  const Tfd1PackageMalformed(this.problem);

  final Tfd1PackageProblem problem;
}

/// Reads one package answer as its bytes arrive, the way the game's
/// `DescriptorEndpoint` writes it: a status line within the first 64 bytes,
/// `TFD1 200` followed by the archive's length as the game's 64-bit integer
/// writes it, then the archive.
///
/// - **Streaming.** [add] hands back the part of each chunk that belongs to
///   the body, for the caller to hash and write to disk at once; nothing of
///   the body is held here.
/// - **Bounds.** A length past [maximumBodyBytes] is refused as soon as the
///   status line ends, before any of the body is taken. The body is taken up
///   to the announced length and no further, as the game's own client reads
///   a descriptor: bytes after it are never handed out, whether they arrive
///   with its end or later. A body that ends short is refused by [close].
/// - Bytes are hostile input, so nothing here throws.
final class Tfd1PackageReader {
  /// [maximumBodyBytes] is at most [ModRegistryFormat.maxPackageBytes].
  Tfd1PackageReader({
    this.maximumBodyBytes = ModRegistryFormat.maxPackageBytes,
  }) {
    RangeError.checkValueInInterval(
      maximumBodyBytes,
      1,
      ModRegistryFormat.maxPackageBytes,
      'maximumBodyBytes',
    );
  }

  /// The status line and its newline arrive within this many bytes.
  static const maximumHeaderBytes = 64;

  /// The most digits a length has: `long.MaxValue` in the game has 19.
  static const maximumLengthDigits = 19;

  static const _okPrefix = 'TFD1 200 ';
  static const _statusPrefix = 'TFD1 ';
  static final _nothing = Uint8List(0);

  final int maximumBodyBytes;
  final Uint8List _header = Uint8List(maximumHeaderBytes);
  var _headerLength = 0;
  int? _announced;
  var _received = 0;
  Tfd1PackageAnswer? _result;

  /// The length the status line announced, once it has.
  int? get announcedLength => _announced;

  /// How much of the body has been handed out.
  int get receivedBytes => _received;

  /// How the answer ended, once [add] or [close] finished it.
  Tfd1PackageAnswer? get result => _result;

  bool get isDone => _result != null;

  /// Takes the next bytes and returns those that belong to the body, which
  /// may be none. Once [result] is set nothing more is taken.
  Uint8List add(Uint8List chunk) {
    if (_result != null) return _nothing;
    var offset = 0;
    if (_announced == null) {
      while (offset < chunk.length && _announced == null && _result == null) {
        final byte = chunk[offset++];
        if (byte == 0x0A) {
          _readStatus();
        } else if (_headerLength == maximumHeaderBytes - 1) {
          _result = const Tfd1PackageMalformed(Tfd1PackageProblem.noStatusLine);
        } else {
          _header[_headerLength++] = byte;
        }
      }
      if (_announced == null) return _nothing;
    }
    final announced = _announced!;
    final taken = min(chunk.length - offset, announced - _received);
    _received += taken;
    if (_received == announced) _result = Tfd1PackageComplete(announced);
    return Uint8List.sublistView(chunk, offset, offset + taken);
  }

  /// The connection ended: finishes the answer with what arrived.
  Tfd1PackageAnswer close() => _result ??= Tfd1PackageMalformed(
    _announced == null
        ? Tfd1PackageProblem.noStatusLine
        : Tfd1PackageProblem.endedEarly,
  );

  /// Reads the status line as the game's client does: `TFD1 200 <length>`
  /// announces a body, and any other line of eight bytes that starts with
  /// `TFD1 ` is a status, `TFD1 200` without a length included.
  void _readStatus() {
    final line = Uint8List.sublistView(_header, 0, _headerLength);
    if (_startsWith(line, _okPrefix)) {
      final length = _plainDecimal(line, _okPrefix.length, maximumBodyBytes);
      if (length == null || length < 1) {
        _result = const Tfd1PackageMalformed(
          Tfd1PackageProblem.impossibleLength,
        );
      } else if (length > maximumBodyBytes) {
        _result = const Tfd1PackageMalformed(Tfd1PackageProblem.tooLarge);
      } else {
        _announced = length;
      }
      return;
    }
    if (line.length == _statusPrefix.length + 3 &&
        _startsWith(line, _statusPrefix)) {
      _result = Tfd1PackageStatus(
        String.fromCharCodes(line, _statusPrefix.length, line.length),
      );
      return;
    }
    _result = const Tfd1PackageMalformed(Tfd1PackageProblem.badStatusLine);
  }

  static bool _startsWith(Uint8List line, String prefix) {
    if (line.length < prefix.length) return false;
    for (var index = 0; index < prefix.length; index++) {
      if (line[index] != prefix.codeUnitAt(index)) return false;
    }
    return true;
  }

  /// The decimal of one to [maximumLengthDigits] digits without a leading
  /// zero that fills [line] from [start], or null. Past [cap] it stops
  /// counting, so a length of any size cannot overflow: anything larger than
  /// [cap] reads as larger than it.
  static int? _plainDecimal(Uint8List line, int start, int cap) {
    final digits = line.length - start;
    if (digits < 1 ||
        digits > maximumLengthDigits ||
        (digits > 1 && line[start] == 0x30)) {
      return null;
    }
    var value = 0;
    for (var index = start; index < line.length; index++) {
      final unit = line[index];
      if (unit < 0x30 || unit > 0x39) return null;
      if (value <= cap) value = value * 10 + unit - 0x30;
    }
    return value;
  }
}
