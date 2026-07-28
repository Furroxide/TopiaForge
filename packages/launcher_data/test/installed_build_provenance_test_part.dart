part of 'launcher_data_test.dart';

void _registerInstalledBuildProvenanceTests({
  required LocalLauncherRepository Function() repository,
  required Directory Function() root,
  required Directory Function() gameRoot,
}) {
  final currentVersion = TopiaForgeRuntimeVersions.gameVersion;
  final currentBuildId = RobotopiaGameVersion.tryBuildId(currentVersion)!;
  final currentBuildLabel = RobotopiaGameVersion.tryBuildLabel(currentVersion)!;
  final nextBuildId = currentBuildId + 1;
  final nextVersion = RobotopiaGameVersion.tryFromBuildId(nextBuildId)!;
  final nextBuildLabel = RobotopiaGameVersion.tryBuildLabel(nextVersion)!;

  test('reads canonical game build provenance independently', () async {
    final metadata = File(p.join(gameRoot().path, 'installed-build.json'));
    metadata.writeAsStringSync('{"id":"$currentBuildId"}');

    final install = await repository().selectGameDirectory(gameRoot().path);

    expect(install.gameVersion, currentVersion);
    expect(install.gameVersionLabel, currentBuildLabel);

    metadata.writeAsStringSync('{"id":0}');
    final invalid = await repository().selectGameDirectory(gameRoot().path);
    expect(invalid.gameVersion, isNull);
    expect(invalid.gameVersionLabel, isEmpty);
    expect(
      invalid.issues.map((issue) => issue.message).join(' '),
      contains('build metadata is invalid or unreadable'),
    );
  });

  test('reads Tomato Cake build provenance beside the game folder', () async {
    final launcher = Directory(p.join(root().path, 'tomato-launcher'))
      ..createSync();
    final game = Directory(p.join(launcher.path, 'Robotopia'))..createSync();
    _createGame(game);
    File(
      p.join(launcher.path, 'installed-build.json'),
    ).writeAsStringSync('{"id":$currentBuildId}');

    final install = await repository().selectGameDirectory(game.path);

    expect(install.gameVersion, currentVersion);
    expect(install.gameVersionLabel, currentBuildLabel);

    File(
      p.join(game.path, 'installed-build.json'),
    ).writeAsStringSync('{"id":$nextBuildId}');
    final rootMarker = await repository().selectGameDirectory(game.path);
    expect(rootMarker.gameVersion, nextVersion);
  });

  test('package install enforces the current canonical game build', () async {
    final metadata = File(p.join(gameRoot().path, 'installed-build.json'));
    metadata.writeAsStringSync('{"id":$currentBuildId}');
    final install = await repository().selectGameDirectory(gameRoot().path);
    final package = _createPackage(
      root(),
      id: 'build.bound.mod',
      version: '1.0.0',
      gameVersionRange: currentVersion,
    );

    final compatible = await repository().previewPackage(package.path, install);
    expect(compatible.hasBlockingIssues, isFalse);

    metadata.writeAsStringSync('{"id":$nextBuildId}');
    final incompatible = await repository().previewPackage(
      package.path,
      install,
    );
    expect(incompatible.hasBlockingIssues, isTrue);
    expect(
      incompatible.issues.map((issue) => issue.message).join(' '),
      contains(
        'supports Robotopia $currentBuildLabel; installed: $nextBuildLabel',
      ),
    );
    await expectLater(
      repository().installPackage(package.path, install),
      throwsA(
        predicate(
          (error) => error.toString().contains('installed: $nextBuildLabel'),
        ),
      ),
    );

    metadata.deleteSync();
    final unknown = await repository().previewPackage(package.path, install);
    expect(unknown.hasBlockingIssues, isTrue);
    expect(
      unknown.issues.map((issue) => issue.message).join(' '),
      contains('could not verify the installed build'),
    );
  });
}
