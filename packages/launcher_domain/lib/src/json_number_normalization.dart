/// Spells the value of JSON number [token] as `digits:exponent`, or `0` for
/// any zero, so that every spelling of one value compares equal.
///
/// Manifests can come from third-party packages, so this stays linear in the
/// token's length. Zeros are found by scanning, because a backtracking engine
/// retries an unanchored `0+$` from every zero of an interior run, and a long
/// exponent is adjusted digit by digit, because BigInt converts decimal text in
/// quadratic time.
String normalizedJsonNumber(String token) {
  final negative = token.startsWith('-');
  final unsigned = negative ? token.substring(1) : token;
  final parts = unsigned.toLowerCase().split('e');
  final mantissa = parts.first;
  final dot = mantissa.indexOf('.');
  final digits = mantissa.replaceAll('.', '');
  var first = 0;
  while (first < digits.length && digits.codeUnitAt(first) == 48) {
    first++;
  }
  if (first == digits.length) return '0';
  // The nonzero digit at `first` stops this scan.
  var end = digits.length;
  while (digits.codeUnitAt(end - 1) == 48) {
    end--;
  }
  var shift = digits.length - end;
  if (dot >= 0) shift -= mantissa.length - dot - 1;
  final exponent = _shiftedExponent(
    parts.length == 1 ? '0' : parts.last,
    shift,
  );
  return '${negative ? '-' : ''}${digits.substring(first, end)}:$exponent';
}

/// Spells `BigInt.parse(exponent) + BigInt.from(shift)` exactly as BigInt does.
///
/// [shift] is bounded by the token's length, far below 10^15, so an exponent of
/// more than 15 digits keeps its sign and changes only in its last 15 digits,
/// plus at most one carry or borrow into the digits above them.
String _shiftedExponent(String exponent, int shift) {
  final negative = exponent.startsWith('-');
  var start = negative || exponent.startsWith('+') ? 1 : 0;
  if (start == exponent.length ||
      exponent.codeUnits.skip(start).any((code) => code < 48 || code > 57)) {
    // Never a JSON exponent: leave its parsing and errors to BigInt.
    return (BigInt.parse(exponent) + BigInt.from(shift)).toString();
  }
  while (start < exponent.length - 1 && exponent.codeUnitAt(start) == 48) {
    start++;
  }
  final magnitude = exponent.substring(start);
  if (magnitude.length <= _lowDigits) {
    final value = int.parse(magnitude);
    return ((negative ? -value : value) + shift).toString();
  }
  var high = magnitude.substring(0, magnitude.length - _lowDigits);
  final delta = negative ? -shift : shift;
  var low = int.parse(magnitude.substring(high.length)) + delta;
  if (low < 0) {
    low += _lowBase;
    high = _decremented(high);
  } else if (low >= _lowBase) {
    low -= _lowBase;
    high = _incremented(high);
  }
  final spelled = high.isEmpty
      ? '$low'
      : '$high${'$low'.padLeft(_lowDigits, '0')}';
  return negative ? '-$spelled' : spelled;
}

const _lowDigits = 15;
const _lowBase = 1000000000000000;

/// Adds one to the positive decimal [digits].
String _incremented(String digits) {
  var index = digits.length;
  while (index > 0 && digits.codeUnitAt(index - 1) == 57) {
    index--;
  }
  final zeros = '0' * (digits.length - index);
  if (index == 0) return '1$zeros';
  final raised = String.fromCharCode(digits.codeUnitAt(index - 1) + 1);
  return '${digits.substring(0, index - 1)}$raised$zeros';
}

/// Subtracts one from the positive decimal [digits], which have no leading
/// zero, and drops the leading zero that the borrow can leave.
String _decremented(String digits) {
  var index = digits.length;
  while (digits.codeUnitAt(index - 1) == 48) {
    index--;
  }
  final nines = '9' * (digits.length - index);
  final lowered = String.fromCharCode(digits.codeUnitAt(index - 1) - 1);
  final result = '${digits.substring(0, index - 1)}$lowered$nines';
  return result.startsWith('0') ? result.substring(1) : result;
}
