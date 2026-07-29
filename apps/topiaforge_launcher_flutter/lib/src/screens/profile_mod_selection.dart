part of '../screens.dart';

class ProfileModSelection extends StatelessWidget {
  const ProfileModSelection({
    super.key,
    required this.state,
    required this.profile,
  });

  final LauncherState state;
  final LauncherProfile profile;

  @override
  Widget build(BuildContext context) {
    final mods = state.installedMods
        .where((mod) => !mod.uninstallPending)
        .toList(growable: false);
    final effectiveEnabled = profile.inheritManagerModState
        ? {for (final mod in mods.where((mod) => mod.enabled)) mod.id}
        : profile.enabledMods;
    final listHeight = (mods.length * 62.0).clamp(96.0, 320.0);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        SwitchListTile(
          contentPadding: EdgeInsets.zero,
          title: const Text('Use global mod choices'),
          subtitle: const Text(
            'Turn this off to keep an independent set for this profile.',
          ),
          value: profile.inheritManagerModState,
          onChanged: (enabled) =>
              _add(context, ProfileManagerStateInheritanceChanged(enabled)),
        ),
        Text('Installed mods', style: Theme.of(context).textTheme.labelLarge),
        const SizedBox(height: 4),
        if (mods.isEmpty)
          const Text('No installed mods are available for this profile.')
        else
          SizedBox(
            height: listHeight,
            child: DecoratedBox(
              decoration: BoxDecoration(
                border: Border.all(color: Theme.of(context).dividerColor),
                borderRadius: BorderRadius.circular(8),
              ),
              child: ListView.builder(
                itemCount: mods.length,
                itemBuilder: (context, index) {
                  final mod = mods[index];
                  final selected = _profileContains(effectiveEnabled, mod.id);
                  final requiredCount =
                      mod.manifest?.dependencies
                          .where((item) => !item.optional)
                          .length ??
                      0;
                  return CheckboxListTile(
                    key: ValueKey('profile-mod-${mod.id}'),
                    dense: true,
                    controlAffinity: ListTileControlAffinity.leading,
                    value: selected,
                    title: Text(mod.name, overflow: TextOverflow.ellipsis),
                    subtitle: Text(
                      [
                        mod.version,
                        if (mod.manifest?.category == 'DevTool')
                          'developer tool',
                        if (requiredCount > 0) '$requiredCount required',
                      ].join('  '),
                      overflow: TextOverflow.ellipsis,
                    ),
                    onChanged: state.isBusy
                        ? null
                        : (enabled) => _add(
                            context,
                            ProfileModSelectionChanged(
                              mod.id,
                              enabled ?? false,
                            ),
                          ),
                  );
                },
              ),
            ),
          ),
        const SizedBox(height: 8),
      ],
    );
  }
}

bool _profileContains(Set<String> ids, String id) =>
    ids.any((candidate) => candidate.toLowerCase() == id.toLowerCase());
