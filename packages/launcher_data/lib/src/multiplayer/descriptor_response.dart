/// The descriptor endpoint's answers, read as they arrive
/// (`docs/internal/MultiplayerLiveSessions.md`, section 15).
library;

import 'dart:convert';
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

/// A status without a body, such as `TFD1 404`: [status] is the three
/// characters after `TFD1 `.
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

  /// More bytes arrived than the status line announced.
  moreThanAnnounced,

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

/// Reads one response as its bytes arrive, within fixed bounds: a status
/// line within the first 64 bytes, and a body of at most [maximumBodyBytes]
/// that must be exactly as long as announced. Bytes are hostile input, so
/// nothing here throws.
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
  /// without success. Bytes after a finished body make it malformed.
  bool add(List<int> chunk) {
    if (_result case Tfd1Body() when chunk.isNotEmpty) {
      _result = _tooMuch;
    }
    if (_result != null) return true;
    var offset = 0;
    if (_body == null) {
      while (offset < chunk.length && _body == null && _result == null) {
        final byte = chunk[offset++];
        if (byte == 0x0A) {
          _readStatus();
        } else if (_headerLength == maximumHeaderBytes - 1 ||
            byte < 0 ||
            byte > 0xFF) {
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
    final remaining = chunk.length - offset;
    if (remaining > body.length - _received) {
      _result = _tooMuch;
      return true;
    }
    for (var index = offset; index < chunk.length; index++) {
      final byte = chunk[index];
      if (byte < 0 || byte > 0xFF) {
        _result = _tooMuch;
        return true;
      }
      body[_received++] = byte;
    }
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

  void _readStatus() {
    for (var index = 0; index < _headerLength; index++) {
      final unit = _header[index];
      if (unit < 0x20 || unit > 0x7E) {
        _result = _badStatus;
        return;
      }
    }
    final line = ascii.decode(Uint8List.sublistView(_header, 0, _headerLength));
    if (line.startsWith(_okPrefix)) {
      final digits = line.substring(_okPrefix.length);
      final length = _plainDecimal(digits);
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
    if (line.startsWith(_statusPrefix) &&
        line.length == _statusPrefix.length + 3 &&
        line != 'TFD1 200') {
      _result = Tfd1Status(line.substring(_statusPrefix.length));
      return;
    }
    _result = _badStatus;
  }

  /// A decimal of one to seven digits without a leading zero, or null.
  static int? _plainDecimal(String digits) {
    if (digits.isEmpty ||
        digits.length > 7 ||
        (digits.length > 1 && digits.codeUnitAt(0) == 0x30)) {
      return null;
    }
    var value = 0;
    for (var index = 0; index < digits.length; index++) {
      final unit = digits.codeUnitAt(index);
      if (unit < 0x30 || unit > 0x39) return null;
      value = value * 10 + unit - 0x30;
    }
    return value;
  }

  static const _badStatus = Tfd1Malformed(
    Tfd1Problem.badStatusLine,
    "The host's answer did not start with a status line.",
  );

  static const _tooMuch = Tfd1Malformed(
    Tfd1Problem.moreThanAnnounced,
    'The host sent more than it announced.',
  );
}
