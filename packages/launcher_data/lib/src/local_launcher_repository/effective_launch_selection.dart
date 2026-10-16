part of '../local_launcher_repository.dart';

final class _EffectiveLaunchSelection {
  _EffectiveLaunchSelection(
    this.profile,
    Iterable<InstalledMod> selected,
    Iterable<LauncherIssue> issues,
  ) : selected = List.unmodifiable(selected),
      issues = List.unmodifiable(issues);
  final EffectiveProfile profile;
  final List<InstalledMod> selected;
  final List<LauncherIssue> issues;
}

extension _EffectiveLaunchSelectionBuilder on LocalLauncherRepository {
  Future<_EffectiveLaunchSelection> _effectiveLaunchSelection(
    GameInstall install,
    LauncherProfile profile,
  ) async {
    final Map<String, Object?> state;
    try {
      state = await _readManagerState(install, allowMalformedRecords: true);
    } on _ManagerStateContentException catch (error) {
      if (!profile.launchSettings.safeMode) rethrow;
      // Runtime admits only explicit safe main-menu in read-only recovery.
      // Invalid content remains on disk and never supplies package authority.
      return _EffectiveLaunchSelection(
        EffectiveProfile(
          profileId: profile.id,
          revision: profile.revision,
          packages: const [],
          install: InstallFacts.withContentTargets(
            platform: _gamePlatform(install),
            architecture: _gameArchitecture(install) ?? '',
            contentTargets: _gameContentTargets(install),
            gameVersion: install.gameVersion ?? '',
          ),
        ),
        const [],
        [
          LauncherIssue(
            severity: IssueSeverity.warning,
            subjectId: profile.id,
            message:
                'Safe Mode will open Main Menu with no packages and preserve the unreadable manager state for repair. $error',
          ),
        ],
      );
    }
    final issues = <LauncherIssue>[];
    final chosen = <String, InstalledMod>{};
    final competing = <String, List<InstalledMod>>{};
    final enabled = <String>{};
    final explicit = {for (final id in profile.enabledMods) id.toLowerCase()};
    void reject(String id, String detail) => issues.add(
      LauncherIssue(
        severity: profile.launchSettings.safeMode
            ? IssueSeverity.warning
            : IssueSeverity.error,
        subjectId: id,
        message: detail,
      ),
    );
    for (final issue in _managerStateRecordIssues(state)) {
      reject(issue.$1, issue.$2);
    }
    final stateById = _stateByModId(state);
    final catalog = await _loadInstalledVersionCatalog(
      install,
      stateById: stateById,
    );
    for (final entry in catalog.entries) {
      if (entry.value.isEmpty) continue;
      final stateItem = stateById[entry.key];
      var selected = _pickCurrentVersion(entry.value, stateItem);
      final isEnabled = profile.inheritManagerModState
          ? selected.enabled
          : explicit.contains(entry.key);
      if (isEnabled) {
        enabled.add(entry.key);
        String? pin;
        for (final item in profile.selectedVersions.entries) {
          if (item.key.toLowerCase() == entry.key) {
            pin = item.value;
            break;
          }
        }
        if (pin != null) {
          selected = _pickCurrentVersion(entry.value, {
            ...?stateItem,
            'id': entry.key,
            'version': pin,
            'versionPinned': true,
            'enabled': true,
          });
        }
        final matches = entry.value
            .where((item) => item.version == selected.version)
            .toList();
        if (matches.length > 1) {
          competing[entry.key] = matches;
          reject(
            entry.key,
            'Selected package ${entry.key}@${selected.version} has multiple installed owners: ${matches.map((item) => item.packagePath).join(', ')}. Repair the duplicate installation.',
          );
        }
      }
      chosen[entry.key] = selected;
    }
    if (profile.inheritManagerModState) {
      for (final raw in (state['mods'] as List).whereType<Map>()) {
        if (raw['enabled'] != true) continue;
        final id = raw['id'];
        if (id is! String || !ModManifest.isValidId(id)) {
          reject(
            id is String ? id : '(missing id)',
            'Manager state contains an enabled malformed package identity. Repair or disable the original entry.',
          );
        } else {
          enabled.add(id.toLowerCase());
        }
      }
    } else {
      enabled.addAll(explicit);
    }

    final selected = <InstalledMod>[];
    final packages = <ResolvedPackage>[];
    final disabled = <ResolvedPackage>[];
    for (final id in enabled.toList()..sort()) {
      final mod = chosen[id];
      if (mod == null) {
        reject(
          id,
          'Profile ${profile.id} selects unavailable package $id. Repair, install or disable it.',
        );
        continue;
      }
      for (final mod in competing[id] ?? [mod]) {
        selected.add(_profileEnabledMod(mod));
        if (!mod.isValid || mod.uninstallPending) {
          reject(
            id,
            'Selected package $id@${mod.version} at ${mod.packagePath} cannot launch: ${[...mod.errors, if (mod.uninstallPending) 'Uninstall is pending.'].join(' ')}',
          );
        }
        try {
          if (mod.manifest == null) {
            throw const FormatException('Manifest is unavailable.');
          }
          packages.add(
            ResolvedPackage(
              id: mod.id,
              version: mod.version,
              manifest: mod.manifest!,
            ),
          );
        } on Object catch (error) {
          reject(
            id,
            'Selected package identity cannot enter a launch plan: ${mod.id}@${mod.version} at ${mod.packagePath}. Repair or disable this package. $error',
          );
        }
      }
    }
    final owners = <String, List<InstalledMod>>{};
    for (final mod in selected) {
      owners.putIfAbsent(mod.id.toLowerCase(), () => []).add(mod);
    }
    for (final entry in owners.entries.where(
      (entry) => entry.value.length > 1,
    )) {
      reject(
        entry.key,
        'Multiple selected package directories declare ${entry.key}: ${entry.value.map((mod) => mod.packagePath).join(', ')}. No owner can be chosen; repair the duplicates.',
      );
    }
    for (final entry in chosen.entries.where(
      (entry) => !enabled.contains(entry.key),
    )) {
      final mod = entry.value;
      try {
        if (mod.manifest != null) {
          disabled.add(
            ResolvedPackage(
              id: mod.id,
              version: mod.version,
              manifest: mod.manifest!,
            ),
          );
        }
      } on Object {
        /* Invalid disabled scans stay visible in the installed list, without becoming authority. */
      }
    }
    if (!profile.launchSettings.safeMode) {
      issues.addAll(
        _dependencyPlanner
            .resolveInstalled(
              selected,
              gameVersion: install.gameVersion,
              requireKnownGameVersion: true,
              loaderVersion: _loaderVersion,
              sdkVersion: _sdkVersion,
              platform: _gamePlatform(install),
              architecture: _gameArchitecture(install),
              contentTargets: _gameContentTargets(install),
            )
            .issues,
      );
    }
    final uniqueIssues = <String, LauncherIssue>{};
    for (final issue in issues) {
      uniqueIssues['${issue.severity.name}:${issue.subjectId ?? ''}:${issue.message}'] =
          issue;
    }
    final ordered = uniqueIssues.keys.toList()..sort();
    return _EffectiveLaunchSelection(
      EffectiveProfile(
        profileId: profile.id,
        revision: profile.revision,
        packages: profile.launchSettings.safeMode ? const [] : packages,
        disabledPackages: profile.launchSettings.safeMode
            ? [...disabled, ...packages]
            : disabled,
        install: InstallFacts.withContentTargets(
          platform: _gamePlatform(install),
          architecture: _gameArchitecture(install) ?? '',
          contentTargets: _gameContentTargets(install),
          gameVersion: install.gameVersion ?? '',
        ),
      ),
      selected,
      ordered.map((key) => uniqueIssues[key]!),
    );
  }
}
