import 'dart:math' as math;

/// WCAG 2.x relative-luminance contrast ratio, recomputed from the widget
/// foreground/background colours the observer serialised. This never trusts a
/// broker-supplied ratio; it is derived from the `#rrggbb` values themselves.
final _hexColor = RegExp(r'^#[0-9a-fA-F]{6}$');

/// True when both colours are present, non-empty `#rrggbb` strings.
bool nativeHasBothColours(Object? foreground, Object? background) =>
    foreground is String &&
    _hexColor.hasMatch(foreground) &&
    background is String &&
    _hexColor.hasMatch(background);

/// Returns true when [foreground] and [background] are both parseable
/// `#rrggbb` values and their contrast ratio is at least 4.5:1.
bool nativeContrastMeetsAA(Object? foreground, Object? background) {
  final fg = _luminance(foreground);
  final bg = _luminance(background);
  if (fg == null || bg == null) return false;
  final lighter = math.max(fg, bg);
  final darker = math.min(fg, bg);
  return (lighter + 0.05) / (darker + 0.05) >= 4.5;
}

double? _luminance(Object? value) {
  if (value is! String || !_hexColor.hasMatch(value)) return null;
  final r = int.parse(value.substring(1, 3), radix: 16);
  final g = int.parse(value.substring(3, 5), radix: 16);
  final b = int.parse(value.substring(5, 7), radix: 16);
  return 0.2126 * _channel(r) + 0.7152 * _channel(g) + 0.0722 * _channel(b);
}

double _channel(int component) {
  final c = component / 255;
  return c <= 0.03928
      ? c / 12.92
      : math.pow((c + 0.055) / 1.055, 2.4) as double;
}
