part of '../multiplayer.dart';

/// Reads one JSON value strictly enough that [CanonicalJson.decode] can then
/// insist on canonical bytes. It refuses repeated keys itself, because a map
/// cannot hold both and re-writing would hide the repeat. Reading a document
/// of any layout, it also steps over white space between values.
final class _CanonicalJsonReader {
  _CanonicalJsonReader(this._text, {required bool anyLayout})
    : _anyLayout = anyLayout;

  final String _text;

  /// Whether white space may stand between values, as in a document of any
  /// layout; canonical bytes have none.
  final bool _anyLayout;
  var _position = 0;

  bool get atEnd => _position == _text.length;

  /// Whether nothing but the white space the layout allows is left.
  bool atDocumentEnd() {
    _skipLayout();
    return atEnd;
  }

  Object? readValue(int depth) {
    if (depth > CanonicalJson.maximumDepth) {
      throw const CanonicalJsonException('The document nests too deeply.');
    }
    _skipLayout();
    if (atEnd) throw const CanonicalJsonException('The document ended early.');
    switch (_text.codeUnitAt(_position)) {
      case 0x7B:
        return _readObject(depth);
      case 0x5B:
        return _readArray(depth);
      case 0x22:
        return _readString();
      case 0x74:
        _expect('true');
        return true;
      case 0x66:
        _expect('false');
        return false;
      case 0x6E:
        _expect('null');
        return null;
      default:
        return _readInteger();
    }
  }

  Map<String, Object?> _readObject(int depth) {
    _position++;
    final result = <String, Object?>{};
    if (_tryConsume(0x7D)) return Map.unmodifiable(result);
    while (true) {
      _skipLayout();
      if (atEnd || _text.codeUnitAt(_position) != 0x22) {
        throw const CanonicalJsonException('An object key must be a string.');
      }
      final key = _readString();
      if (!CanonicalJson.isKey(key)) {
        throw const CanonicalJsonException(
          'An object key must be 1 to 64 ASCII letters and digits.',
        );
      }
      if (result.containsKey(key)) {
        throw const CanonicalJsonException('An object repeats a key.');
      }
      if (!_tryConsume(0x3A)) {
        throw const CanonicalJsonException(
          'An object key must be followed by a colon.',
        );
      }
      result[key] = readValue(depth + 1);
      if (_tryConsume(0x7D)) return Map.unmodifiable(result);
      if (!_tryConsume(0x2C)) {
        throw const CanonicalJsonException(
          'Object members must be separated by commas.',
        );
      }
    }
  }

  List<Object?> _readArray(int depth) {
    _position++;
    final items = <Object?>[];
    if (_tryConsume(0x5D)) return List.unmodifiable(items);
    while (true) {
      items.add(readValue(depth + 1));
      if (_tryConsume(0x5D)) return List.unmodifiable(items);
      if (!_tryConsume(0x2C)) {
        throw const CanonicalJsonException(
          'Array items must be separated by commas.',
        );
      }
    }
  }

  String _readString() {
    _position++;
    final units = <int>[];
    while (true) {
      if (atEnd) {
        throw const CanonicalJsonException('A string is not terminated.');
      }
      final unit = _text.codeUnitAt(_position++);
      if (unit == 0x22) break;
      if (unit < 0x20) {
        throw const CanonicalJsonException(
          'A string holds a raw control character.',
        );
      }
      if (unit != 0x5C) {
        units.add(unit);
        continue;
      }
      if (atEnd) {
        throw const CanonicalJsonException('A string ends inside an escape.');
      }
      final escape = _text.codeUnitAt(_position++);
      units.add(switch (escape) {
        0x22 => 0x22,
        0x5C => 0x5C,
        0x2F => 0x2F,
        0x62 => 0x08,
        0x66 => 0x0C,
        0x6E => 0x0A,
        0x72 => 0x0D,
        0x74 => 0x09,
        0x75 => _readUnicodeEscape(),
        _ => throw const CanonicalJsonException(
          'A string holds an unknown escape.',
        ),
      });
    }
    for (var index = 0; index < units.length; index++) {
      if (_isHighSurrogate(units[index]) &&
          index + 1 < units.length &&
          _isLowSurrogate(units[index + 1])) {
        index++;
      } else if (_isSurrogate(units[index])) {
        throw const CanonicalJsonException(
          'A string holds an unpaired surrogate.',
        );
      }
    }
    return String.fromCharCodes(units);
  }

  int _readUnicodeEscape() {
    if (_position + 4 > _text.length) {
      throw const CanonicalJsonException('A unicode escape is truncated.');
    }
    var value = 0;
    for (var index = 0; index < 4; index++) {
      final digit = _text.codeUnitAt(_position++);
      final int nibble;
      if (digit >= 0x30 && digit <= 0x39) {
        nibble = digit - 0x30;
      } else if (digit >= 0x61 && digit <= 0x66) {
        nibble = digit - 0x61 + 10;
      } else if (digit >= 0x41 && digit <= 0x46) {
        nibble = digit - 0x41 + 10;
      } else {
        throw const CanonicalJsonException(
          'A unicode escape holds a non-hexadecimal digit.',
        );
      }
      value = (value << 4) | nibble;
    }
    return value;
  }

  int _readInteger() {
    final start = _position;
    if (_tryConsume(0x2D) && atEnd) {
      throw const CanonicalJsonException('A number is truncated.');
    }
    if (atEnd || !_isDigit(_text.codeUnitAt(_position))) {
      throw const CanonicalJsonException(
        'Unexpected character in the document.',
      );
    }
    if (_text.codeUnitAt(_position) == 0x30 &&
        _position + 1 < _text.length &&
        _isDigit(_text.codeUnitAt(_position + 1))) {
      throw const CanonicalJsonException('A number has a leading zero.');
    }
    while (!atEnd && _isDigit(_text.codeUnitAt(_position))) {
      _position++;
    }
    if (!atEnd) {
      final next = _text.codeUnitAt(_position);
      if (next == 0x2E || next == 0x65 || next == 0x45) {
        throw const CanonicalJsonException('Only integers are allowed.');
      }
    }
    final digits = _text.substring(start, _position);
    final value = digits.length > 17 ? null : int.tryParse(digits);
    if (value == null ||
        value > CanonicalJson.maximumSafeInteger ||
        value < -CanonicalJson.maximumSafeInteger) {
      throw const CanonicalJsonException(
        'An integer is outside the exactly representable range.',
      );
    }
    return value;
  }

  void _expect(String literal) {
    if (!_text.startsWith(literal, _position)) {
      throw const CanonicalJsonException('Unexpected literal in the document.');
    }
    _position += literal.length;
  }

  bool _tryConsume(int unit) {
    _skipLayout();
    if (atEnd || _text.codeUnitAt(_position) != unit) return false;
    _position++;
    return true;
  }

  /// Steps over the white space JSON allows between values, in a document of
  /// any layout only.
  void _skipLayout() {
    if (!_anyLayout) return;
    while (!atEnd) {
      final unit = _text.codeUnitAt(_position);
      if (unit != 0x20 && unit != 0x09 && unit != 0x0A && unit != 0x0D) return;
      _position++;
    }
  }

  static bool _isDigit(int unit) => unit >= 0x30 && unit <= 0x39;
}
