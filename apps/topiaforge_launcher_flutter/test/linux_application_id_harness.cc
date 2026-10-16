// Prints the application id the Linux runner gives a launcher, for
// linux_application_id_test.dart. The four arguments are TOPIAFORGE_DATA_ROOT,
// HOME, USERPROFILE and the current directory, each "-" when unset or "="
// followed by its value.
#include <stdio.h>

#include "data_root_id.h"

static const char* value(const char* argument) {
  return argument[0] == '=' ? argument + 1 : nullptr;
}

int main(int argc, char** argv) {
  if (argc != 5) {
    return 2;
  }
  g_autofree gchar* id = data_root_application_id(
      "io.github.furroxide.topiaforge.launcher", value(argv[1]),
      value(argv[2]), value(argv[3]), value(argv[4]));
  printf("%s\n", id);
  return 0;
}
