/// Downloads an archive a host serves in developer mode, from its bounded
/// TCP endpoint (`docs/internal/MultiplayerLiveSessions.md`, section 15).
library;

import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:crypto/crypto.dart';
import 'package:launcher_domain/launcher_domain.dart';

import 'descriptor_client.dart';
import 'package_response.dart';

/// Asks one host for one archive with `TFD1 package <sha256>`, and streams
/// the answer to a file with its SHA-256 computed as it arrives.
///
/// - **Contact.** Under relay-only an address beyond the local network is
///   refused before anything opens; that is the last line of the
///   [JoinContactRules], which callers also apply before they get here.
/// - **Bounds.** The answer is read as [Tfd1PackageReader] reads it: a
///   length past [maximumBytes] is refused before the body, and the body is
///   read up to the announced length and no further.
/// - **Deadlines.** The connection must open within [connectTimeout], as a
///   descriptor fetch's does. A host that sends nothing for [stallTimeout],
///   the status line included, has stalled. The whole answer must arrive
///   within [transferTimeout], the two minutes the game's endpoint gives one
///   transfer before it closes it, so waiting longer could never succeed.
/// - **Cancellation** closes the connection at once.
/// - **Nothing partial.** However the download ends without the whole
///   announced body, the file is deleted before the call returns. The file
///   is created before anything is contacted, so a disk that cannot take it
///   contacts nobody.
/// - **Failures** never throw and never name an address: a socket's own
///   messages can, so none is read.
final class HostPackageClient {
  HostPackageClient({
    this.connectTimeout = const Duration(seconds: 5),
    this.stallTimeout = const Duration(seconds: 15),
    this.transferTimeout = const Duration(minutes: 2),
    this.maximumBytes = ModRegistryFormat.maxPackageBytes,
    DescriptorConnector? connect,
    DescriptorTimer? timer,
  }) : _connect = connect ?? Socket.startConnect,
       _timer = timer ?? Timer.new {
    RangeError.checkValueInInterval(
      maximumBytes,
      1,
      ModRegistryFormat.maxPackageBytes,
      'maximumBytes',
    );
  }

  final Duration connectTimeout;
  final Duration stallTimeout;
  final Duration transferTimeout;
  final int maximumBytes;
  final DescriptorConnector _connect;
  final DescriptorTimer _timer;

  /// The request for the archive whose SHA-256 is [sha256].
  static Uint8List request(String sha256) =>
      ascii.encode('TFD1 package $sha256\n');

  /// Downloads the archive with [sha256] from [address] and [port] into
  /// [target]. [allowDirect] is the session's choice: without it, only an
  /// address on the local network is contacted. [onProgress] gets the bytes
  /// written so far and the announced length.
  Future<JoinHostPackageResult> fetchFrom(
    InternetAddress address,
    int port,
    String sha256,
    File target, {
    required bool allowDirect,
    void Function(int bytesReceived, int bytesTotal)? onProgress,
    CancellationToken? cancellation,
  }) async {
    if (!WireText.isHex(sha256, 32)) {
      throw ArgumentError('A SHA-256 is 64 lowercase hexadecimal digits.');
    }
    if (!allowDirect && !DescriptorClient.isOnLocalNetwork(address)) {
      return const JoinHostPackageFailed(
        JoinHostPackageProblem.directNotAllowed,
      );
    }
    if (port < 1 || port > 65535) {
      return const JoinHostPackageFailed(JoinHostPackageProblem.unreachable);
    }
    if (cancellation?.isCancelled ?? false) {
      return const JoinHostPackageFailed(JoinHostPackageProblem.cancelled);
    }
    return _Transfer(
      this,
      target,
      onProgress,
      cancellation,
    ).run(address, port, archiveSha256: sha256);
  }
}

/// One download: its connection, its deadlines and its file.
final class _Transfer {
  _Transfer(this._client, this._target, this._onProgress, this._cancellation);

  final HostPackageClient _client;
  final File _target;
  final void Function(int bytesReceived, int bytesTotal)? _onProgress;
  final CancellationToken? _cancellation;
  final _opened = Completer<Socket?>();
  ConnectionTask<Socket>? _task;
  Socket? _socket;
  Timer? _connectDeadline;
  Timer? _stall;
  Timer? _whole;

  /// Why the transfer was stopped from outside its connection.
  JoinHostPackageProblem? _stopped;

  Future<JoinHostPackageResult> run(
    InternetAddress address,
    int port, {
    required String archiveSha256,
  }) async {
    RandomAccessFile? output;
    var kept = false;
    CancellationRegistration? registration;
    try {
      try {
        output = await _target.open(mode: FileMode.write);
      } on FileSystemException {
        return const JoinHostPackageFailed(JoinHostPackageProblem.notSaved);
      }
      registration = _cancellation?.register(
        () => _stop(JoinHostPackageProblem.cancelled),
      );
      final socket = await _open(address, port);
      if (socket == null) {
        return JoinHostPackageFailed(
          _stopped ?? JoinHostPackageProblem.unreachable,
        );
      }
      final result = await _receive(socket, output, archiveSha256);
      kept = result is JoinHostPackageDownloaded;
      return result;
    } finally {
      registration?.dispose();
      _connectDeadline?.cancel();
      _stall?.cancel();
      _whole?.cancel();
      _socket?.destroy();
      _task?.cancel();
      try {
        await output?.close();
      } on FileSystemException {
        // Closing what failed to write; the delete below still runs.
      }
      if (!kept) await _deleteQuietly(_target);
    }
  }

  /// Opens the connection within the connect deadline; null when it could
  /// not open or the transfer was stopped first.
  Future<Socket?> _open(InternetAddress address, int port) {
    // Cancelled while the file was being created: contact nobody.
    if (_opened.isCompleted) return _opened.future;
    _connectDeadline = _client._timer(
      _client.connectTimeout,
      () => _stop(JoinHostPackageProblem.unreachable),
    );
    // A socket's own message may name the address, so the error is not read.
    void failed(Object _) => _stop(JoinHostPackageProblem.unreachable);
    Future.sync(() => _client._connect(address, port)).then((task) {
      if (_opened.isCompleted) {
        // Too late: consume the attempt's outcome before abandoning it.
        unawaited(task.socket.then((late) => late.destroy(), onError: (_) {}));
        task.cancel();
        return;
      }
      _task = task;
      task.socket.then((socket) {
        if (_opened.isCompleted) {
          socket.destroy();
          return;
        }
        _connectDeadline?.cancel();
        _socket = socket;
        _opened.complete(socket);
      }, onError: failed);
    }, onError: failed);
    return _opened.future;
  }

  Future<JoinHostPackageResult> _receive(
    Socket socket,
    RandomAccessFile output,
    String archiveSha256,
  ) async {
    unawaited(socket.done.then((_) {}, onError: (Object _) {}));
    final reader = Tfd1PackageReader(maximumBodyBytes: _client.maximumBytes);
    final digest = _Digest();
    final hashing = sha256.startChunkedConversion(digest);
    try {
      socket.setOption(SocketOption.tcpNoDelay, true);
      socket.add(HostPackageClient.request(archiveSha256));
      _whole = _client._timer(
        _client.transferTimeout,
        () => _stop(JoinHostPackageProblem.timedOut),
      );
      _armStall();
      await for (final chunk in socket) {
        if (_stopped != null) break;
        final body = reader.add(chunk);
        if (body.isNotEmpty) {
          _stall?.cancel();
          hashing.add(body);
          await output.writeFrom(body);
          _report(reader.receivedBytes, reader.announcedLength!);
        }
        if (reader.isDone || _stopped != null) break;
        _armStall();
      }
    } on FileSystemException {
      return const JoinHostPackageFailed(JoinHostPackageProblem.notSaved);
    } on Object {
      return JoinHostPackageFailed(
        _stopped ?? JoinHostPackageProblem.unreachable,
      );
    }
    final stopped = _stopped;
    if (stopped != null) return JoinHostPackageFailed(stopped);
    switch (reader.result ?? reader.close()) {
      case Tfd1PackageComplete(:final length):
        try {
          await output.flush();
        } on FileSystemException {
          return const JoinHostPackageFailed(JoinHostPackageProblem.notSaved);
        }
        hashing.close();
        return JoinHostPackageDownloaded(
          sha256: digest.value.toString(),
          length: length,
        );
      case Tfd1PackageStatus(:final status):
        return JoinHostPackageFailed(switch (status) {
          '404' => JoinHostPackageProblem.notServed,
          '429' || '503' => JoinHostPackageProblem.busy,
          _ => JoinHostPackageProblem.refused,
        });
      case Tfd1PackageMalformed(:final problem):
        return JoinHostPackageFailed(switch (problem) {
          Tfd1PackageProblem.tooLarge => JoinHostPackageProblem.tooLarge,
          Tfd1PackageProblem.endedEarly => JoinHostPackageProblem.endedEarly,
          Tfd1PackageProblem.noStatusLine ||
          Tfd1PackageProblem.badStatusLine ||
          Tfd1PackageProblem.impossibleLength =>
            JoinHostPackageProblem.malformed,
        });
    }
  }

  /// Progress is only shown, so a listener that throws never stops the
  /// download.
  void _report(int received, int total) {
    try {
      _onProgress?.call(received, total);
    } on Object {
      // Ignored: see above.
    }
  }

  void _armStall() {
    _stall?.cancel();
    _stall = _client._timer(
      _client.stallTimeout,
      () => _stop(JoinHostPackageProblem.stalled),
    );
  }

  /// Stops the transfer for [problem] and closes its connection at once.
  /// The first reason wins.
  void _stop(JoinHostPackageProblem problem) {
    _stopped ??= problem;
    if (!_opened.isCompleted) _opened.complete(null);
    _task?.cancel();
    _socket?.destroy();
  }

  static Future<void> _deleteQuietly(File file) async {
    try {
      if (FileSystemEntity.typeSync(file.path, followLinks: false) ==
          FileSystemEntityType.file) {
        await file.delete();
      }
    } on FileSystemException {
      // A partial file that cannot be deleted just now is swept later.
    }
  }
}

final class _Digest implements Sink<Digest> {
  Digest? _value;

  Digest get value => _value!;

  @override
  void add(Digest data) => _value = data;

  @override
  void close() {}
}
