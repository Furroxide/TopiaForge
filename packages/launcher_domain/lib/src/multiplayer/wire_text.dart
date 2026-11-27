part of '../multiplayer.dart';

/// The text encodings the launcher shares with the game and the control
/// plane: lowercase hexadecimal and unpadded base64url (RFC 4648 section 5).
/// Parsing is strict, so every value has exactly one accepted spelling.
abstract final class WireText {
  static const _hexDigits = '0123456789abcdef';
  static const _base64UrlAlphabet =
      'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_';

  static String toHex(List<int> bytes) {
    final buffer = StringBuffer();
    for (final value in bytes) {
      _requireByte(value);
      buffer
        ..write(_hexDigits[value >> 4])
        ..write(_hexDigits[value & 0x0F]);
    }
    return buffer.toString();
  }

  /// Parses exactly [byteCount] bytes of lowercase hexadecimal, or returns
  /// null.
  static Uint8List? tryParseHex(String? text, int byteCount) {
    if (text == null || text.length != byteCount * 2) return null;
    final bytes = Uint8List(byteCount);
    for (var index = 0; index < byteCount; index++) {
      final high = _hexValue(text.codeUnitAt(index * 2));
      final low = _hexValue(text.codeUnitAt(index * 2 + 1));
      if (high < 0 || low < 0) return null;
      bytes[index] = (high << 4) | low;
    }
    return bytes;
  }

  static bool isHex(String? text, int byteCount) =>
      tryParseHex(text, byteCount) != null;

  static String toBase64Url(List<int> bytes) {
    final buffer = StringBuffer();
    var accumulator = 0;
    var bits = 0;
    for (final value in bytes) {
      _requireByte(value);
      accumulator = ((accumulator << 8) | value) & 0xFFFF;
      bits += 8;
      while (bits >= 6) {
        bits -= 6;
        buffer.write(_base64UrlAlphabet[(accumulator >> bits) & 0x3F]);
      }
    }
    if (bits > 0) {
      buffer.write(_base64UrlAlphabet[(accumulator << (6 - bits)) & 0x3F]);
    }
    return buffer.toString();
  }

  /// Parses unpadded base64url that encodes exactly [byteCount] bytes. The
  /// unused low bits of the last character must be zero, so no two spellings
  /// decode to the same bytes.
  static Uint8List? tryParseBase64Url(String? text, int byteCount) {
    if (text == null || text.length != (byteCount * 8 + 5) ~/ 6) return null;
    final bytes = Uint8List(byteCount);
    var written = 0;
    var accumulator = 0;
    var bits = 0;
    for (var index = 0; index < text.length; index++) {
      final value = _base64UrlAlphabet.indexOf(text[index]);
      if (value < 0) return null;
      accumulator = ((accumulator << 6) | value) & 0xFFFF;
      bits += 6;
      if (bits >= 8) {
        bits -= 8;
        if (written == byteCount) return null;
        bytes[written++] = (accumulator >> bits) & 0xFF;
      }
    }
    if (written != byteCount || (accumulator & ((1 << bits) - 1)) != 0) {
      return null;
    }
    return bytes;
  }

  /// Whether [text] is display text the wire formats accept: [minimum] to
  /// [maximum] UTF-16 code units, no surrounding white space, no control
  /// characters and no unpaired surrogates.
  ///
  /// Length counts UTF-16 code units in every runtime, so a limit means the
  /// same in Dart, C# and TypeScript. White space and control characters
  /// follow .NET's `char.IsWhiteSpace` and `char.IsControl`, which the game
  /// applies to the same fields.
  static bool isDisplayText(String? text, int maximum, {int minimum = 1}) {
    if (text == null ||
        text.isEmpty ||
        text.length < minimum ||
        text.length > maximum) {
      return false;
    }
    if (_isWhiteSpace(text.codeUnitAt(0)) ||
        _isWhiteSpace(text.codeUnitAt(text.length - 1))) {
      return false;
    }
    for (var index = 0; index < text.length; index++) {
      final unit = text.codeUnitAt(index);
      if (_isControl(unit)) return false;
      if (_isHighSurrogate(unit)) {
        if (index + 1 >= text.length ||
            !_isLowSurrogate(text.codeUnitAt(index + 1))) {
          return false;
        }
        index++;
      } else if (_isLowSurrogate(unit)) {
        return false;
      }
    }
    return true;
  }

  static int _hexValue(int unit) {
    if (unit >= 0x30 && unit <= 0x39) return unit - 0x30;
    if (unit >= 0x61 && unit <= 0x66) return unit - 0x61 + 10;
    return -1;
  }

  static void _requireByte(int value) {
    if (value < 0 || value > 0xFF) {
      throw ArgumentError.value(value, 'bytes', 'Each value must be a byte.');
    }
  }
}

bool _isHighSurrogate(int unit) => unit >= 0xD800 && unit <= 0xDBFF;

bool _isLowSurrogate(int unit) => unit >= 0xDC00 && unit <= 0xDFFF;

bool _isSurrogate(int unit) => unit >= 0xD800 && unit <= 0xDFFF;

/// Unicode category Cc, as .NET's `char.IsControl` reports it.
bool _isControl(int unit) => unit <= 0x1F || (unit >= 0x7F && unit <= 0x9F);

/// .NET's `char.IsWhiteSpace` for one UTF-16 code unit: the space, line and
/// paragraph separators, plus U+0009 to U+000D and U+0085.
bool _isWhiteSpace(int unit) =>
    (unit >= 0x09 && unit <= 0x0D) ||
    unit == 0x20 ||
    unit == 0x85 ||
    unit == 0xA0 ||
    unit == 0x1680 ||
    (unit >= 0x2000 && unit <= 0x200A) ||
    unit == 0x2028 ||
    unit == 0x2029 ||
    unit == 0x202F ||
    unit == 0x205F ||
    unit == 0x3000;

bool _sameBytes(List<int> left, List<int> right) {
  if (left.length != right.length) return false;
  for (var index = 0; index < left.length; index++) {
    if (left[index] != right[index]) return false;
  }
  return true;
}
