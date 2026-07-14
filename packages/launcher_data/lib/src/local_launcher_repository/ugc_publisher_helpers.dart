part of '../local_launcher_repository.dart';

class UgcPublisherCommand {
  UgcPublisherCommand({
    required this.executable,
    required List<String> arguments,
  }) : arguments = List.unmodifiable(arguments);

  factory UgcPublisherCommand.forSettings({
    String nodeExecutable = 'node',
    required String sidecarPath,
    required String sessionFilePath,
    required UgcLiveSyncSettings settings,
  }) {
    return UgcPublisherCommand(
      executable: nodeExecutable,
      arguments: [
        sidecarPath,
        '--watch',
        settings.watchFolder,
        '--sync',
        settings.syncServerUrl,
        '--session-file',
        sessionFilePath,
        if (settings.documentUrl.isNotEmpty) ...['--doc', settings.documentUrl],
        if (settings.sceneId.isNotEmpty) ...['--scene', settings.sceneId],
      ],
    );
  }

  final String executable;
  final List<String> arguments;
}

extension _UgcPublisherHelpers on LocalLauncherRepository {
  Future<UgcPublisherStartResult> _startUgcPublisher(
    UgcLiveSyncSettings settings,
  ) async {
    if (_disposed) {
      return const UgcPublisherStartResult(
        started: false,
        message: 'The launcher repository has been disposed.',
      );
    }
    if (_ugcPublisher != null) {
      return UgcPublisherStartResult(
        started: false,
        message: 'The Automerge publisher is already running.',
        sessionId: _ugcPublisherSessionId,
      );
    }

    try {
      final sidecar = _findUgcSidecar();
      final toolchain = await sidecar.ensureDependencies();
      await _prepareUgcSessionPath();
      final command = UgcPublisherCommand.forSettings(
        nodeExecutable: toolchain.nodeExecutable,
        sidecarPath: sidecar.script.path,
        sessionFilePath: _ugcPublisherSessionFile.path,
        settings: settings,
      );
      await Directory(settings.watchFolder).create(recursive: true);
      if (FileSystemEntity.typeSync(settings.watchFolder, followLinks: false) !=
          FileSystemEntityType.directory) {
        throw StateError('The UGC watch folder must be a regular directory.');
      }
      final process = await Process.start(
        command.executable,
        command.arguments,
        workingDirectory: sidecar.directory.path,
        runInShell: false,
      ).timeout(const Duration(seconds: 10));
      final sessionId = ++_ugcPublisherSessionId;
      _ugcPublisher = process;
      _ugcPublisherStopping = false;
      _ugcPublisherStdout = _boundedPublisherOutput(process.stdout).listen(
        (line) => _ugcPublisherEvents.add(UgcPublisherOutput(sessionId, line)),
      );
      _ugcPublisherStderr =
          _boundedPublisherOutput(process.stderr, prefix: '! ').listen(
            (line) =>
                _ugcPublisherEvents.add(UgcPublisherOutput(sessionId, line)),
          );
      unawaited(
        process.exitCode.then(
          (exitCode) => _handleUgcPublisherExit(process, sessionId, exitCode),
        ),
      );
      return UgcPublisherStartResult(
        started: true,
        message: 'Automerge publisher started.',
        sessionId: sessionId,
      );
    } on ProcessException catch (error) {
      return UgcPublisherStartResult(
        started: false,
        message:
            'Could not start Node (${error.message}). Install Node 20+ to publish via Automerge.',
      );
    } on FileSystemException catch (error) {
      return UgcPublisherStartResult(
        started: false,
        message: 'Could not prepare the watch folder (${error.message}).',
      );
    } on Object catch (error) {
      return UgcPublisherStartResult(
        started: false,
        message: 'Could not prepare the Automerge publisher: $error',
      );
    }
  }

  Future<void> _stopUgcPublisher({required bool waitForExit}) async {
    final process = _ugcPublisher;
    if (process == null) {
      return;
    }

    _ugcPublisherStopping = true;
    try {
      final exitCode = process.exitCode;
      process.kill();
      try {
        await exitCode.timeout(
          waitForExit ? const Duration(seconds: 3) : const Duration(seconds: 1),
        );
      } on TimeoutException {
        process.kill(ProcessSignal.sigkill);
        try {
          await exitCode.timeout(const Duration(seconds: 2));
        } on TimeoutException {
          throw StateError('The Automerge publisher could not be stopped.');
        }
      }
    } on Object {
      _ugcPublisherStopping = false;
      rethrow;
    }

    if (identical(_ugcPublisher, process)) {
      _ugcPublisher = null;
    }
    await _cancelUgcPublisherOutput();
    _ugcPublisherStopping = false;
  }

  Future<void> _handleUgcPublisherExit(
    Process process,
    int sessionId,
    int exitCode,
  ) async {
    if (!identical(_ugcPublisher, process)) {
      return;
    }
    final intentional = _ugcPublisherStopping;
    _ugcPublisher = null;
    await _cancelUgcPublisherOutput();
    if (!intentional) {
      _ugcPublisherEvents.add(UgcPublisherExited(sessionId, exitCode));
    }
  }

  Future<void> _cancelUgcPublisherOutput() async {
    final stdout = _ugcPublisherStdout;
    final stderr = _ugcPublisherStderr;
    _ugcPublisherStdout = null;
    _ugcPublisherStderr = null;
    await stdout?.cancel();
    await stderr?.cancel();
  }

  TrustedUgcSidecar _findUgcSidecar() {
    return TrustedUgcSidecar.inspectRepository(_repositoryRoot);
  }

  Future<void> _prepareUgcSessionPath() async {
    _dataRoot.createSync(recursive: true);
    if (FileSystemEntity.typeSync(_dataRoot.path, followLinks: false) !=
        FileSystemEntityType.directory) {
      throw StateError('Launcher data root must be a regular directory.');
    }
    final sessionType = FileSystemEntity.typeSync(
      _ugcPublisherSessionFile.path,
      followLinks: false,
    );
    if (sessionType != FileSystemEntityType.notFound &&
        sessionType != FileSystemEntityType.file) {
      throw StateError(
        'UGC session path must be a regular file and not a link.',
      );
    }
    if (!Platform.isWindows) {
      for (final permission in [
        (_dataRoot.path, '700'),
        if (sessionType == FileSystemEntityType.file)
          (_ugcPublisherSessionFile.path, '600'),
      ]) {
        final result = await runBoundedProcess(
          '/bin/chmod',
          [permission.$2, permission.$1],
          runInShell: false,
          timeout: const Duration(seconds: 5),
          maxStdoutBytes: 4096,
          maxStderrBytes: 4096,
        );
        if (result.exitCode != 0) {
          throw StateError('Could not restrict UGC session permissions.');
        }
      }
    }
  }
}

Stream<String> _boundedPublisherOutput(
  Stream<List<int>> source, {
  String prefix = '',
}) => source
    .transform(utf8.decoder)
    .transform(_BoundedCoalescedLineTransformer(prefix));

final class _BoundedCoalescedLineTransformer
    extends StreamTransformerBase<String, String> {
  const _BoundedCoalescedLineTransformer(this.prefix);

  final String prefix;

  @override
  Stream<String> bind(Stream<String> stream) {
    late StreamController<String> controller;
    StreamSubscription<String>? subscription;
    var line = StringBuffer();
    var truncated = false;

    void emitLine(List<String> batch) {
      var text = line.toString();
      line = StringBuffer();
      if (text.endsWith('\r')) text = text.substring(0, text.length - 1);
      if (truncated) text = '$text … [publisher output truncated]';
      truncated = false;
      final output = '$prefix$text';
      if (prefix.isEmpty && output.startsWith('TOPIAFORGE_UGC_SESSION ')) {
        if (batch.isNotEmpty) {
          controller.add(batch.join('\n'));
          batch.clear();
        }
        controller.add(output);
      } else {
        batch.add(output);
      }
    }

    controller = StreamController<String>(
      sync: true,
      onListen: () {
        subscription = stream.listen(
          (chunk) {
            final batch = <String>[];
            for (var index = 0; index < chunk.length; index++) {
              final char = chunk[index];
              if (char == '\n') {
                emitLine(batch);
              } else if (line.length < _maxPublisherLineCharacters) {
                line.write(char);
              } else {
                truncated = true;
              }
            }
            if (batch.isNotEmpty) controller.add(batch.join('\n'));
          },
          onError: controller.addError,
          onDone: () {
            if (line.length > 0 || truncated) {
              final batch = <String>[];
              emitLine(batch);
              if (batch.isNotEmpty) controller.add(batch.join('\n'));
            }
            controller.close();
          },
        );
      },
      onPause: () => subscription?.pause(),
      onResume: () => subscription?.resume(),
      onCancel: () => subscription?.cancel(),
    );
    return controller.stream;
  }
}

const _maxPublisherLineCharacters = 16 * 1024;
