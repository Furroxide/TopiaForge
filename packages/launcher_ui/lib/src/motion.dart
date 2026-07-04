import 'package:flutter/material.dart';

import 'launcher_theme.dart';

/// Whether the platform asks for reduced motion (e.g. Windows "Animation
/// effects" off). Every motion primitive in this file collapses to a static
/// presentation when this returns true.
bool motionDisabled(BuildContext context) =>
    MediaQuery.disableAnimationsOf(context);

/// Scales its child up slightly while hovered, selling the "sticker lift"
/// used across the launcher's cards.
class HoverLift extends StatefulWidget {
  const HoverLift({
    super.key,
    required this.child,
    this.scale = 1.02,
    this.enabled = true,
  });

  final Widget child;
  final double scale;
  final bool enabled;

  @override
  State<HoverLift> createState() => _HoverLiftState();
}

class _HoverLiftState extends State<HoverLift> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final lifted = _hovered && widget.enabled;
    return MouseRegion(
      cursor: widget.enabled
          ? SystemMouseCursors.click
          : SystemMouseCursors.basic,
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: AnimatedScale(
        scale: lifted ? widget.scale : 1.0,
        duration: motionDisabled(context)
            ? Duration.zero
            : const Duration(milliseconds: 180),
        curve: Curves.easeOut,
        child: widget.child,
      ),
    );
  }
}

/// The hero call-to-action: a large gradient button with a slow pulsing glow
/// while enabled. The glow pauses when disabled or when the platform requests
/// reduced motion (then it renders a static mid-glow).
class GlowButton extends StatefulWidget {
  const GlowButton({
    super.key,
    required this.label,
    required this.icon,
    this.onPressed,
    this.height = 60,
  });

  final String label;
  final IconData icon;
  final VoidCallback? onPressed;
  final double height;

  @override
  State<GlowButton> createState() => _GlowButtonState();
}

class _GlowButtonState extends State<GlowButton>
    with SingleTickerProviderStateMixin {
  late final AnimationController _controller = AnimationController(
    vsync: this,
    duration: const Duration(milliseconds: 1600),
  );

  bool get _enabled => widget.onPressed != null;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _syncAnimation();
  }

  @override
  void didUpdateWidget(GlowButton oldWidget) {
    super.didUpdateWidget(oldWidget);
    _syncAnimation();
  }

  void _syncAnimation() {
    // MediaQuery is unavailable in initState, so the controller is started
    // here instead. Reduced motion holds a static mid-glow frame.
    if (_enabled && !motionDisabled(context)) {
      if (!_controller.isAnimating) {
        _controller.repeat(reverse: true);
      }
    } else {
      _controller.stop();
      _controller.value = _enabled ? 0.5 : 0.0;
    }
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final display = Theme.of(context).textTheme.titleLarge!;
    return Semantics(
      button: true,
      enabled: _enabled,
      child: HoverLift(
        scale: 1.03,
        enabled: _enabled,
        child: AnimatedBuilder(
          animation: _controller,
          builder: (context, child) {
            final t = _enabled
                ? Curves.easeInOut.transform(_controller.value)
                : 0.0;
            return Container(
              height: widget.height,
              decoration: BoxDecoration(
                gradient: _enabled
                    ? const LinearGradient(
                        colors: [
                          QuantumWorksPalette.launch,
                          QuantumWorksPalette.magenta,
                        ],
                      )
                    : null,
                color: _enabled ? null : QuantumWorksPalette.surfaceTint,
                border: Border.all(
                  color: _enabled
                      ? QuantumWorksPalette.launchDark
                      : QuantumWorksPalette.surfaceTint,
                  width: 2.5,
                ),
                borderRadius: BorderRadius.circular(22),
                boxShadow: _enabled
                    ? [
                        BoxShadow(
                          color: QuantumWorksPalette.launch.withValues(
                            alpha: 0.20 + 0.22 * t,
                          ),
                          blurRadius: 16 + 12 * t,
                          spreadRadius: 1 + 2.5 * t,
                        ),
                      ]
                    : null,
              ),
              child: child,
            );
          },
          child: Material(
            type: MaterialType.transparency,
            child: InkWell(
              borderRadius: BorderRadius.circular(22),
              onTap: widget.onPressed,
              child: Padding(
                padding: const EdgeInsets.symmetric(horizontal: 26),
                child: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Icon(
                      widget.icon,
                      size: 26,
                      color: _enabled
                          ? QuantumWorksPalette.white
                          : QuantumWorksPalette.faintText,
                    ),
                    const SizedBox(width: 12),
                    Text(
                      widget.label.toUpperCase(),
                      style: display.copyWith(
                        color: _enabled
                            ? QuantumWorksPalette.white
                            : QuantumWorksPalette.faintText,
                        fontSize: 20,
                        letterSpacing: 1.2,
                      ),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

/// Fades and slides its child in once, delayed by [index] so sibling zones
/// enter as a stagger. Finite (no repeating ticker), so widget tests can
/// pumpAndSettle across it.
class StaggeredReveal extends StatelessWidget {
  const StaggeredReveal({super.key, required this.index, required this.child});

  final int index;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    if (motionDisabled(context)) {
      return child;
    }
    final duration = Duration(milliseconds: 320 + 60 * index);
    final delayFraction = index == 0 ? 0.0 : (60.0 * index) / (320 + 60 * index);
    return TweenAnimationBuilder<double>(
      tween: Tween(begin: 0, end: 1),
      duration: duration,
      curve: Interval(delayFraction, 1, curve: Curves.easeOutCubic),
      builder: (context, t, child) => Opacity(
        opacity: t,
        child: Transform.translate(
          offset: Offset(0, 14 * (1 - t)),
          child: child,
        ),
      ),
      child: child,
    );
  }
}
