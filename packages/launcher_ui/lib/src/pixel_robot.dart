import 'package:flutter/widgets.dart';

import 'launcher_theme.dart';

/// First-party pixel-art robot used for launcher theming.
///
/// This is drawn from a checked-in pixel grid rather than a raster asset so the
/// mark is unambiguously TopiaForge-owned and carries no third-party rights
/// question. It replaces the previously bundled `robot.webp`, which originated
/// from the Robotopia web bundle.
class TopiaForgePixelRobot extends StatelessWidget {
  const TopiaForgePixelRobot({super.key, this.width = 136, this.semanticLabel});

  /// Rendered width. Height follows the sprite's square aspect ratio.
  final double width;

  /// Optional description for assistive technology. Decorative by default.
  final String? semanticLabel;

  /// Sprite rows, one character per pixel.
  ///
  /// `.` transparent, `k` outline, `w` body, `c` visor, `o` core accent.
  static const List<String> _rows = <String>[
    '.......oo.......',
    '.......kk.......',
    '...kkkkkkkkkk...',
    '...kwwwwwwwwk...',
    '...kwccwwccwk...',
    '...kwccwwccwk...',
    '...kwwwwwwwwk...',
    '...kwkkkkkkwk...',
    '...kkkkkkkkkk...',
    '.....kwwwwk.....',
    '..kkkkkkkkkkkk..',
    '..kwwkwoowkwwk..',
    '..kwwkwoowkwwk..',
    '..kwwkwwwwkwwk..',
    '..kkkkwwwwkkkk..',
    '......kk..kk....',
  ];

  @override
  Widget build(BuildContext context) {
    final robot = SizedBox(
      width: width,
      height: width,
      child: CustomPaint(painter: _PixelRobotPainter(_rows)),
    );
    if (semanticLabel == null) {
      return ExcludeSemantics(child: robot);
    }
    return Semantics(label: semanticLabel, image: true, child: robot);
  }
}

class _PixelRobotPainter extends CustomPainter {
  const _PixelRobotPainter(this.rows);

  final List<String> rows;

  static const Map<String, Color> _palette = <String, Color>{
    'k': TopiaForgePalette.text,
    'w': TopiaForgePalette.surface,
    'c': TopiaForgePalette.accent,
    'o': TopiaForgePalette.launch,
  };

  @override
  void paint(Canvas canvas, Size size) {
    final columns = rows.first.length;
    final cell = size.width / columns;
    // Overdraw by a hairline so neighbouring cells share an edge instead of
    // leaving seams at fractional scales.
    final bleed = cell * 0.02;
    final paint = Paint()..isAntiAlias = false;

    for (var y = 0; y < rows.length; y++) {
      final row = rows[y];
      for (var x = 0; x < columns; x++) {
        final color = _palette[row[x]];
        if (color == null) {
          continue;
        }
        paint.color = color;
        canvas.drawRect(
          Rect.fromLTWH(x * cell, y * cell, cell + bleed, cell + bleed),
          paint,
        );
      }
    }
  }

  @override
  bool shouldRepaint(_PixelRobotPainter oldDelegate) =>
      !identical(oldDelegate.rows, rows);
}
