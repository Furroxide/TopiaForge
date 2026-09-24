part of '../models.dart';

final class LaunchTargetChoice {
  LaunchTargetChoice({
    required this.id,
    required this.title,
    required PackageIdentity owner,
    this.description = '',
    this.order = 0,
    Iterable<LaunchBlock> blocks = const [],
  }) : owner = PackageIdentity.fromJson(owner.toJson()),
       blocks = _previewBlocks(blocks);
  final String id;
  final String title;
  final String description;
  final PackageIdentity owner;
  final int order;
  final List<LaunchBlock> blocks;
  bool get available => blocks.isEmpty;
  bool get selectable => !blocks.any(
    (block) => const {
      LaunchBlockCode.targetNotDeclared,
      LaunchBlockCode.targetPackageDisabled,
      LaunchBlockCode.declarationIdAmbiguous,
    }.contains(block.code),
  );
}

final class LaunchWorldChoice {
  LaunchWorldChoice({
    required this.id,
    required this.name,
    required PackageIdentity owner,
    this.familyId,
    Iterable<LaunchBlock> blocks = const [],
  }) : owner = PackageIdentity.fromJson(owner.toJson()),
       blocks = _previewBlocks(blocks);
  final String id;
  final String name;
  final String? familyId;
  final PackageIdentity owner;
  final List<LaunchBlock> blocks;
  bool get available => blocks.isEmpty;
}

/// An immutable, profile-specific view; installed manifests remain authority.
final class LaunchPreview {
  LaunchPreview({
    required this.profileId,
    required this.profileRevision,
    required this.installIdentity,
    required this.packageDigest,
    required this.requestedSelection,
    required this.effectiveSelection,
    Iterable<PackageIdentity> packages = const [],
    Iterable<LauncherIssue> issues = const [],
    this.resolution,
    Iterable<LaunchTargetChoice> targets = const [],
    Iterable<LaunchWorldChoice> worlds = const [],
    Iterable<String> transitions = const [],
    this.allowWorldOverride = false,
    this.allowTransitionOverride = false,
  }) : packages = List.unmodifiable(
         packages.map((item) => PackageIdentity.fromJson(item.toJson())),
       ),
       issues = List.unmodifiable(issues),
       targets = List.unmodifiable(targets),
       worlds = List.unmodifiable(worlds),
       transitions = List.unmodifiable(transitions);
  final String profileId;
  final int profileRevision;
  final String installIdentity;
  final String packageDigest;
  final LaunchSelection requestedSelection;
  final LaunchSelection effectiveSelection;
  final List<PackageIdentity> packages;
  final List<LauncherIssue> issues;
  final LaunchResolution? resolution;
  final List<LaunchTargetChoice> targets;
  final List<LaunchWorldChoice> worlds;
  final List<String> transitions;
  final bool allowWorldOverride;
  final bool allowTransitionOverride;
  List<LaunchBlock> get blocks => resolution?.blocks ?? const [];
  bool get canLaunch =>
      !issues.any((issue) => issue.isBlocking) &&
      switch (effectiveSelection.kind) {
        LaunchSelectionKind.mainMenu => true,
        LaunchSelectionKind.target => resolution?.resolved == true,
        LaunchSelectionKind.unresolvedLegacy => false,
      };
}

List<LaunchBlock> _previewBlocks(Iterable<LaunchBlock> blocks) =>
    List.unmodifiable(blocks.toSet().toList()..sort());
