part of 'topiaforge.dart';

extension _MapCommands on _TopiaForgeCli {
  Future<int> _worldAddMap(List<String> args) async {
    if (args.length < 2 || args.first != 'map' || args[1].startsWith('--')) {
      throw UsageError(
        'Usage: topiaforge world add map <link-or-id> [--project dir] [--scene id] [--name text] '
        '[--world id] [--target id] [--spawn name] [--offline]',
      );
    }
    final reference = MapReference.parse(args[1]);
    final root = p.absolute(
      _option(args, '--project') ?? Directory.current.path,
    );
    final file = File(p.join(root, 'topiaforge.mod.json'));
    ordinaryMapPath(file.path);
    final original = readBoundedTextFileSync(
      file,
      maxBytes: CliFileLimits.manifest,
    );
    rejectDuplicateJsonProperties(original, label: 'Manifest');
    final source = jsonDecode(original) as Map<String, dynamic>;
    final offline = args.contains('--offline');
    final chosenScene = _option(args, '--scene');
    final chosenName = _option(args, '--name');
    if (offline && (chosenScene == null || chosenName == null)) {
      throw UsageError(
        '--offline needs both --scene and --name. No map is downloaded.',
      );
    }
    final survey = offline ? null : await MapProjectReader().read(reference);
    final scene = survey?.document.select(chosenScene);
    final marker = _option(args, '--spawn');
    scene?.requireSpawn(marker);
    final name = chosenName ?? survey!.document.name;
    final output = addMapWorld(
      source,
      reference: reference,
      scene: scene?.id ?? chosenScene!,
      name: name,
      worldId: _option(args, '--world'),
      targetId: _option(args, '--target'),
      markerName: marker,
    );
    if (survey != null) {
      final snapshot = File(p.join(root, 'obj', 'topiaforge-map-survey.json'));
      ordinaryMapPath(snapshot.path);
      await snapshot.parent.create(recursive: true);
      await snapshot.writeAsString(
        '${const JsonEncoder.withIndent('  ').convert(survey.snapshot(reference, scene!))}\n',
      );
    }
    await writeMapManifest(file, original, output);
    stdout.writeln(
      'Added map world "$name" with a fixed launch target and the Worlds dependency. '
      'Manifest schema 6 is retained; loader and SDK ranges now require at least 0.2.0.',
    );
    stdout.writeln(
      offline
          ? 'Scene and spawn still need verification: topiaforge check world ${reference.id} --scene $chosenScene.'
          : 'Survey saved under obj/. The published map itself is downloaded only when playing.',
    );
    return 0;
  }

  Future<int> _checkWorld(List<String> args) async {
    if (args.isEmpty || args.first.startsWith('--')) {
      throw UsageError(
        'Usage: topiaforge check world <link-or-id> [--scene id] [--spawn name]',
      );
    }
    final reference = MapReference.parse(args.first);
    final survey = await MapProjectReader().read(reference);
    final scene = survey.document.select(_option(args, '--scene'));
    scene.requireSpawn(_option(args, '--spawn'));
    stdout.writeln(
      'Map ${reference.id}: scene ${scene.id}, ${scene.entities.length} entities, '
      '${scene.spawns.length} spawn locations; ${survey.packedSize} bytes; SHA-256 ${survey.sha256}.',
    );
    return 0;
  }
}
