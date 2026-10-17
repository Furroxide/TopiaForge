// Prints the application id the Linux runner gives a launcher, for
// linux_application_id_test.dart. The four arguments are TOPIAFORGE_DATA_ROOT,
// HOME, USERPROFILE and the current directory, each "-" when unset, "="
// followed by its value, or "x" followed by its bytes in hexadecimal, for
// bytes that a Dart string cannot carry.
#include <stdio.h>
#include <string.h>

#include "data_root_id.h"

static gchar* value(const char* argument) {
  if (argument[0] == '=') {
    return g_strdup(argument + 1);
  }
  if (argument[0] != 'x') {
    return nullptr;
  }
  const size_t length = strlen(argument + 1) / 2;
  gchar* bytes = static_cast<gchar*>(g_malloc0(length + 1));
  for (size_t i = 0; i < length; ++i) {
    bytes[i] = static_cast<gchar>(
        g_ascii_xdigit_value(argument[1 + 2 * i]) * 16 +
        g_ascii_xdigit_value(argument[2 + 2 * i]));
  }
  return bytes;
}

int main(int argc, char** argv) {
  if (argc != 5) {
    return 2;
  }
  g_autofree gchar* configured = value(argv[1]);
  g_autofree gchar* home = value(argv[2]);
  g_autofree gchar* user_profile = value(argv[3]);
  g_autofree gchar* current_directory = value(argv[4]);
  g_autofree gchar* id = data_root_application_id(
      "io.github.furroxide.topiaforge.launcher", configured, home,
      user_profile, current_directory);
  printf("%s\n", id);
  return 0;
}
