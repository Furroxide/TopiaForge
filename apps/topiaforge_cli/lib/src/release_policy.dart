import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;

import 'spdx_expression.dart';
import 'release_game_build_policy.dart';
import 'bounded_file_reader.dart';

part 'release_policy_io.dart';
part 'release_policy_models.dart';

class ReleasePolicyValidator {
  const ReleasePolicyValidator();

  Future<List<String>> validate({
    required TopiaForgeReleasePolicy policy,
    required TopiaForgeReleaseCatalogEntry release,
    bool allowUnresolvedPolicy = false,
    bool verifyArchiveHashes = true,
  }) async {
    final root = policy.repositoryRoot;
    final issues = <String>[];
    _validateCatalog(policy, release, root, issues, allowUnresolvedPolicy);
    _validateToolchains(policy, release, root, issues, allowUnresolvedPolicy);
    _validateGameBuild(policy, root, issues);
    _validateComponents(release, root, issues);
    _validateMods(release, root, issues);
    _validateLicense(policy, release, root, issues, allowUnresolvedPolicy);
    await _validateReleaseProvenance(
      policy,
      root,
      issues,
      verifyArchiveHashes: verifyArchiveHashes,
    );
    return issues;
  }

  void _validateCatalog(
    TopiaForgeReleasePolicy policy,
    TopiaForgeReleaseCatalogEntry release,
    String root,
    List<String> issues,
    bool allowUnresolvedPolicy,
  ) {
    final versionMatch = RegExp(
      r'^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?$',
    ).firstMatch(release.version);
    if (versionMatch == null) {
      issues.add(
        'Catalog version ${release.version} is not a supported semantic version.',
      );
    }
    final versionIsPrerelease =
        versionMatch != null && release.version.contains('-');
    if (release.prerelease != versionIsPrerelease) {
      issues.add(
        'Catalog prerelease ${release.prerelease} does not match version ${release.version}.',
      );
    }
    if (release.tag != 'v${release.version}') {
      issues.add('Catalog tag ${release.tag} must equal v${release.version}.');
    }
    if (!allowUnresolvedPolicy && release.status != 'ready') {
      issues.add(
        'Release ${release.version} is ${release.status}; set its manually reviewed catalog status to ready.',
      );
    }
    final notes = File(p.join(root, release.notesFile));
    if (!notes.existsSync() || notes.lengthSync() == 0) {
      issues.add('Release notes are missing or empty: ${release.notesFile}.');
    }
    final expected = <String>{
      ...policy.platformArchives,
      for (final entry in release.mods.entries)
        '${entry.key}-${entry.value}.topiaforgemod',
    };
    if (!_sameSet(expected, release.artifacts.toSet())) {
      issues.add(
        'Catalog artifacts do not exactly match platform and mod versions.',
      );
    }
  }

  void _validateToolchains(
    TopiaForgeReleasePolicy policy,
    TopiaForgeReleaseCatalogEntry release,
    String root,
    List<String> issues,
    bool allowUnresolvedPolicy,
  ) {
    const expected = {
      'dotnetSdk': '10.0.301',
      'dotnetRuntime': '10.0.9',
      'dart': '3.12.2',
      'flutter': '3.44.6',
      'node': '24.18.0',
      'unity': '6000.0.23f1',
    };
    if (!_sameMap(expected, policy.toolchains)) {
      issues.add('release-policy.json toolchains do not match required pins.');
    }
    if (policy.productVersion != release.version ||
        policy.rollback != 'none-initial-release' ||
        policy.bepInExVersion != release.components['bepInEx'] ||
        policy.unityDoorstopCommit !=
            '33dab9a6733862eb81869ff08431d9478b28784b') {
      issues.add(
        'Release versioning or bundled-runtime policy is inconsistent.',
      );
    }
    if (release.version == '1.0.0-rc.1') {
      if (!release.prerelease ||
          !policy.targetsWindows ||
          policy.targetsMacOS) {
        issues.add(
          'Release 1.0.0-rc.1 must target only signed Windows x64 packages.',
        );
      }
    }
    final windowsIdentityIsValid =
        policy.windowsCertificateSha256.isEmpty ||
        RegExp(r'^[0-9a-f]{64}$').hasMatch(policy.windowsCertificateSha256);
    final macIdentityIsValid =
        policy.macosTeamId.isEmpty ||
        RegExp(r'^[A-Z0-9]{10}$').hasMatch(policy.macosTeamId);
    if (!windowsIdentityIsValid || !macIdentityIsValid) {
      issues.add(
        'Configured signing identities must be a lowercase Windows '
        'certificate SHA-256 or an uppercase 10-character Apple Team ID.',
      );
    }
    if (!allowUnresolvedPolicy &&
        policy.requiresWindowsSigningIdentity &&
        !policy.hasConfiguredWindowsSigningIdentity) {
      issues.add(
        'A configured Windows signing identity is required for this release.',
      );
    }
    if (!allowUnresolvedPolicy &&
        policy.requiresMacOSSigningIdentity &&
        !policy.hasConfiguredMacOSSigningIdentity) {
      issues.add(
        'A configured macOS signing identity is required for this release.',
      );
    }
    // Linux is descoped from 1.0.0-rc.1 and returns in rc.2. WSLg cannot reach
    // a GPU Vulkan implementation, and Robotopia's D3D12 renderer needs it
    // through VKD3D, so no credible Proton acceptance evidence can be produced
    // on the administrator host. See P0-LINUX-01 in docs/LaunchBlockers.md.
    const rc1PlatformArchives = {'TopiaForge-windows-x64.zip'};
    final hasSupportedPlatforms = policy.platformArchives.every(
      releasePlatformArchives.containsValue,
    );
    if (!hasSupportedPlatforms ||
        policy.platformArchives.length != policy.targetPlatforms.length ||
        (release.version == '1.0.0-rc.1' &&
            !_sameSet(policy.platformArchives.toSet(), rc1PlatformArchives)) ||
        !_sameSet(policy.generatedMetadata.toSet(), {
          'release-bom.json',
          'release-sbom.spdx.json',
          'topiaforge-update-v1.json',
          'topiaforge-update-v1.json.sig',
          'SHA256SUMS',
        })) {
      issues.add('Release artifact policy is incomplete.');
    }
    final global = _readObject(File(p.join(root, 'global.json')));
    final sdk = _object(global['sdk'], 'global.json sdk');
    if (sdk['version'] != policy.toolchains['dotnetSdk'] ||
        sdk['rollForward'] != 'disable' ||
        sdk['allowPrerelease'] != false) {
      issues.add('global.json does not enforce the approved .NET SDK patch.');
    }
    final props = readBoundedTextFileSync(
      File(p.join(root, 'Directory.Build.props')),
      maxBytes: CliFileLimits.metadata,
    );
    if (!props.contains(
      '<TopiaForgeDotNetRuntimeVersion>${policy.toolchains['dotnetRuntime']}</TopiaForgeDotNetRuntimeVersion>',
    )) {
      issues.add('Directory.Build.props does not contain the runtime pin.');
    }
    final unityVersion = readBoundedTextFileSync(
      File(
        p.join(
          root,
          'tools',
          'unity-ui-bundle',
          'ProjectSettings',
          'ProjectVersion.txt',
        ),
      ),
      maxBytes: CliFileLimits.session,
    );
    if (!unityVersion.contains(policy.toolchains['unity']!)) {
      issues.add('Unity tooling does not use ${policy.toolchains['unity']}.');
    }
  }

  void _validateGameBuild(
    TopiaForgeReleasePolicy policy,
    String root,
    List<String> issues,
  ) {
    final metadata = _readObject(
      File(p.join(root, policy.gameBuildMetadataFile)),
    );
    final baseline = _readObject(
      File(p.join(root, 'baselines', 'gamecode.surface.baseline.json')),
    );
    issues.addAll(
      validateRobotopiaGameBuildMetadata(
        metadata: metadata,
        policyBuildId: policy.gameBuildId,
        requireLatestAtRelease: policy.requireLatestGameBuild,
        baseline: baseline,
      ),
    );
  }

  void _validateComponents(
    TopiaForgeReleaseCatalogEntry release,
    String root,
    List<String> issues,
  ) {
    final pubspecs = {
      'cli': 'apps/topiaforge_cli/pubspec.yaml',
      'launcher': 'apps/topiaforge_launcher_flutter/pubspec.yaml',
      'launcherDomain': 'packages/launcher_domain/pubspec.yaml',
      'launcherData': 'packages/launcher_data/pubspec.yaml',
      'launcherUi': 'packages/launcher_ui/pubspec.yaml',
    };
    for (final entry in pubspecs.entries) {
      _expectYamlVersion(
        root,
        entry.value,
        release.components[entry.key],
        issues,
      );
    }
    _expectJsonVersion(
      root,
      'tools/ugc-automerge-sidecar/package.json',
      release.components['sidecar'],
      issues,
    );
    final vpmPaths = {
      'io.github.furroxide.topiaforge.vpm-resolver':
          'templates/TopiaForge.UnityWorldTemplate/Packages/io.github.furroxide.topiaforge.vpm-resolver/package.json',
      'io.github.furroxide.topiaforge.world-companion':
          'templates/TopiaForge.UnityWorldTemplate/Packages/io.github.furroxide.topiaforge.world-companion/package.json',
      'io.github.furroxide.topiaforge.ugc-companion':
          'templates/unity-companion/Packages/io.github.furroxide.topiaforge.ugc-companion/package.json',
    };
    for (final entry in vpmPaths.entries) {
      _expectJsonVersion(
        root,
        entry.value,
        release.vpmPackages[entry.key],
        issues,
      );
    }
    final assemblies = {
      'src/TopiaForge.ModManager/TopiaForge.ModManager.csproj':
          release.components['loader'],
      'src/TopiaForge.ModManager.Core/TopiaForge.ModManager.Core.csproj':
          release.components['loaderCore'],
      'src/TopiaForge.Mods.UnityUi/TopiaForge.Mods.UnityUi.csproj':
          release.components['unityUi'],
      'src/TopiaForge.GameCompat.Extractor/TopiaForge.GameCompat.Extractor.csproj':
          release.components['gameCompatExtractor'],
      'src/TopiaForge.GameCompat.Surface/TopiaForge.GameCompat.Surface.csproj':
          release.components['gameCompatSurface'],
    };
    for (final entry in assemblies.entries) {
      _expectCsprojVersion(
        root,
        entry.key,
        entry.value,
        issues,
        expectedAssemblyVersion:
            entry.key ==
                'src/TopiaForge.Mods.UnityUi/TopiaForge.Mods.UnityUi.csproj'
            ? _stableMajorAssemblyVersion(entry.value)
            : null,
      );
    }
    for (final packageId in topiaForgeSdkPackageIds) {
      _expectSdkCsprojVersion(
        root,
        'src/$packageId/$packageId.csproj',
        packageId,
        release.components['sdk'],
        issues,
      );
    }
    final versionSource = readBoundedTextFileSync(
      File(
        p.join(root, 'src/TopiaForge.ModManager.Core/TopiaForgeVersions.cs'),
      ),
      maxBytes: CliFileLimits.metadata,
    );
    for (final entry in {
      'LoaderVersion': release.components['loader'],
      'SdkVersion': release.components['sdk'],
    }.entries) {
      if (!versionSource.contains(
        'const string ${entry.key} = "${entry.value}"',
      )) {
        issues.add('${entry.key} does not match the release catalog.');
      }
    }
  }

  void _validateLicense(
    TopiaForgeReleasePolicy policy,
    TopiaForgeReleaseCatalogEntry release,
    String root,
    List<String> issues,
    bool allowUnresolvedPolicy,
  ) {
    if (!policy.hasApprovedLicense) {
      if (!allowUnresolvedPolicy) {
        issues.add('Project license policy requires owner/legal approval.');
      }
      return;
    }
    final licensePath = p.join(root, policy.licenseFile!);
    final type = FileSystemEntity.typeSync(licensePath, followLinks: false);
    if (type != FileSystemEntityType.file) {
      issues.add(
        'Approved project license must be a real file: ${policy.licenseFile}.',
      );
      return;
    }
    final license = File(licensePath);
    if (license.lengthSync() == 0 || license.lengthSync() > 1024 * 1024) {
      issues.add('Approved project license file is empty or oversized.');
    }
    for (final id in {
      ...release.mods.keys,
      ...release.excludedDeveloperMods.keys,
    }) {
      final manifest = _findManifest(root, id);
      if (manifest == null || manifest['license'] != policy.licenseExpression) {
        issues.add('$id license does not match the approved project policy.');
        continue;
      }
      final files = manifest['licenseFiles'];
      if (files is! List ||
          files.isEmpty ||
          !files.whereType<String>().every(isSafePackageRelativePath)) {
        issues.add('$id must declare safe package licenseFiles.');
      }
    }
  }
}
