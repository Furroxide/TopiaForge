import 'package:flutter/material.dart';
import 'package:flutter_svg/flutter_svg.dart';

import 'launcher_theme.dart';

class ScreenHeader extends StatelessWidget {
  const ScreenHeader({
    super.key,
    required this.title,
    required this.subtitle,
    this.trailing,
  });

  final String title;
  final String subtitle;
  final Widget? trailing;

  @override
  Widget build(BuildContext context) {
    return LayoutBuilder(
      builder: (context, constraints) {
        final titleBlock = Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              title,
              style: Theme.of(context).textTheme.headlineSmall,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
            ),
            const SizedBox(height: 4),
            Text(
              subtitle,
              style: Theme.of(context).textTheme.bodySmall,
              maxLines: 2,
              overflow: TextOverflow.ellipsis,
            ),
          ],
        );
        final actions = trailing;
        final compact = actions != null && constraints.maxWidth < 760;

        return Padding(
          padding: const EdgeInsets.fromLTRB(22, 18, 22, 14),
          child: compact
              ? Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    titleBlock,
                    const SizedBox(height: 10),
                    Align(alignment: Alignment.centerLeft, child: actions),
                  ],
                )
              : Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Expanded(child: titleBlock),
                    if (actions != null) ...[
                      const SizedBox(width: 16),
                      actions,
                    ],
                  ],
                ),
        );
      },
    );
  }
}

class StatusPill extends StatelessWidget {
  const StatusPill({
    super.key,
    required this.label,
    required this.tone,
    this.icon,
    this.tooltip,
    this.onPressed,
  });

  final String label;
  final StatusTone tone;
  final IconData? icon;
  final String? tooltip;
  final VoidCallback? onPressed;

  @override
  Widget build(BuildContext context) {
    final color = switch (tone) {
      StatusTone.good => TopiaForgePalette.good,
      StatusTone.info => TopiaForgePalette.accentDark,
      StatusTone.warning => TopiaForgePalette.warning,
      StatusTone.danger => TopiaForgePalette.danger,
      StatusTone.neutral => TopiaForgePalette.mutedText,
    };
    final background = switch (tone) {
      StatusTone.good => const Color(0x1F148D63),
      StatusTone.info => const Color(0x2220F6FE),
      StatusTone.warning => const Color(0x22D68017),
      StatusTone.danger => const Color(0x20C83E4D),
      StatusTone.neutral => const Color(0x18FFFFFF),
    };

    final pill = Container(
      constraints: const BoxConstraints(minHeight: 28),
      padding: const EdgeInsets.symmetric(horizontal: 11, vertical: 5),
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(999),
        border: Border.all(color: color.withValues(alpha: 0.75), width: 2),
        color: background,
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          if (icon != null) ...[
            Icon(icon, size: 14, color: color),
            const SizedBox(width: 5),
          ],
          Flexible(
            child: Text(
              label,
              style: TextStyle(
                color: color,
                fontSize: 12,
                fontWeight: FontWeight.w900,
              ),
              overflow: TextOverflow.ellipsis,
            ),
          ),
        ],
      ),
    );

    final message = tooltip;
    final child = onPressed == null
        ? pill
        : Semantics(
            button: true,
            child: InkWell(
              borderRadius: BorderRadius.circular(999),
              onTap: onPressed,
              child: pill,
            ),
          );

    if (message == null || message.isEmpty) {
      return child;
    }
    return Tooltip(message: message, child: child);
  }
}

enum StatusTone { good, info, warning, danger, neutral }

class EmptyStatePanel extends StatelessWidget {
  const EmptyStatePanel({
    super.key,
    required this.icon,
    required this.title,
    required this.message,
    this.action,
    this.brandAsset,
  });

  final IconData icon;
  final String title;
  final String message;
  final Widget? action;
  final String? brandAsset;

  @override
  Widget build(BuildContext context) {
    return Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 560),
        child: Stack(
          clipBehavior: Clip.none,
          alignment: Alignment.topCenter,
          children: [
            if (brandAsset != null)
              Positioned(
                top: -100,
                right: 8,
                child: IgnorePointer(
                  child: Image.asset(
                    brandAsset!,
                    package: TopiaForgeBrandAssets.package,
                    width: 136,
                    fit: BoxFit.contain,
                    filterQuality: FilterQuality.medium,
                  ),
                ),
              ),
            BorderedPane(
              padding: const EdgeInsets.fromLTRB(28, 30, 28, 26),
              child: SingleChildScrollView(
                primary: false,
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Container(
                      width: 62,
                      height: 62,
                      decoration: BoxDecoration(
                        color: TopiaForgePalette.surfaceTint,
                        shape: BoxShape.circle,
                        border: Border.all(
                          color: TopiaForgePalette.launch,
                          width: 3,
                        ),
                      ),
                      child: Icon(
                        icon,
                        size: 32,
                        color: TopiaForgePalette.text,
                      ),
                    ),
                    const SizedBox(height: 16),
                    Text(
                      title,
                      style: Theme.of(context).textTheme.titleLarge,
                      textAlign: TextAlign.center,
                    ),
                    const SizedBox(height: 8),
                    Text(
                      message,
                      style: Theme.of(context).textTheme.bodyMedium,
                      textAlign: TextAlign.center,
                    ),
                    if (action != null) ...[
                      const SizedBox(height: 20),
                      action!,
                    ],
                  ],
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class TopiaForgeBackdrop extends StatelessWidget {
  const TopiaForgeBackdrop({super.key, required this.child});

  final Widget child;

  @override
  Widget build(BuildContext context) {
    return DecoratedBox(
      decoration: const BoxDecoration(
        color: TopiaForgePalette.paper,
        image: DecorationImage(
          image: AssetImage(
            TopiaForgeBrandAssets.cityHeader,
            package: TopiaForgeBrandAssets.package,
          ),
          fit: BoxFit.cover,
          opacity: 0.15,
          alignment: Alignment.topCenter,
        ),
      ),
      child: CustomPaint(painter: _TopiaForgeGridPainter(), child: child),
    );
  }
}

class TopiaForgeLogo extends StatelessWidget {
  const TopiaForgeLogo({super.key, this.height = 34});

  final double height;

  @override
  Widget build(BuildContext context) {
    return SvgPicture.asset(
      TopiaForgeBrandAssets.logo,
      package: TopiaForgeBrandAssets.package,
      height: height,
      fit: BoxFit.contain,
    );
  }
}

class BorderedPane extends StatelessWidget {
  const BorderedPane({
    super.key,
    required this.child,
    this.padding = const EdgeInsets.all(12),
    this.accentColor = TopiaForgePalette.borderStrong,
    this.clipBehavior = Clip.none,
  });

  final Widget child;
  final EdgeInsetsGeometry padding;
  final Color accentColor;
  final Clip clipBehavior;

  @override
  Widget build(BuildContext context) {
    final borderRadius = BorderRadius.circular(28);
    final paddedChild = Padding(padding: padding, child: child);
    return DecoratedBox(
      decoration: BoxDecoration(
        borderRadius: borderRadius,
        boxShadow: [
          BoxShadow(
            color: accentColor.withValues(alpha: 0.18),
            offset: const Offset(-4, 8),
            blurRadius: 0,
          ),
          BoxShadow(
            color: Colors.black.withValues(alpha: 0.06),
            offset: const Offset(0, 14),
            blurRadius: 34,
          ),
        ],
      ),
      child: Material(
        color: TopiaForgePalette.surface,
        shape: RoundedRectangleBorder(
          borderRadius: borderRadius,
          side: BorderSide(
            color: accentColor.withValues(alpha: 0.52),
            width: 3,
          ),
        ),
        clipBehavior: clipBehavior,
        child: paddedChild,
      ),
    );
  }
}

class DetailSection extends StatelessWidget {
  const DetailSection({super.key, required this.title, required this.children});

  final String title;
  final List<Widget> children;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(title, style: Theme.of(context).textTheme.titleMedium),
          const SizedBox(height: 8),
          ...children,
        ],
      ),
    );
  }
}

class LogViewer extends StatelessWidget {
  const LogViewer({super.key, required this.text});

  final String text;

  @override
  Widget build(BuildContext context) {
    return DecoratedBox(
      decoration: BoxDecoration(
        color: TopiaForgePalette.logPanel,
        border: Border.all(color: TopiaForgePalette.launch, width: 3),
        borderRadius: BorderRadius.circular(24),
        boxShadow: [
          BoxShadow(
            color: TopiaForgePalette.launchDark.withValues(alpha: 0.24),
            offset: const Offset(-4, 8),
            blurRadius: 0,
          ),
        ],
      ),
      child: SingleChildScrollView(
        padding: const EdgeInsets.all(12),
        child: SelectableText(
          text.trim().isEmpty ? 'No logs available.' : text,
          style: const TextStyle(
            fontFamily: 'Consolas',
            fontSize: 12,
            height: 1.35,
            color: TopiaForgePalette.white,
          ),
        ),
      ),
    );
  }
}

class _TopiaForgeGridPainter extends CustomPainter {
  @override
  void paint(Canvas canvas, Size size) {
    final paint = Paint()
      ..color = const Color(0x1A64503C)
      ..strokeWidth = 1;
    const spacing = 42.0;
    final horizon = size.height * 0.56;

    for (double y = horizon; y < size.height; y += spacing) {
      canvas.drawLine(Offset(0, y), Offset(size.width, y), paint);
    }
    for (double x = -size.width; x < size.width * 2; x += spacing) {
      canvas.drawLine(
        Offset(size.width / 2, horizon),
        Offset(x, size.height),
        paint,
      );
    }
  }

  @override
  bool shouldRepaint(covariant CustomPainter oldDelegate) => false;
}
