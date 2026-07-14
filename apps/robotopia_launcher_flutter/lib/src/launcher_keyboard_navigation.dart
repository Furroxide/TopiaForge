import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import 'launcher_section.dart';

class LauncherKeyboardNavigation extends StatefulWidget {
  const LauncherKeyboardNavigation({
    super.key,
    required this.currentSection,
    required this.visibleSections,
    required this.onSectionSelected,
    required this.child,
  });

  final LauncherSection currentSection;
  final List<LauncherSection> visibleSections;
  final ValueChanged<LauncherSection> onSectionSelected;
  final Widget child;

  @override
  State<LauncherKeyboardNavigation> createState() =>
      _LauncherKeyboardNavigationState();
}

class _LauncherKeyboardNavigationState
    extends State<LauncherKeyboardNavigation> {
  final FocusNode _focusNode = FocusNode(
    debugLabel: 'Launcher keyboard navigation',
  );
  bool _restoreFocus = false;

  @override
  void didUpdateWidget(LauncherKeyboardNavigation oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (_restoreFocus && oldWidget.currentSection != widget.currentSection) {
      _restoreFocus = false;
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted) _focusNode.requestFocus();
      });
    }
  }

  @override
  void dispose() {
    _focusNode.dispose();
    super.dispose();
  }

  void _select(LauncherSection section) {
    _restoreFocus = true;
    widget.onSectionSelected(section);
  }

  void _move(int delta) {
    final sections = widget.visibleSections;
    if (sections.isEmpty) return;
    final current = sections.indexOf(widget.currentSection);
    final next = ((current < 0 ? 0 : current) + delta) % sections.length;
    _select(sections[next]);
  }

  @override
  Widget build(BuildContext context) {
    final digits = <LogicalKeyboardKey>[
      LogicalKeyboardKey.digit1,
      LogicalKeyboardKey.digit2,
      LogicalKeyboardKey.digit3,
      LogicalKeyboardKey.digit4,
      LogicalKeyboardKey.digit5,
      LogicalKeyboardKey.digit6,
      LogicalKeyboardKey.digit7,
      LogicalKeyboardKey.digit8,
    ];
    return CallbackShortcuts(
      bindings: {
        const SingleActivator(LogicalKeyboardKey.arrowLeft, alt: true): () =>
            _move(-1),
        const SingleActivator(LogicalKeyboardKey.arrowRight, alt: true): () =>
            _move(1),
        for (
          var index = 0;
          index < widget.visibleSections.length && index < digits.length;
          index++
        )
          SingleActivator(digits[index], control: true): () =>
              _select(widget.visibleSections[index]),
      },
      child: FocusTraversalGroup(
        policy: OrderedTraversalPolicy(),
        child: Focus(
          key: const ValueKey('launcher-shell-focus'),
          focusNode: _focusNode,
          autofocus: true,
          child: widget.child,
        ),
      ),
    );
  }
}
