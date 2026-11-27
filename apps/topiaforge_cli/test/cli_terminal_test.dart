import 'package:test/test.dart';
import 'package:topiaforge/src/cli_terminal.dart';

void main() {
  test('redirected input is noninteractive without querying console mode', () {
    expect(
      hasInteractiveInput(
        hasTerminal: () => false,
        readLineMode: () => throw StateError('must not probe'),
      ),
      isFalse,
    );
  });
  test('a detached Windows character handle is noninteractive', () {
    expect(
      hasInteractiveInput(
        hasTerminal: () => true,
        readLineMode: () => throw StateError('no console input mode'),
      ),
      isFalse,
    );
  });
  for (final lineMode in [true, false]) {
    test('a real terminal in line mode $lineMode still requires prompts', () {
      expect(
        hasInteractiveInput(
          hasTerminal: () => true,
          readLineMode: () => lineMode,
        ),
        isTrue,
      );
    });
  }
  test('an unavailable input handle is noninteractive', () {
    expect(
      hasInteractiveInput(hasTerminal: () => throw StateError('closed')),
      isFalse,
    );
  });
}
