import 'package:launcher_data/launcher_data.dart';
import 'package:test/test.dart';

void main() {
  test('runs every callback once, in order, when cancelled', () {
    final token = CancellationToken();
    final calls = <int>[];
    token
      ..register(() => calls.add(1))
      ..register(() => calls.add(2));
    expect(token.isCancelled, isFalse);
    token
      ..cancel()
      ..cancel();
    expect(token.isCancelled, isTrue);
    expect(calls, [1, 2]);
  });

  test('a callback registered after cancelling runs at once', () {
    final token = CancellationToken()..cancel();
    var ran = false;
    token.register(() => ran = true).dispose();
    expect(ran, isTrue);
  });

  test('a disposed registration never runs', () {
    final token = CancellationToken();
    var ran = false;
    token.register(() => ran = true).dispose();
    token.cancel();
    expect(ran, isFalse);
  });

  test('one failing callback does not stop the others', () {
    final token = CancellationToken();
    var ran = false;
    token
      ..register(() => throw StateError('failed'))
      ..register(() => ran = true)
      ..cancel();
    expect(ran, isTrue);
  });

  test('throwIfCancelled throws only once cancelled', () {
    final token = CancellationToken();
    token.throwIfCancelled();
    token.cancel();
    expect(token.throwIfCancelled, throwsA(isA<OperationCancelledException>()));
  });
}
