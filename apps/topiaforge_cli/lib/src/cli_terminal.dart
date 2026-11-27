import 'dart:io';

/// A character handle is not necessarily a readable console. On Windows a
/// detached command can report hasTerminal yet reject console-mode queries
/// and readLineSync. Probe the input mode without changing it before choosing
/// the interactive consent path. A real terminal still prompts with --yes.
bool hasInteractiveInput({
  bool Function()? hasTerminal,
  bool Function()? readLineMode,
}) {
  try {
    if (!(hasTerminal ?? () => stdin.hasTerminal)()) return false;
    (readLineMode ?? () => stdin.lineMode)();
    return true;
  } on Object {
    return false;
  }
}
