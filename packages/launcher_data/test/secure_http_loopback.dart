import 'dart:async';
import 'dart:io';

/// Routes every HTTPS client the code under test creates to a plain HTTP
/// server on the loopback interface, so the helpers run end to end without
/// TLS or a network. Nothing leaves the machine.
final class LoopbackHttps extends HttpOverrides {
  LoopbackHttps._(this.server);

  static Future<LoopbackHttps> start(
    FutureOr<void> Function(HttpRequest request) handle,
  ) async {
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    server.listen((request) async {
      try {
        await handle(request);
      } on Object {
        // A client that hung up mid-answer is part of several tests.
      }
    });
    return LoopbackHttps._(server);
  }

  final HttpServer server;
  var clientsCreated = 0;
  final connections = <Uri>[];

  @override
  HttpClient createHttpClient(SecurityContext? context) {
    clientsCreated++;
    return super.createHttpClient(context)
      ..findProxy = ((_) => 'DIRECT')
      ..connectionFactory = (uri, proxyHost, proxyPort) {
        connections.add(uri);
        return Socket.startConnect(InternetAddress.loopbackIPv4, server.port);
      };
  }

  /// Runs [body] with every HTTP client routed here.
  Future<T> run<T>(Future<T> Function() body) =>
      HttpOverrides.runWithHttpOverrides(body, this);

  Future<void> close() => server.close(force: true);
}

/// Counts HTTP clients without routing them anywhere: any that is created
/// fails to connect.
final class CountingHttpOverrides extends HttpOverrides {
  var clientsCreated = 0;

  @override
  HttpClient createHttpClient(SecurityContext? context) {
    clientsCreated++;
    return super.createHttpClient(context)
      ..findProxy = ((_) => 'DIRECT')
      ..connectionFactory = (uri, proxyHost, proxyPort) =>
          throw const SocketException('No connection may open.');
  }
}
