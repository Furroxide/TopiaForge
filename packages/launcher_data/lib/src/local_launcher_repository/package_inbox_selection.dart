part of '../local_launcher_repository.dart';

const _maxInboxSelectionSearchNodes = 100000;

extension _PackageInboxSelection on LocalLauncherRepository {
  Future<_InboxSelectionAssessment> _assessInboxSelection(
    List<_InboxCandidate> candidates,
    GameInstall install,
  ) async {
    final valid = candidates.where((candidate) => candidate.isValid).toList();
    if (valid.isEmpty) return const _InboxSelectionAssessment();
    try {
      final installed = await _loadInstalledMods(install);
      final sources = await _loadPackageSources();
      final registry = await _loadRegistryCandidates(installed, sources);
      return _selectCompatibleInboxBatch(valid, installed, registry, install);
    } on Object catch (error) {
      return _blockedInboxSelection(valid, error);
    }
  }

  _InboxSelectionAssessment _selectCompatibleInboxBatch(
    List<_InboxCandidate> valid,
    List<InstalledMod> installed,
    List<RegistryMod> registry,
    GameInstall install,
  ) {
    final rejected = <_InboxCandidate>{};
    final issues = <LauncherIssue>[];
    final ordered = valid.toList()
      ..sort((left, right) {
        final group = left.groupKey.compareTo(right.groupKey);
        return group != 0 ? group : _compareInboxSelection(left, right);
      });

    // Remove candidates that cannot form a complete plan even when every
    // valid inbox alternative is available. Repeat because rejecting a sole
    // provider can make a previously viable consumer impossible.
    for (var pass = 0; pass <= valid.length; pass++) {
      final availableCandidates = ordered
          .where((candidate) => !rejected.contains(candidate))
          .toList(growable: false);
      final available = <RegistryMod>[
        ...registry,
        ...availableCandidates.map(_registryModForInboxCandidate),
      ];
      var changed = false;
      for (final candidate in availableCandidates) {
        final blocking = _inboxPlanBlockers(
          candidate,
          installed,
          available,
          install,
        );
        if (blocking.isEmpty) continue;
        _rejectInboxCandidate(
          candidate,
          rejected,
          issues,
          blocking.map((issue) => issue.message).join(' '),
        );
        changed = true;
      }
      if (!changed) break;
    }

    final groups = <String, List<_InboxCandidate>>{};
    for (final candidate in ordered.where(
      (candidate) => !rejected.contains(candidate),
    )) {
      groups.putIfAbsent(candidate.groupKey, () => []).add(candidate);
    }
    for (final group in groups.values) {
      group.sort(_compareInboxSelection);
    }
    final activeGroups = groups.keys.toList()..sort();

    // Search exact ID/version combinations by descending root count. The first
    // result is therefore a maximum-cardinality compatible batch; candidate-
    // before-skip traversal then makes equal-size choices deterministic and
    // prefers the highest versions in normalized ID order.
    final search = _searchInboxBatch(
      activeGroups,
      groups,
      installed,
      registry,
      install,
    );
    if (search.limitReached || search.selected == null) {
      return _blockedInboxSelection(
        valid,
        StateError(
          'the bounded $_maxInboxSelectionSearchNodes-node dependency '
          'search limit was reached',
        ),
        priorIssues: issues,
      );
    }

    final selected = search.selected!;
    for (final groupKey in activeGroups) {
      if (selected.containsKey(groupKey)) continue;
      final omitted = groups[groupKey]!;
      rejected.addAll(omitted);
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: omitted.first.fileName,
          message:
              'TFINBOX115: No version of ${omitted.first.manifest!.id} can '
              'participate in the maximum compatible inbox plan. Its '
              'candidates were retained.',
        ),
      );
    }
    return _InboxSelectionAssessment(
      selected: Map.unmodifiable(selected),
      rejected: rejected,
      issues: issues,
    );
  }

  _InboxBatchSearchResult _searchInboxBatch(
    List<String> groupKeys,
    Map<String, List<_InboxCandidate>> groups,
    List<InstalledMod> installed,
    List<RegistryMod> registry,
    GameInstall install,
  ) {
    final active = groupKeys.toSet();
    final selected = <String, _InboxCandidate>{};
    var visited = 0;
    var limitReached = false;

    bool visit(
      int index,
      int selectedCount,
      int targetCount,
      Map<String, List<VersionRange>> constraints,
    ) {
      final remaining = groupKeys.length - index;
      if (selectedCount > targetCount ||
          selectedCount + remaining < targetCount) {
        return false;
      }
      if (index == groupKeys.length) {
        return selectedCount == targetCount &&
            _isCompleteInboxBatch(selected, installed, registry, install);
      }
      final groupKey = groupKeys[index];
      final incoming = constraints[groupKey] ?? const <VersionRange>[];
      if (selectedCount < targetCount) {
        for (final candidate in groups[groupKey]!) {
          visited += 1;
          if (visited > _maxInboxSelectionSearchNodes) {
            limitReached = true;
            return false;
          }
          if (!incoming.every(
            (range) => range.allows(candidate.manifest!.version),
          )) {
            continue;
          }
          final nextConstraints = <String, List<VersionRange>>{
            for (final entry in constraints.entries)
              entry.key: List<VersionRange>.of(entry.value),
          };
          var viable = true;
          for (final dependency in candidate.manifest!.dependencies) {
            final target = 'id:${_normalizedInboxId(dependency.id)}';
            if (!active.contains(target)) continue;
            final ranges = nextConstraints.putIfAbsent(target, () => []);
            ranges.add(dependency.versionRange);
            final assigned = selected[target];
            if (assigned != null) {
              viable = ranges.every(
                (range) => range.allows(assigned.manifest!.version),
              );
            }
            if (!viable) break;
          }
          if (!viable) continue;
          selected[groupKey] = candidate;
          final conflicts = _hasInboxRootConflict(selected.values);
          if (!conflicts &&
              visit(
                index + 1,
                selectedCount + 1,
                targetCount,
                nextConstraints,
              )) {
            return true;
          }
          selected.remove(groupKey);
          if (limitReached) return false;
        }
      }

      if (selectedCount + remaining - 1 >= targetCount) {
        visited += 1;
        if (visited > _maxInboxSelectionSearchNodes) {
          limitReached = true;
          return false;
        }
        if (visit(index + 1, selectedCount, targetCount, constraints)) {
          return true;
        }
      }
      return false;
    }

    var found = false;
    for (var target = groupKeys.length; target >= 0; target--) {
      selected.clear();
      found = visit(0, 0, target, const {});
      if (found || limitReached) break;
    }
    return _InboxBatchSearchResult(
      selected: found ? Map.of(selected) : null,
      limitReached: limitReached,
    );
  }

  bool _isCompleteInboxBatch(
    Map<String, _InboxCandidate> selected,
    List<InstalledMod> installed,
    List<RegistryMod> registry,
    GameInstall install,
  ) {
    if (_hasInboxRootConflict(selected.values)) return false;
    final selectedIds = selected.values
        .map((candidate) => candidate.manifest!.id.toLowerCase())
        .toSet();
    final planningInstalled = installed
        .where((mod) => !selectedIds.contains(mod.id.toLowerCase()))
        .toList(growable: false);
    final available = <RegistryMod>[
      ...registry.where(
        (mod) => !selectedIds.contains(mod.manifest.id.toLowerCase()),
      ),
      ...selected.values.map(_registryModForInboxCandidate),
    ];
    return selected.values.every(
      (candidate) => _inboxPlanBlockers(
        candidate,
        planningInstalled,
        available,
        install,
      ).isEmpty,
    );
  }

  bool _hasInboxRootConflict(Iterable<_InboxCandidate> candidates) {
    final byId = {
      for (final candidate in candidates)
        _normalizedInboxId(candidate.manifest!.id): candidate,
    };
    for (final candidate in candidates) {
      for (final conflict in candidate.manifest!.conflicts) {
        final other = byId[_normalizedInboxId(conflict.id)];
        if (other != null &&
            conflict.versionRange.allows(other.manifest!.version)) {
          return true;
        }
      }
    }
    return false;
  }

  List<LauncherIssue> _inboxPlanBlockers(
    _InboxCandidate candidate,
    List<InstalledMod> installed,
    List<RegistryMod> available,
    GameInstall install,
  ) => _dependencyPlanner
      .previewInstall(
        candidate.manifest!,
        installed,
        packageSha256: candidate.sha256,
        packageUrl: candidate.file.path,
        sourceId: 'inbox',
        sourceName: 'Package inbox',
        availableMods: available,
        gameVersion: install.gameVersion,
        requireKnownGameVersion: true,
        loaderVersion: _loaderVersion,
        sdkVersion: _sdkVersion,
        platform: _gamePlatform(install),
        architecture: _gameArchitecture(install),
        contentTargets: _gameContentTargets(install),
      )
      .issues
      .where((issue) => issue.isBlocking)
      .toList(growable: false);
}

RegistryMod _registryModForInboxCandidate(_InboxCandidate candidate) =>
    RegistryMod(
      manifest: candidate.manifest!,
      downloadUrl: candidate.file.path,
      packageSha256: candidate.sha256,
      sourceId: 'inbox',
      sourceName: 'Package inbox',
    );

void _rejectInboxCandidate(
  _InboxCandidate candidate,
  Set<_InboxCandidate> rejected,
  List<LauncherIssue> issues,
  String reason,
) {
  if (!rejected.add(candidate)) return;
  issues.add(
    LauncherIssue(
      severity: IssueSeverity.error,
      subjectId: candidate.fileName,
      message:
          'TFINBOX115: ${candidate.fileName} cannot participate in a complete '
          'compatible install plan and was retained. $reason',
    ),
  );
}

_InboxSelectionAssessment _blockedInboxSelection(
  List<_InboxCandidate> valid,
  Object error, {
  List<LauncherIssue> priorIssues = const [],
}) => _InboxSelectionAssessment(
  rejected: valid.toSet(),
  issues: [
    ...priorIssues,
    LauncherIssue(
      severity: IssueSeverity.error,
      subjectId: 'package-inbox',
      message:
          'TFINBOX114: Package installability could not be assessed safely, '
          'so all valid candidates were retained. $error',
    ),
  ],
);

class _InboxBatchSearchResult {
  const _InboxBatchSearchResult({
    required this.selected,
    required this.limitReached,
  });

  final Map<String, _InboxCandidate>? selected;
  final bool limitReached;
}

class _InboxSelectionAssessment {
  const _InboxSelectionAssessment({
    this.selected = const {},
    this.rejected = const {},
    this.issues = const [],
  });

  final Map<String, _InboxCandidate> selected;
  final Set<_InboxCandidate> rejected;
  final List<LauncherIssue> issues;
}
