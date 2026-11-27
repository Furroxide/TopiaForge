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

// |text|, an environment variable, as the characters of the string Dart's
// Platform.environment holds for it, or null when Dart leaves the variable
// out. Dart decodes UTF-8 as its VM does: it leaves out a variable with a
// stray or truncated sequence, an overlong form or a value past U+10FFFF, and
// keeps an encoded surrogate as a code unit of its own, so a high surrogate
// followed by a low one is one character, however each was encoded. GLib's
// UTF-8 rules are not Dart's: they refuse an encoded surrogate.
static GArray* dart_characters(const gchar* text) {
  if (text == nullptr) {
    return nullptr;
  }
  static const gunichar minimum[] = {0, 0, 0x80, 0x800, 0x10000};
  g_autoptr(GArray) characters = g_array_new(FALSE, FALSE, sizeof(gunichar));
  const guchar* bytes = reinterpret_cast<const guchar*>(text);
  while (*bytes != '\0') {
    const guchar lead = *bytes;
    const int length = lead < 0x80   ? 1
                       : lead < 0xC0 ? 0
                       : lead < 0xE0 ? 2
                       : lead < 0xF0 ? 3
                       : lead < 0xF8 ? 4
                                     : 0;
    if (length == 0) {
      return nullptr;
    }
    gunichar c = length == 1 ? lead : lead & (0x7F >> length);
    for (int i = 1; i < length; ++i) {
      if ((bytes[i] & 0xC0) != 0x80) {
        return nullptr;
      }
      c = (c << 6) | (bytes[i] & 0x3F);
    }
    if (c < minimum[length] || c > 0x10FFFF) {
      return nullptr;
    }
    bytes += length;
    if (c >= 0xDC00 && c <= 0xDFFF && characters->len > 0) {
      gunichar& previous =
          g_array_index(characters, gunichar, characters->len - 1);
      if (previous >= 0xD800 && previous <= 0xDBFF) {
        previous = 0x10000 + ((previous - 0xD800) << 10) + (c - 0xDC00);
        continue;
      }
    }
    g_array_append_val(characters, c);
  }
  return static_cast<GArray*>(g_steal_pointer(&characters));
}

// |characters| from |start| to |end| as UTF-8, a lone surrogate in the three
// bytes Dart reads it from, so equal Dart strings give equal bytes.
static gchar* utf8_of(GArray* characters, guint start, guint end) {
  GString* text = g_string_sized_new(end - start);
  for (guint i = start; i < end; ++i) {
    const gunichar c = g_array_index(characters, gunichar, i);
    if (c < 0x80) {
      g_string_append_c(text, static_cast<gchar>(c));
    } else if (c < 0x800) {
      g_string_append_c(text, static_cast<gchar>(0xC0 | (c >> 6)));
      g_string_append_c(text, static_cast<gchar>(0x80 | (c & 0x3F)));
    } else if (c < 0x10000) {
      g_string_append_c(text, static_cast<gchar>(0xE0 | (c >> 12)));
      g_string_append_c(text, static_cast<gchar>(0x80 | ((c >> 6) & 0x3F)));
      g_string_append_c(text, static_cast<gchar>(0x80 | (c & 0x3F)));
    } else {
      g_string_append_c(text, static_cast<gchar>(0xF0 | (c >> 18)));
      g_string_append_c(text, static_cast<gchar>(0x80 | ((c >> 12) & 0x3F)));
      g_string_append_c(text, static_cast<gchar>(0x80 | ((c >> 6) & 0x3F)));
      g_string_append_c(text, static_cast<gchar>(0x80 | (c & 0x3F)));
    }
  }
  return g_string_free(text, FALSE);
}

// |text|, an environment variable, as Dart holds it; null when Dart leaves it
// out.
static gchar* dart_text(const gchar* text) {
  g_autoptr(GArray) characters = dart_characters(text);
  if (characters == nullptr) {
    return nullptr;
  }
  return utf8_of(characters, 0, characters->len);
}

// |text| as Dart holds it, without the whitespace Dart's String.trim() removes
// from its ends; null when Dart leaves it out.
static gchar* dart_trimmed(const gchar* text) {
  g_autoptr(GArray) characters = dart_characters(text);
  if (characters == nullptr) {
    return nullptr;
  }
  guint start = 0;
  guint end = characters->len;
  while (start < end &&
         is_dart_whitespace(g_array_index(characters, gunichar, start))) {
    ++start;
  }
  while (end > start &&
         is_dart_whitespace(g_array_index(characters, gunichar, end - 1))) {
    --end;
  }
  return utf8_of(characters, start, end);
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
  // Each variable as Dart's Platform.environment holds it. One that Dart
  // cannot decode is unset to Dart, so it is unset here too.
  g_autofree gchar* root = dart_trimmed(configured);
  if (root == nullptr || root[0] == '\0') {
    return g_strdup(base_id);
  }
  g_autofree gchar* home_text = dart_text(home);
  g_autofree gchar* profile_text = dart_text(user_profile);
  g_autofree gchar* current = dart_text(current_directory);
  g_autofree gchar* key = dart_normalized_absolute(root, current);

  // The root without TOPIAFORGE_DATA_ROOT: HOME, else USERPROFILE, else the
  // current directory, joined with ".topiaforge_launcher" the way
  // package:path joins, which skips an empty HOME.
  const gchar* base = home_text != nullptr      ? home_text
                      : profile_text != nullptr ? profile_text
                                                : current;
  g_autofree gchar* fallback = nullptr;
  if (base != nullptr) {
    g_autofree gchar* joined =
        base[0] == '\0' ? g_strdup(".topiaforge_launcher")
                        : g_strconcat(base, "/.topiaforge_launcher", nullptr);
    fallback = dart_normalized_absolute(joined, current);
  }
  if (key != nullptr && fallback != nullptr && strcmp(key, fallback) == 0) {
    return g_strdup(base_id);
  }
  g_autofree gchar* digest = g_compute_checksum_for_string(
      G_CHECKSUM_SHA256, key != nullptr ? key : root, -1);
  return g_strdup_printf("%s.r%.16s", base_id, digest);
}
