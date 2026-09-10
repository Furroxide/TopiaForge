part of '../screens.dart';

const _mainMenuChoice = '__main_menu__';
const _unresolvedChoice = '__unresolved__';
const _defaultOverrideChoice = '__target_default__';

class _LaunchTargetPicker extends StatelessWidget {
  const _LaunchTargetPicker({required this.state});
  final LauncherState state;

  @override
  Widget build(BuildContext context) {
    final profile = state.selectedProfile;
    final preview = state.launchPreview;
    final selection = profile == null
        ? const LaunchSelection.mainMenu()
        : _visibleLaunchSelection(state, profile);
    final safeMode = profile?.launchSettings.safeMode == true;
    final selected = safeMode || selection.kind == LaunchSelectionKind.mainMenu
        ? _mainMenuChoice
        : selection.request?.targetId ?? _unresolvedChoice;
    final targets = preview?.targets ?? const <LaunchTargetChoice>[];
    final unknown =
        selected != _mainMenuChoice &&
        !targets.any((target) => target.id == selected);
    return DropdownButtonFormField<String>(
      key: ValueKey(
        'launch-target-${profile?.id}-${profile?.revision}-$selected',
      ),
      initialValue: selected,
      isExpanded: true,
      decoration: const InputDecoration(labelText: 'Launch target'),
      items: [
        const DropdownMenuItem(
          value: _mainMenuChoice,
          child: Text('Main menu'),
        ),
        if (unknown)
          DropdownMenuItem(
            value: selected,
            enabled: false,
            child: Text(
              selection.kind == LaunchSelectionKind.unresolvedLegacy
                  ? 'Unavailable launch selection'
                  : 'Unavailable: $selected',
              overflow: TextOverflow.ellipsis,
            ),
          ),
        for (final target in targets)
          DropdownMenuItem(
            value: target.id,
            enabled: target.selectable,
            child: Text(target.title, overflow: TextOverflow.ellipsis),
          ),
      ],
      onChanged: state.isBusy || safeMode || preview == null
          ? null
          : (value) {
              if (value == null || value == _unresolvedChoice) return;
              _add(
                context,
                LaunchSelectionChanged(
                  value == _mainMenuChoice
                      ? const LaunchSelection.mainMenu()
                      : LaunchSelection.target(LaunchRequest(targetId: value)),
                ),
              );
            },
    );
  }
}

class _LaunchTargetControls extends StatelessWidget {
  const _LaunchTargetControls({required this.state, this.showPicker = true});
  final LauncherState state;
  final bool showPicker;

  @override
  Widget build(BuildContext context) {
    final profile = state.selectedProfile;
    if (profile == null) return const Text('Create or select a profile.');
    final selection = _visibleLaunchSelection(state, profile);
    final preview = state.previewFor(profile);
    final request = selection.request;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        if (showPicker) _LaunchTargetPicker(state: state),
        if (preview == null) ...[
          const SizedBox(height: 10),
          const Text('Loading launch options…'),
        ] else if (preview.targets.isEmpty) ...[
          const SizedBox(height: 10),
          const Text('No launch targets available'),
          const Text(
            'Install and enable a package that declares a launch target, or open the main menu.',
          ),
        ],
        if (profile.launchSettings.safeMode) ...[
          const SizedBox(height: 10),
          const Text(
            'Safe mode opens the main menu. Your saved launch selection is preserved.',
          ),
        ] else if (selection.kind == LaunchSelectionKind.unresolvedLegacy) ...[
          const SizedBox(height: 10),
          Text('Saved selection: ${jsonEncode(selection.legacy)}'),
          const Text(
            'This older selection has no unique available target. Choose a target above or explicitly use the main menu.',
          ),
          const SizedBox(height: 8),
          OutlinedButton.icon(
            onPressed: state.isBusy
                ? null
                : () => _add(
                    context,
                    const LaunchSelectionChanged(LaunchSelection.mainMenu()),
                  ),
            icon: const Icon(Icons.home_outlined),
            label: const Text('Use main menu'),
          ),
        ] else if (request != null && preview != null) ...[
          const SizedBox(height: 12),
          _WorldOverrideControl(
            state: state,
            preview: preview,
            request: request,
          ),
          const SizedBox(height: 12),
          _TransitionOverrideControl(
            state: state,
            preview: preview,
            request: request,
          ),
        ],
        if (preview != null) ...[
          for (final issue in preview.issues) _issueTile(issue),
          for (final block
              in preview.resolution?.blocks ?? const <LaunchBlock>[])
            _issueTile(
              LauncherIssue(
                severity: IssueSeverity.error,
                message: block.message,
              ),
            ),
        ],
      ],
    );
  }
}

class _WorldOverrideControl extends StatelessWidget {
  const _WorldOverrideControl({
    required this.state,
    required this.preview,
    required this.request,
  });
  final LauncherState state;
  final LaunchPreview preview;
  final LaunchRequest request;

  @override
  Widget build(BuildContext context) {
    final value = request.worldOverride ?? _defaultOverrideChoice;
    final missing =
        request.worldOverride != null &&
        !preview.worlds.any((world) => world.id == request.worldOverride);
    return DropdownButtonFormField<String>(
      key: ValueKey('world-override-${request.targetId}-$value'),
      initialValue: value,
      isExpanded: true,
      decoration: InputDecoration(
        labelText: 'World',
        helperText: preview.allowWorldOverride
            ? null
            : 'Determined by the launch target',
      ),
      items: [
        DropdownMenuItem(
          value: _defaultOverrideChoice,
          child: Text(_resolvedWorldLabel(preview)),
        ),
        if (missing)
          DropdownMenuItem(
            value: value,
            enabled: false,
            child: Text('Unavailable: $value', overflow: TextOverflow.ellipsis),
          ),
        if (preview.allowWorldOverride)
          for (final world in preview.worlds)
            DropdownMenuItem(
              value: world.id,
              enabled: world.available,
              child: Text(world.name, overflow: TextOverflow.ellipsis),
            ),
        if (!preview.allowWorldOverride &&
            request.worldOverride != null &&
            !missing)
          DropdownMenuItem(
            value: value,
            enabled: false,
            child: Text(
              'Unavailable override: $value',
              overflow: TextOverflow.ellipsis,
            ),
          ),
      ],
      onChanged: !preview.allowWorldOverride || state.isBusy
          ? null
          : (selected) {
              if (selected == null) return;
              _add(
                context,
                LaunchSelectionChanged(
                  LaunchSelection.target(
                    LaunchRequest(
                      targetId: request.targetId,
                      worldOverride: selected == _defaultOverrideChoice
                          ? null
                          : selected,
                    ),
                  ),
                ),
              );
            },
    );
  }
}

class _TransitionOverrideControl extends StatelessWidget {
  const _TransitionOverrideControl({
    required this.state,
    required this.preview,
    required this.request,
  });
  final LauncherState state;
  final LaunchPreview preview;
  final LaunchRequest request;
  @override
  Widget build(BuildContext context) {
    final value = request.transitionOverride ?? _defaultOverrideChoice;
    final missing =
        request.transitionOverride != null &&
        !preview.transitions.contains(request.transitionOverride);
    return DropdownButtonFormField<String>(
      key: ValueKey(
        'transition-override-${request.targetId}-${request.worldOverride}-$value',
      ),
      initialValue: value,
      isExpanded: true,
      decoration: InputDecoration(
        labelText: 'Transition',
        helperText: preview.allowTransitionOverride
            ? null
            : 'Determined by the launch target',
      ),
      items: [
        const DropdownMenuItem(
          value: _defaultOverrideChoice,
          child: Text('Use target policy'),
        ),
        if (missing ||
            (!preview.allowTransitionOverride &&
                request.transitionOverride != null))
          DropdownMenuItem(
            value: value,
            enabled: false,
            child: Text('Unavailable: $value', overflow: TextOverflow.ellipsis),
          ),
        if (preview.allowTransitionOverride)
          for (final transition in preview.transitions)
            DropdownMenuItem(
              value: transition,
              child: Text(_transitionLabel(transition)),
            ),
      ],
      onChanged: !preview.allowTransitionOverride || state.isBusy
          ? null
          : (selected) {
              if (selected == null) return;
              _add(
                context,
                LaunchSelectionChanged(
                  LaunchSelection.target(
                    LaunchRequest(
                      targetId: request.targetId,
                      worldOverride: request.worldOverride,
                      transitionOverride: selected == _defaultOverrideChoice
                          ? null
                          : selected,
                    ),
                  ),
                ),
              );
            },
    );
  }
}

String _transitionLabel(String transition) => switch (transition) {
  ModTransitions.additiveArena => 'Additive arena',
  ModTransitions.sceneReplacement => 'Scene replacement',
  _ => 'Unavailable: $transition',
};

String _resolvedWorldLabel(LaunchPreview preview) => 'Use target default';
