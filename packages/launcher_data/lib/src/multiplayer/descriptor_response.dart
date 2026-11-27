/// The descriptor endpoint's answers, read as they arrive
/// (`docs/internal/MultiplayerLiveSessions.md`, section 15).
library;

import 'dart:math';
import 'dart:typed_data';

import 'package:launcher_domain/launcher_domain.dart';

/// What a host's descriptor endpoint answered to one request.
sealed class Tfd1Response {
  const Tfd1Response();
}

/// `TFD1 200 <length>` followed by exactly that many bytes.
final class Tfd1Body extends Tfd1Response {
  const Tfd1Body(this.bytes);

  final Uint8List bytes;
}

/// A status without a body, such as `TFD1 404`: [status] is the three bytes
/// after `TFD1 `, as Latin-1 text.
final class Tfd1Status extends Tfd1Response {
  const Tfd1Status(this.status);

  final String status;
}

/// Why an answer was not a TFD1 response.
enum Tfd1Problem {
  /// The first 64 bytes held no status line, or the connection ended before
  /// one arrived.
  noStatusLine,

  /// The status line is not a TFD1 status.
  badStatusLine,

  /// The announced length is zero, larger than the limit, or not written in
  /// plain decimal.
  impossibleLength,

  /// The connection ended before the announced body was complete.
  endedEarly,
}

/// An answer that broke the protocol. [message] is for people and names no
/// address.
final class Tfd1Malformed extends Tfd1Response {
  const Tfd1Malformed(this.problem, this.message);

  final Tfd1Problem problem;
  final String message;
}

/// Reads one descriptor answer as its bytes arrive, the way the game's
/// `DescriptorClient` reads it: a status line within the first 64 bytes,
/// then a body of at most [maximumBodyBytes], read up to the announced
/// length and no further. Bytes after the body are never read, whether they
/// arrive with its end or later, so the outcome never depends on how the
/// network split them. Bytes are hostile input, so nothing here throws.
///
/// It reads descriptors only: the length here is at most seven digits and
/// the body is held in memory. A `TFD1 package <sha256>` archive, whose
/// length the game writes as a 64-bit integer, is read by
/// `Tfd1PackageReader`, which streams it to disk (`package_response.dart`,
/// through `HostPackageClient`).
final class Tfd1ResponseReader {
  Tfd1ResponseReader({
    this.maximumBodyBytes = SignedServerDescriptorV1.maximumBytes,
  });

  /// The status line and its newline arrive within this many bytes.
  static const maximumHeaderBytes = 64;

  static const _okPrefix = 'TFD1 200 ';
  static const _statusPrefix = 'TFD1 ';

  final int maximumBodyBytes;
  final Uint8List _header = Uint8List(maximumHeaderBytes);
  var _headerLength = 0;
  Uint8List? _body;
  var _received = 0;
  Tfd1Response? _result;

  /// The response, once [add] or [close] has finished it.
  Tfd1Response? get result => _result;

  bool get isDone => _result != null;

  /// Takes the next bytes; true once the response is finished, with or
  /// without success. Bytes past the finished response are ignored.
  bool add(Uint8List chunk) {
    if (_result != null) return true;
    var offset = 0;
    if (_body == null) {
      while (offset < chunk.length && _body == null && _result == null) {
        final byte = chunk[offset++];
        if (byte == 0x0A) {
          _readStatus();
        } else if (_headerLength == maximumHeaderBytes - 1) {
          _result = const Tfd1Malformed(
            Tfd1Problem.noStatusLine,
            "The host's answer did not start with a status line.",
          );
        } else {
          _header[_headerLength++] = byte;
        }
      }
      if (_result != null) return true;
      if (_body == null) return false;
    }
    final body = _body!;
    final taken = min(chunk.length - offset, body.length - _received);
    body.setRange(_received, _received + taken, chunk, offset);
    _received += taken;
    if (_received == body.length) _result = Tfd1Body(body);
    return _result != null;
  }

  /// The connection ended: finishes the response with what arrived.
  Tfd1Response close() {
    return _result ??= _body == null
        ? const Tfd1Malformed(
            Tfd1Problem.noStatusLine,
            'The host closed the connection without answering.',
          )
        : const Tfd1Malformed(
            Tfd1Problem.endedEarly,
            'The host closed the connection before the descriptor was '
            'complete.',
          );
  }

  /// Reads the status line as the game does: `TFD1 200 <length>` announces
  /// a body, and any other line of eight bytes that starts with `TFD1 ` is a
  /// status, `TFD1 200` without a length included.
  void _readStatus() {
    final line = Uint8List.sublistView(_header, 0, _headerLength);
    if (_startsWith(line, _okPrefix)) {
      final length = _plainDecimal(line, _okPrefix.length);
      if (length == null || length < 1 || length > maximumBodyBytes) {
        _result = Tfd1Malformed(
          Tfd1Problem.impossibleLength,
          'The host announced a descriptor of an impossible size; the limit '
          'is $maximumBodyBytes bytes.',
        );
        return;
      }
      _body = Uint8List(length);
      return;
    }
    if (line.length == _statusPrefix.length + 3 &&
        _startsWith(line, _statusPrefix)) {
      _result = Tfd1Status(
        String.fromCharCodes(line, _statusPrefix.length, line.length),
      );
      return;
    }
    _result = const Tfd1Malformed(
      Tfd1Problem.badStatusLine,
      "The host's answer did not start with a status line.",
    );
  }

  static bool _startsWith(Uint8List line, String prefix) {
    if (line.length < prefix.length) return false;
    for (var index = 0; index < prefix.length; index++) {
      if (line[index] != prefix.codeUnitAt(index)) return false;
    }
    return true;
  }

  /// The decimal of one to seven digits without a leading zero that fills
  /// [line] from [start], or null.
  static int? _plainDecimal(Uint8List line, int start) {
    final digits = line.length - start;
    if (digits < 1 || digits > 7 || (digits > 1 && line[start] == 0x30)) {
      return null;
    }
    var value = 0;
    for (var index = start; index < line.length; index++) {
      final unit = line[index];
      if (unit < 0x30 || unit > 0x39) return null;
      value = value * 10 + unit - 0x30;
    }
    return value;
  }
}
