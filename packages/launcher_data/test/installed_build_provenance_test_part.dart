part of 'launcher_data_test.dart';

void _registerInstalledBuildProvenanceTests({
  required LocalLauncherRepository Function() repository,
  required Directory Function() root,
  required Directory Function() gameRoot,
}) {
  test('reads canonical game build provenance independently', () async {
    final metadata = File(p.join(gameRoot().path, 'installed-build.json'));
    metadata.writeAsStringSync('{"id":"2227"}');

    final install = await repository().selectGameDirectory(gameRoot().path);

    expect(install.gameVersion, '0.0.2227');
    expect(install.gameVersionLabel, 'build 2227');

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
    ).writeAsStringSync('{"id":2227}');

    final install = await repository().selectGameDirectory(game.path);

    expect(install.gameVersion, '0.0.2227');
    expect(install.gameVersionLabel, 'build 2227');

    File(
      p.join(game.path, 'installed-build.json'),
    ).writeAsStringSync('{"id":2228}');
    final rootMarker = await repository().selectGameDirectory(game.path);
    expect(rootMarker.gameVersion, '0.0.2228');
  });

  test('package install enforces the current canonical game build', () async {
    final metadata = File(p.join(gameRoot().path, 'installed-build.json'));
    metadata.writeAsStringSync('{"id":2227}');
    final install = await repository().selectGameDirectory(gameRoot().path);
    final package = _createPackage(
      root(),
      id: 'build.bound.mod',
      version: '1.0.0',
      gameVersionRange: '0.0.2227',
    );

    final compatible = await repository().previewPackage(package.path, install);
    expect(compatible.hasBlockingIssues, isFalse);

    metadata.writeAsStringSync('{"id":2228}');
    final incompatible = await repository().previewPackage(
      package.path,
      install,
    );
    expect(incompatible.hasBlockingIssues, isTrue);
    expect(
      incompatible.issues.map((issue) => issue.message).join(' '),
      contains('supports Robotopia build 2227; installed: build 2228'),
    );
    await expectLater(
      repository().installPackage(package.path, install),
      throwsA(
        predicate(
          (error) => error.toString().contains('installed: build 2228'),
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
