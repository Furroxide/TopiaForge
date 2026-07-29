part of '../local_developer_repository.dart';

const _multiplayerContractLockName = 'topiaforge.multiplayer.lock.json';
const _multiplayerContractMarker = '// TopiaForge.MultiplayerContractLock:v2:';
const _multiplayerContractSeparator = '\u001f';

final class _GeneratedMultiplayerContract {
  const _GeneratedMultiplayerContract({
    required this.id,
    required this.wireFormatRevision,
    required this.schemaSha256,
    required this.stateIds,
    required this.commandIds,
    required this.objectTypeIds,
    required this.eventIds,
  });

  final String id;
  final int wireFormatRevision;
  final String schemaSha256;
  final List<String> stateIds;
  final List<String> commandIds;
  final List<String> objectTypeIds;
  final List<String> eventIds;

  Map<String, Object?> toJson() => {
    'id': id,
    'wireFormatRevision': wireFormatRevision,
    'schemaSha256': schemaSha256,
    'stateIds': stateIds,
    'commandIds': commandIds,
    'objectTypeIds': objectTypeIds,
    'eventIds': eventIds,
  };
}

final class _TopiaForgeModBuild {
  const _TopiaForgeModBuild({
    required this.outputDirectory,
    required this.contracts,
  });

  final Directory outputDirectory;
  final List<_GeneratedMultiplayerContract> contracts;
}

extension LocalDeveloperMultiplayerContractLock on LocalDeveloperRepository {
  /// Rebuilds a multiplayer mod and atomically refreshes its generated contract
  /// lock. Authors never need to copy wire identifiers or schema digests.
  Future<String> synchronizeMultiplayerContractLock(
    String projectPath, {
    String configuration = 'Release',
  }) async {
    final root = Directory(projectPath).absolute;
    final manifest = _readContractLockManifest(root);
    if (!manifest.multiplayerIsPresent || manifest.multiplayer == null) {
      throw StateError(
        'The mod is standalone-only. Run `topiaforge mod add multiplayer` '
        'before synchronizing a multiplayer contract lock.',
      );
    }
    final projects = _entryProjectCandidates(root);
    if (projects.length > 1) {
      throw StateError(
        'Could not choose the entry C# project: found ${projects.length} '
        'projects in ${root.path}.',
      );
    }
    if (projects.isEmpty) {
      throw StateError(
        'Multiplayer contract synchronization requires one root C# project '
        'so TopiaForge can rebuild and verify generated contract descriptors. '
        'Source-less or precompiled-only multiplayer packages are not '
        'supported.',
      );
    }
    final contracts = (await _buildTopiaForgeMod(
      root,
      projects.single,
      configuration,
      emitContractMetadata: true,
    )).contracts;
    final lockFile = File(p.join(root.path, _multiplayerContractLockName));
    _writeDeveloperTextAtomic(
      lockFile,
      '${_prettyJson(_expectedMultiplayerContractLock(manifest, contracts))}\n',
    );
    return lockFile.path;
  }

  ModManifest _readContractLockManifest(Directory root) {
    final file = File(p.join(root.path, 'topiaforge.mod.json'));
    if (!file.existsSync()) {
      throw StateError('topiaforge.mod.json was not found in ${root.path}');
    }
    final decoded = jsonDecode(
      utf8.decode(
        _readDeveloperFileBoundedSync(
          file,
          maxBytes: _maxDeveloperManifestBytes,
          label: 'topiaforge.mod.json',
        ),
      ),
    );
    if (decoded is! Map<String, Object?>) {
      throw const FormatException('topiaforge.mod.json must be an object.');
    }
    final manifest = ModManifest.fromJson(decoded);
    final blocking = manifest.validate().where((issue) => issue.isBlocking);
    if (blocking.isNotEmpty) {
      throw StateError(
        'topiaforge.mod.json is invalid: '
        '${blocking.map((issue) => issue.message).join(' ')}',
      );
    }
    return manifest;
  }

  List<File> _entryProjectCandidates(Directory root) =>
      (root
          .listSync(followLinks: false)
          .whereType<File>()
          .where((file) => file.path.toLowerCase().endsWith('.csproj'))
          .toList()
        ..sort((left, right) => left.path.compareTo(right.path)));

  Future<_TopiaForgeModBuild> _buildTopiaForgeMod(
    Directory root,
    File csproj,
    String configuration, {
    required bool emitContractMetadata,
  }) async {
    final buildRoot = File(p.join(root.path, 'global.json')).existsSync()
        ? root
        : _repositoryRoot;
    final dotnet = await _dotnetSdkResolver(buildRoot);
    Directory? generatedDirectory;
    try {
      if (emitContractMetadata) {
        generatedDirectory = Directory.systemTemp.createTempSync(
          'topiaforge-multiplayer-contracts-',
        );
      }
      final arguments = <String>[
        'build',
        csproj.path,
        '-c',
        configuration,
        if (generatedDirectory != null) ...[
          '--no-incremental',
          '-p:EmitCompilerGeneratedFiles=true',
          '-p:CompilerGeneratedFilesOutputPath=${generatedDirectory.path}',
        ],
      ];
      final build = await runBoundedProcess(
        dotnet.executable,
        arguments,
        workingDirectory: buildRoot.path,
        runInShell:
            Platform.isWindows &&
            const {
              '.bat',
              '.cmd',
            }.contains(p.extension(dotnet.executable).toLowerCase()),
        timeout: const Duration(minutes: 10),
        maxStdoutBytes: 16 * 1024 * 1024,
        maxStderrBytes: 16 * 1024 * 1024,
      );
      if (build.exitCode != 0) {
        throw StateError('${build.stdout}\n${build.stderr}'.trim());
      }
      final output = _findModBuildOutput(root, configuration);
      final contracts = generatedDirectory == null
          ? const <_GeneratedMultiplayerContract>[]
          : _readGeneratedMultiplayerContracts(generatedDirectory);
      return _TopiaForgeModBuild(outputDirectory: output, contracts: contracts);
    } finally {
      if (generatedDirectory?.existsSync() ?? false) {
        generatedDirectory!.deleteSync(recursive: true);
      }
    }
  }

  Directory _findModBuildOutput(Directory root, String configuration) {
    final bin = Directory(p.join(root.path, 'bin', configuration));
    final candidates = bin.existsSync()
        ? (bin.listSync().whereType<Directory>().toList()
            ..sort((left, right) => left.path.compareTo(right.path)))
        : const <Directory>[];
    final output = candidates.firstOrNull;
    if (output == null) {
      throw StateError('Could not find build output under ${bin.path}');
    }
    return output;
  }

  List<_GeneratedMultiplayerContract> _readGeneratedMultiplayerContracts(
    Directory generatedDirectory,
  ) {
    final contracts = <_GeneratedMultiplayerContract>[];
    final files =
        generatedDirectory
            .listSync(recursive: true, followLinks: false)
            .whereType<File>()
            .where((file) => file.path.endsWith('.cs'))
            .toList()
          ..sort((left, right) => left.path.compareTo(right.path));
    if (files.length > 8192) {
      throw StateError('Generated source output exceeded the 8192-file limit.');
    }
    for (final file in files) {
      final handle = file.openSync();
      List<int> prefix;
      try {
        prefix = handle.readSync(1024 * 1024);
      } finally {
        handle.closeSync();
      }
      final newline = prefix.indexOf(0x0a);
      final line = utf8
          .decode(
            newline < 0 ? prefix : prefix.sublist(0, newline),
            allowMalformed: false,
          )
          .replaceFirst('\ufeff', '');
      if (!line.startsWith(_multiplayerContractMarker)) continue;
      contracts.add(
        _decodeGeneratedMultiplayerContract(
          line.substring(_multiplayerContractMarker.length).trim(),
        ),
      );
    }
    contracts.sort((left, right) => left.id.compareTo(right.id));
    final ids = <String>{};
    for (final contract in contracts) {
      if (!ids.add(contract.id)) {
        throw StateError(
          'Generated multiplayer contract id ${contract.id} is duplicated.',
        );
      }
    }
    return List.unmodifiable(contracts);
  }

  _GeneratedMultiplayerContract _decodeGeneratedMultiplayerContract(
    String encoded,
  ) {
    try {
      final fields = utf8.decode(base64.decode(encoded)).split('\n');
      final wireFormatRevision = fields.length < 2
          ? null
          : int.tryParse(fields[1]);
      if (fields.length != 7 ||
          fields[0].isEmpty ||
          wireFormatRevision == null ||
          wireFormatRevision < 1 ||
          wireFormatRevision > 0x7fffffff ||
          !RegExp(r'^[0-9a-f]{64}$').hasMatch(fields[2])) {
        throw const FormatException('invalid generated descriptor fields');
      }
      List<String> ids(String value) => value.isEmpty
          ? const <String>[]
          : (value.split(_multiplayerContractSeparator)..sort());
      return _GeneratedMultiplayerContract(
        id: fields[0],
        wireFormatRevision: wireFormatRevision,
        schemaSha256: fields[2],
        stateIds: ids(fields[3]),
        commandIds: ids(fields[4]),
        objectTypeIds: ids(fields[5]),
        eventIds: ids(fields[6]),
      );
    } on Object catch (error) {
      throw StateError(
        'The multiplayer generator emitted invalid contract metadata: $error',
      );
    }
  }

  Map<String, Object?> _expectedMultiplayerContractLock(
    ModManifest manifest,
    List<_GeneratedMultiplayerContract> contracts,
  ) => {
    'schemaVersion': 2,
    'protocolVersion': ?manifest.multiplayer?.protocol?.version,
    'contracts': contracts.map((contract) => contract.toJson()).toList(),
  };

  void _validateMultiplayerContractLock(
    Directory root,
    ModManifest manifest,
    List<_GeneratedMultiplayerContract> contracts,
  ) {
    if (!manifest.multiplayerIsPresent || manifest.multiplayer == null) return;
    final lockFile = File(p.join(root.path, _multiplayerContractLockName));
    const remedy =
        'Run `topiaforge mod sync multiplayer --project <path>` and commit '
        'the refreshed lock.';
    if (!lockFile.existsSync()) {
      throw StateError('$_multiplayerContractLockName is missing. $remedy');
    }
    Object? actual;
    try {
      actual = jsonDecode(
        utf8.decode(
          _readDeveloperFileBoundedSync(
            lockFile,
            maxBytes: _maxDeveloperManifestBytes,
            label: _multiplayerContractLockName,
          ),
        ),
      );
    } on Object catch (error) {
      throw StateError(
        '$_multiplayerContractLockName is malformed or tampered: $error. '
        '$remedy',
      );
    }
    final expected = _expectedMultiplayerContractLock(manifest, contracts);
    if (jsonEncode(actual) != jsonEncode(expected)) {
      throw StateError(
        '$_multiplayerContractLockName is stale or tampered and does not '
        'match generated multiplayer contracts. $remedy',
      );
    }
  }
}
