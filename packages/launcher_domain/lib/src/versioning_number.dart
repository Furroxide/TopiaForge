part of 'versioning.dart';

/// One unbounded, canonical numeric SemVer core component.
class SemanticVersionNumber implements Comparable<SemanticVersionNumber> {
  const SemanticVersionNumber.fromInt(int value)
    : assert(value >= 0),
      _small = value,
      _digits = null;

  SemanticVersionNumber._(this._digits) : _small = null;

  factory SemanticVersionNumber.fromValue(Object value) {
    if (value is SemanticVersionNumber) {
      return value;
    }
    if (value is int && value >= 0) {
      return SemanticVersionNumber.fromInt(value);
    }
    if (value is String) {
      return SemanticVersionNumber.parse(value);
    }
    throw ArgumentError.value(value, 'value', 'must be a non-negative number');
  }

  factory SemanticVersionNumber.parse(String digits) {
    if (!_rangeCoreComponentPattern.hasMatch(digits)) {
      throw FormatException('Invalid semantic version number: $digits');
    }
    return SemanticVersionNumber._(digits);
  }

  final int? _small;
  final String? _digits;

  String get digits => _digits ?? _small.toString();
  bool get isZero => digits == '0';
  bool get isPositive => !isZero;

  int? tryToInt() => _small ?? int.tryParse(digits);

  int requireInt(String component) {
    final value = tryToInt();
    if (value == null) {
      throw StateError(
        'Semantic version $component exceeds this platform int range; '
        'use ${component}Digits or ${component}Number.',
      );
    }
    return value;
  }

  SemanticVersionNumber increment() {
    final source = digits.codeUnits;
    final result = List<int>.from(source);
    var carry = 1;
    for (var index = result.length - 1; index >= 0 && carry != 0; index -= 1) {
      final value = result[index] - 0x30 + carry;
      result[index] = 0x30 + (value % 10);
      carry = value ~/ 10;
    }
    if (carry != 0) {
      result.insert(0, 0x31);
    }
    return SemanticVersionNumber._(String.fromCharCodes(result));
  }

  @override
  int compareTo(SemanticVersionNumber other) {
    final left = digits;
    final right = other.digits;
    final length = left.length.compareTo(right.length);
    return length != 0 ? length : left.compareTo(right);
  }

  bool operator <(int other) => _compareInt(other) < 0;
  bool operator <=(int other) => _compareInt(other) <= 0;
  bool operator >(int other) => _compareInt(other) > 0;
  bool operator >=(int other) => _compareInt(other) >= 0;

  int operator +(int other) {
    final value = tryToInt();
    if (value == null) {
      throw StateError('Semantic version number does not fit in an int.');
    }
    return value + other;
  }

  int _compareInt(int other) {
    if (other < 0) {
      return 1;
    }
    return compareTo(SemanticVersionNumber._(other.toString()));
  }

  @override
  bool operator ==(Object other) => other is SemanticVersionNumber
      ? digits == other.digits
      : other is int && _compareInt(other) == 0;

  @override
  int get hashCode => int.tryParse(digits)?.hashCode ?? digits.hashCode;

  @override
  String toString() => digits;
}
