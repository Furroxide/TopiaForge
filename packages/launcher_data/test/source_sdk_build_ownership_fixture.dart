import 'dart:async';
import 'dart:io';
import 'dart:isolate';

import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:launcher_data/src/source_sdk_build_lock.dart';

Future<void> firstBuilder(List<Object> args) async {
  final repo = args[0] as String;
  final root = args[1] as String;
  final events = args[2] as SendPort;
  final control = ReceivePort();
  events.send(control.sendPort);
  final repository = LocalDeveloperRepository(
    repositoryRoot: repo,
    dataRoot: p.join(root, 'data-first'),
    dotnetSdkResolver: (_) async {
      events.send('first admitted');
      await control.first;
      writeOutputs(Directory(repo));
      throw StateError('intentional first build failure');
    },
  );
  try {
    await repository.createModProject(
      parentDirectory: p.join(root, 'first'),
      id: 'test.first',
      name: 'First',
    );
    events.send('unexpected success');
  } on StateError catch (error) {
    events.send(
      error.message == 'intentional first build failure'
          ? 'first stopped'
          : error.toString(),
    );
  } finally {
    control.close();
  }
}

Future<String> secondBuilder(
  String repo,
  String root, [
  SendPort? observer,
]) async {
  final repository = LocalDeveloperRepository(
    repositoryRoot: repo,
    dataRoot: p.join(root, 'data-second'),
    dotnetSdkResolver: (_) async {
      File(
        p.join(root, 'overlap'),
      ).writeAsStringSync('duplicate missing build');
      throw StateError('overlapping SDK build admitted');
    },
  );
  try {
    await runZoned(
      () => repository.createModProject(
        parentDirectory: p.join(root, 'second'),
        id: 'test.second',
        name: 'Second',
      ),
      zoneValues: {sourceSdkOwnershipObserver: observer},
    );
    return 'second scaffolded';
  } catch (error) {
    return error.toString();
  }
}

Future<void> main(List<String> args) async {
  if (args[0] == '--failed-child') {
    stdout.write('build output');
    stderr.write('build error');
    exitCode = 17;
    return;
  }
  if (args[0] == '--stalled-child') {
    await Future<void>.delayed(const Duration(seconds: 20));
    return;
  }
  if (args[0] == '--writing-child') {
    final socket = await Socket.connect(
      InternetAddress.loopbackIPv4,
      int.parse(args[1]),
    );
    final output = File(args[2]);
    output.writeAsStringSync('writing');
    socket.writeln('writing');
    await socket.flush();
    await socket.first;
    output.writeAsStringSync('complete');
    await socket.close();
    return;
  }
  final events = ReceivePort();
  events.listen((event) {
    if (event == 'waiting') stdout.writeln('waiting');
  });
  stdout.writeln(await secondBuilder(args[0], args[1], events.sendPort));
  events.close();
}

Future<void> exitingResolver(List<Object> args) async {
  final repo = args[0] as String;
  final root = args[1] as String;
  final events = args[2] as SendPort;
  final repository = LocalDeveloperRepository(
    repositoryRoot: repo,
    dataRoot: p.join(root, 'exit-data'),
    dotnetSdkResolver: (_) async {
      writeOutputs(Directory(repo));
      Isolate.exit(events, 'resolver exited under ownership');
    },
  );
  await repository.createModProject(
    parentDirectory: p.join(root, 'exit'),
    id: 'test.exit',
    name: 'Exit',
  );
}

Future<void> callerWithWritingChild(List<Object> args) async {
  final repo = args[0] as String;
  final events = args[1] as SendPort;
  final control = ReceivePort();
  await withSourceSdkBuildLock(Directory(repo), () async {
    events.send(control.sendPort);
    unawaited(
      runOwnedSourceSdkBuild(
        Platform.resolvedExecutable,
        [
          '--packages=${p.absolute('.dart_tool/package_config.json')}',
          p.absolute('test/source_sdk_build_ownership_fixture.dart'),
          '--writing-child',
          '${args[2]}',
          args[3] as String,
        ],
        workingDirectory: Directory.current.path,
        timeout: const Duration(seconds: 20),
        maxStdoutBytes: 1024,
        maxStderrBytes: 1024,
      ),
    );
    await control.first;
    Isolate.exit(events, 'caller exited with active child');
  });
}

void sourceFixture(Directory repo) {
  File(
    p.join(repo.path, 'global.json'),
  ).writeAsStringSync('{"sdk":{"version":"10.0.301"}}');
  for (final name in topiaForgeSdkPackageIds) {
    final project = File(p.join(repo.path, 'src', name, '$name.csproj'));
    project.parent.createSync(recursive: true);
    project.writeAsStringSync(
      '<Project><PropertyGroup>'
      '<TargetFramework>netstandard2.1</TargetFramework>'
      '<Version>1.0.0</Version></PropertyGroup></Project>',
    );
  }
}

void writeOutputs(Directory repo) {
  for (final name in topiaForgeSdkPackageIds) {
    for (final relative in [
      'bin/Release/netstandard2.1/$name.dll',
      'bin/Release/netstandard2.1/$name.xml',
      'obj/Release/netstandard2.1/ref/$name.dll',
    ]) {
      final file = File(p.join(repo.path, 'src', name, relative));
      file.parent.createSync(recursive: true);
      file.writeAsStringSync('fixture $name');
    }
  }
}

Future<void> callerBeforeOwnerEntry(List<Object> args) async {
  unawaited(
    withSourceSdkBuildLock(
      Directory(args[0] as String),
      () async {
        throw StateError(
          'An unacknowledged caller must never receive ownership.',
        );
      },
      spawnOwner: (arguments, replies) async {
        arguments[2] = args[4] as SendPort;
        arguments[4] = args[2] as Duration;
        arguments.add(args[3] as SendPort);
        final owner = await Isolate.spawn(
          sourceSdkOwnerMain,
          arguments,
          paused: true,
          onExit: replies,
        );
        // The test waits for this caller's VM exit notification before resuming the
        // actual owner entrypoint, making late registration deterministic.
        Isolate.exit(args[1] as SendPort, [
          owner.controlPort,
          owner.pauseCapability,
        ]);
      },
    ),
  );
}

void immediatelyExitingCaller(Object? _) => Isolate.exit();

Future<void> callerWhileContended(List<Object> args) async {
  final control = ReceivePort();
  control.listen((_) => Isolate.exit());
  (args[1] as SendPort).send(control.sendPort);
  await runZoned(
    () => withSourceSdkBuildLock(
      Directory(args[0] as String),
      () async => (args[1] as SendPort).send('unexpected admission'),
      spawnOwner: (arguments, replies) async {
        final owner = await Isolate.spawn(
          sourceSdkOwnerMain,
          arguments,
          paused: true,
          onExit: replies,
        );
        owner.addOnExitListener(args[3] as SendPort);
        (args[4] as SendPort).send(owner.controlPort);
        owner.resume(owner.pauseCapability!);
        return owner;
      },
    ),
    zoneValues: {sourceSdkOwnershipObserver: args[2] as SendPort},
  );
}
