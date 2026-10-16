#include "data_root_id.h"

#include <string.h>

// Whitespace to Dart's String.trim(): the Unicode White_Space characters and
// the byte order mark, U+0009..U+000D, U+0020, U+0085, U+00A0, U+1680,
// U+2000..U+200A, U+2028, U+2029, U+202F, U+205F, U+3000 and U+FEFF.
static gboolean is_dart_whitespace(gunichar c) {
  return (c >= 0x09 && c <= 0x0D) || c == 0x20 || c == 0x85 || c == 0xA0 ||
         c == 0x1680 || (c >= 0x2000 && c <= 0x200A) || c == 0x2028 ||
         c == 0x2029 || c == 0x202F || c == 0x205F || c == 0x3000 ||
         c == 0xFEFF;
}

// |text| without the whitespace Dart's String.trim() removes from its ends.
// Text that is not UTF-8 is kept as it is.
static gchar* dart_trim(const gchar* text) {
  if (!g_utf8_validate(text, -1, nullptr)) {
    return g_strdup(text);
  }
  const gchar* start = text;
  while (*start != '\0' && is_dart_whitespace(g_utf8_get_char(start))) {
    start = g_utf8_next_char(start);
  }
  const gchar* end = start + strlen(start);
  while (end > start) {
    const gchar* previous = g_utf8_prev_char(end);
    if (!is_dart_whitespace(g_utf8_get_char(previous))) {
      break;
    }
    end = previous;
  }
  return g_strndup(start, end - start);
}

// |path| as package:path's POSIX normalize(absolute(path)) gives it, with a
// relative path resolved against |current_directory|. Separators collapse, a
// leading "//" too; "." segments go; ".." takes the segment before it, and
// nothing at the root; no separator trails. Null when a relative path has no
// current directory to resolve against.
static gchar* dart_normalized_absolute(const gchar* path,
                                       const gchar* current_directory) {
  g_autofree gchar* absolute = nullptr;
  if (path[0] == '/') {
    absolute = g_strdup(path);
  } else if (current_directory != nullptr && current_directory[0] == '/') {
    absolute = g_strconcat(current_directory, "/", path, nullptr);
  } else {
    return nullptr;
  }
  g_auto(GStrv) segments = g_strsplit(absolute, "/", -1);
  g_autoptr(GPtrArray) kept = g_ptr_array_new();
  for (gchar** segment = segments; *segment != nullptr; ++segment) {
    if ((*segment)[0] == '\0' || strcmp(*segment, ".") == 0) {
      continue;
    }
    if (strcmp(*segment, "..") == 0) {
      if (kept->len > 0) {
        g_ptr_array_remove_index(kept, kept->len - 1);
      }
      continue;
    }
    g_ptr_array_add(kept, *segment);
  }
  g_ptr_array_add(kept, nullptr);
  g_autofree gchar* joined =
      g_strjoinv("/", reinterpret_cast<gchar**>(kept->pdata));
  return g_strconcat("/", joined, nullptr);
}

gchar* data_root_application_id(const gchar* base_id,
                                const gchar* configured,
                                const gchar* home,
                                const gchar* user_profile,
                                const gchar* current_directory) {
  if (configured == nullptr) {
    return g_strdup(base_id);
  }
  g_autofree gchar* root = dart_trim(configured);
  if (root[0] == '\0') {
    return g_strdup(base_id);
  }
  g_autofree gchar* key = dart_normalized_absolute(root, current_directory);

  // The root without TOPIAFORGE_DATA_ROOT: HOME, else USERPROFILE, else the
  // current directory, joined with ".topiaforge_launcher" the way
  // package:path joins, which skips an empty HOME.
  const gchar* base = home != nullptr           ? home
                      : user_profile != nullptr ? user_profile
                                                : current_directory;
  g_autofree gchar* fallback = nullptr;
  if (base != nullptr) {
    g_autofree gchar* joined =
        base[0] == '\0' ? g_strdup(".topiaforge_launcher")
                        : g_strconcat(base, "/.topiaforge_launcher", nullptr);
    fallback = dart_normalized_absolute(joined, current_directory);
  }
  if (key != nullptr && fallback != nullptr && strcmp(key, fallback) == 0) {
    return g_strdup(base_id);
  }
  g_autofree gchar* digest = g_compute_checksum_for_string(
      G_CHECKSUM_SHA256, key != nullptr ? key : root, -1);
  return g_strdup_printf("%s.r%.16s", base_id, digest);
}
