part of 'topiaforge.dart';

extension _TopiaForgeDevPeers on _TopiaForgeCli {
  Future<int> _runDevPeers(
    int count,
    String projectRoot,
    String configuration,
    String packagePath,
    String? gameDir,
    CliLaunchOptions options,
    bool tail,
  ) async {
    if (acceptanceIsolation != null) {
      throw StateError(
        'Use the multiplayer acceptance suite inside an admitted QA layout.',
      );
    }
    final repoRoot = _findRepoRoot();
    if (repoRoot == null) {
      throw StateError(
        'Run dev --peers from a TopiaForge checkout with built runtime packages.',
      );
    }
    final discovery = LocalLauncherRepository(
      knownGamePath: gameDir,
      repositoryRoot: repoRoot,
    );
    final overlays = DevPeerOverlays(resolveTopiaForgeDataRoot());
    final sessions = <DevPeerSession>[];
    final handles = <DevPeerHandle>[];
    final stopped = Completer<void>();
    final cancellation = CancellationToken();
    String? runRoot;
    StreamSubscription<ProcessSignal>? signal;
    var cancelled = false;
    try {
      signal = ProcessSignal.sigint.watch().listen((_) {
        cancelled = true;
        cancellation.cancel();
        if (!stopped.isCompleted) stopped.complete();
      });
      final source = await discovery.detectKnownInstall();
      if (source == null) throw StateError(_noInstallRemedy);
      final sourceProfile = _launchProfile(
        await discovery.loadSnapshot(),
        options,
      );
      final manifest = await developerRepository.readModManifest(projectRoot);
      final random = Random.secure();
      final runId = List.generate(
        16,
        (_) => random.nextInt(256).toRadixString(16).padLeft(2, '0'),
      ).join();
      await _runDevStage(_DevStage.overlays, () async {
        final peers = await overlays.create(
          source.path,
          count,
          cancellation: cancellation,
        );
        runRoot = overlays.runRoot(peers.first);
        final packages = await _packAllMods(
          outputDir: p.join(runRoot!, 'packages'),
          configuration: configuration,
          cancellation: cancellation,
        );
        packages.add(packagePath);
        for (final peer in peers) {
          if (cancelled) return;
          final session = DevPeerSession(
            peer,
            runId,
            repoRoot,
            cancellation: cancellation,
          );
          sessions.add(session);
          handles.add(
            DevPeerHandle(
              index: peer.index,
              alive: session.isAlive,
              lines: session.recentLog,
              close: session.close,
            ),
          );
          await session.prepare(
            packages,
            sourceProfile,
            manifest.id,
            options.selectionOverride,
          );
        }
      });
      if (cancelled) return 130;
      LaunchSessionTarget? target;
      await _runDevStage(_DevStage.launch, () async {
        stdout.writeln(
          'TFDEV160: Starting peer 0 privately on an automatic local port.',
        );
        await sessions.first.launch(count);
        final deadline = DateTime.now().add(const Duration(minutes: 2));
        while (!cancelled && DateTime.now().isBefore(deadline)) {
          if (await sessions.first.isAlive() == false) {
            throw StateError(
              'The development host exited. Inspect its log and retry.',
            );
          }
          target = await sessions.first.hostTarget();
          if (target != null) return;
          await Future.any([
            stopped.future,
            Future<void>.delayed(const Duration(milliseconds: 200)),
          ]);
        }
        if (!cancelled) {
          throw StateError(
            'The development host did not confirm startup. Check its world and package selection.',
          );
        }
      });
      if (cancelled) return 130;
      await _runDevStage(_DevStage.joinPeers, () async {
        for (final peer in sessions.skip(1)) {
          if (cancelled) return;
          await peer.launch(count, target: target!);
        }
        final deadline = DateTime.now().add(const Duration(minutes: 2));
        while (!cancelled && DateTime.now().isBefore(deadline)) {
          var ready = true;
          for (final peer in sessions.skip(1)) {
            if (await peer.isAlive() == false) {
              continue; // The supervisor reports its tail; siblings keep running.
            }
            if (!await peer.isReady()) ready = false;
          }
          if (ready) return;
          await Future.any([
            stopped.future,
            Future<void>.delayed(const Duration(milliseconds: 200)),
          ]);
        }
        if (!cancelled) {
          stdout.writeln(
            'Some peers have not confirmed joining. Their logs remain live below.',
          );
        }
      });
      if (!cancelled) {
        await _runDevStage(_DevStage.tail, () async {
          stdout.writeln(
            'TFDEV170: Development peers are running. Press Ctrl+C to stop and clean up.',
          );
          await superviseDevPeers(
            handles,
            stopped.future,
            stdout.writeln,
            tail: tail,
          );
        });
      }
      return cancelled ? 130 : 0;
    } on _DevStageFailure {
      if (cancelled) return 130;
      rethrow;
    } finally {
      if (!stopped.isCompleted) stopped.complete();
      await signal?.cancel();
      final closed = await closeDevPeers(handles);
      await discovery.dispose();
      if (runRoot != null && closed) {
        overlays.cleanup(runRoot!);
      } else if (!closed) {
        throw _DevStageFailure(
          _DevStage.tail,
          StateError(
            'A peer could not be confirmed stopped. Its private dev-instances directory was retained; inspect it before retrying.',
          ),
        );
      }
    }
  }
}
