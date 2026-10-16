#include "my_application.h"

#include <flutter_linux/flutter_linux.h>
#ifdef GDK_WINDOWING_X11
#include <gdk/gdkx.h>
#endif

#include <cstring>

#include "flutter/generated_plugin_registrant.h"

// The channel Dart uses for single instance and session links
// (lib/src/launcher_instance_channel.dart).
static const char kInstanceChannel[] =
    "io.github.furroxide.topiaforge.launcher/instance";

// A session link is at most 512 characters. The margin lets a slightly longer
// one through, so that Dart reports it as too long itself.
static const size_t kMaximumLinkLength = 512 + 64;

// The options the update helper starts a freshly installed launcher with
// (lib/src/update_health_handshake.dart).
static const char kUpdateHealthNonce[] = "--topiaforge-update-health-nonce";
static const char kUpdateHealthFile[] = "--topiaforge-update-health-file";

struct _MyApplication {
  GtkApplication parent_instance;
  char** dart_entrypoint_arguments;
  GtkWindow* window;
  FlMethodChannel* instance_channel;
  gboolean dart_ready;
  // The newest link, or refusal, held until Dart listens.
  gchar* held_method;
  gchar* held_value;
};

G_DEFINE_TYPE(MyApplication, my_application, GTK_TYPE_APPLICATION)

// Whether |text| starts with the topiaforge: scheme, in ASCII case only. It
// stops at the first character that differs, so it never reads past the end
// of a shorter text.
static gboolean has_session_link_scheme(const gchar* text) {
  static const char scheme[] = "topiaforge:";
  for (size_t index = 0; scheme[index] != '\0'; index++) {
    char unit = text[index];
    if (unit >= 'A' && unit <= 'Z') {
      unit = static_cast<char>(unit - 'A' + 'a');
    }
    if (unit != scheme[index]) {
      return FALSE;
    }
  }
  return TRUE;
}

static gboolean is_nonce(const gchar* text) {
  size_t length = 0;
  for (; text[length] != '\0'; length++) {
    const char unit = text[length];
    if (!((unit >= '0' && unit <= '9') || (unit >= 'a' && unit <= 'f'))) {
      return FALSE;
    }
  }
  return length == 64;
}

// Whether |arguments| are exactly what the update helper starts a launcher
// with: the binary, the nonce option, a nonce of 64 lowercase hexadecimal
// characters, the file option and a path, and nothing else. The same rule as
// isUpdateHealthStart in Dart.
static gboolean is_update_health_start(gchar** arguments) {
  return arguments != nullptr && g_strv_length(arguments) == 5 &&
         g_strcmp0(arguments[1], kUpdateHealthNonce) == 0 &&
         is_nonce(arguments[2]) &&
         g_strcmp0(arguments[3], kUpdateHealthFile) == 0 &&
         arguments[4][0] != '\0';
}

// The link of a link start: its first argument after the binary name, when
// that has the topiaforge: scheme. Every other argument of a link start is
// ignored. The same rule as sessionLinkArgument in Dart.
static const gchar* session_link_argument(gchar** arguments) {
  if (arguments == nullptr || arguments[0] == nullptr ||
      arguments[1] == nullptr || !has_session_link_scheme(arguments[1])) {
    return nullptr;
  }
  return arguments[1];
}

static void send_to_dart(MyApplication* self,
                         const gchar* method,
                         const gchar* value) {
  if (!self->dart_ready || self->instance_channel == nullptr) {
    // Until Dart listens, the newest link replaces the one held.
    g_free(self->held_method);
    g_free(self->held_value);
    self->held_method = g_strdup(method);
    self->held_value = g_strdup(value);
    return;
  }
  g_autoptr(FlValue) argument = fl_value_new_string(value);
  fl_method_channel_invoke_method(self->instance_channel, method, argument,
                                  nullptr, nullptr, nullptr);
}

// Passes a link from a later start of the launcher to Dart. It is untrusted:
// only its scheme is read before its length is checked, and it reaches Dart
// as text and nothing else, never a shell.
static void receive_link(MyApplication* self, const gchar* link) {
  if (!has_session_link_scheme(link)) {
    return;
  }
  if (strnlen(link, kMaximumLinkLength + 1) > kMaximumLinkLength) {
    send_to_dart(self, "linkRefused", "tooLong");
  } else if (!g_utf8_validate(link, -1, nullptr)) {
    send_to_dart(self, "linkRefused", "invalid");
  } else {
    send_to_dart(self, "link", link);
  }
}

static void instance_call_cb(FlMethodChannel* channel,
                             FlMethodCall* method_call,
                             gpointer user_data) {
  MyApplication* self = MY_APPLICATION(user_data);
  if (strcmp(fl_method_call_get_name(method_call), "ready") != 0) {
    // D-Bus already keeps the launcher single, so Dart has nothing to claim
    // or hand off here.
    fl_method_call_respond_not_implemented(method_call, nullptr);
    return;
  }
  self->dart_ready = TRUE;
  if (self->held_method != nullptr) {
    g_autofree gchar* method = self->held_method;
    g_autofree gchar* value = self->held_value;
    self->held_method = nullptr;
    self->held_value = nullptr;
    send_to_dart(self, method, value);
  }
  fl_method_call_respond_success(method_call, nullptr, nullptr);
}

// Called when first Flutter frame received.
static void first_frame_cb(MyApplication* self, FlView* view) {
  gtk_widget_show(gtk_widget_get_toplevel(GTK_WIDGET(view)));
}

// Implements GApplication::activate.
static void my_application_activate(GApplication* application) {
  MyApplication* self = MY_APPLICATION(application);
  if (self->window != nullptr) {
    // One window per launcher: activating again brings it forward.
    gtk_window_present(self->window);
    return;
  }
  GtkWindow* window =
      GTK_WINDOW(gtk_application_window_new(GTK_APPLICATION(application)));
  self->window = window;
  g_object_add_weak_pointer(G_OBJECT(window),
                            reinterpret_cast<gpointer*>(&self->window));

  // Use a header bar when running in GNOME as this is the common style used
  // by applications and is the setup most users will be using (e.g. Ubuntu
  // desktop).
  // If running on X and not using GNOME then just use a traditional title bar
  // in case the window manager does more exotic layout, e.g. tiling.
  // If running on Wayland assume the header bar will work (may need changing
  // if future cases occur).
  gboolean use_header_bar = TRUE;
#ifdef GDK_WINDOWING_X11
  GdkScreen* screen = gtk_window_get_screen(window);
  if (GDK_IS_X11_SCREEN(screen)) {
    const gchar* wm_name = gdk_x11_screen_get_window_manager_name(screen);
    if (g_strcmp0(wm_name, "GNOME Shell") != 0) {
      use_header_bar = FALSE;
    }
  }
#endif
  if (use_header_bar) {
    GtkHeaderBar* header_bar = GTK_HEADER_BAR(gtk_header_bar_new());
    gtk_widget_show(GTK_WIDGET(header_bar));
    gtk_header_bar_set_title(header_bar, "TopiaForge");
    gtk_header_bar_set_show_close_button(header_bar, TRUE);
    gtk_window_set_titlebar(window, GTK_WIDGET(header_bar));
  } else {
    gtk_window_set_title(window, "TopiaForge");
  }

  gtk_window_set_default_size(window, 1280, 720);

  g_autoptr(FlDartProject) project = fl_dart_project_new();
  fl_dart_project_set_dart_entrypoint_arguments(
      project, self->dart_entrypoint_arguments);

  FlView* view = fl_view_new(project);
  GdkRGBA background_color;
  // Background defaults to black, override it here if necessary, e.g. #00000000
  // for transparent.
  gdk_rgba_parse(&background_color, "#000000");
  fl_view_set_background_color(view, &background_color);
  gtk_widget_show(GTK_WIDGET(view));
  gtk_container_add(GTK_CONTAINER(window), GTK_WIDGET(view));

  // Show the window when Flutter renders.
  // Requires the view to be realized so we can start rendering.
  g_signal_connect_swapped(view, "first-frame", G_CALLBACK(first_frame_cb),
                           self);
  gtk_widget_realize(GTK_WIDGET(view));

  fl_register_plugins(FL_PLUGIN_REGISTRY(view));

  g_autoptr(FlStandardMethodCodec) codec = fl_standard_method_codec_new();
  self->instance_channel = fl_method_channel_new(
      fl_engine_get_binary_messenger(fl_view_get_engine(view)),
      kInstanceChannel, FL_METHOD_CODEC(codec));
  fl_method_channel_set_method_call_handler(
      self->instance_channel, instance_call_cb, self, nullptr);

  gtk_widget_grab_focus(GTK_WIDGET(view));
}

// Implements GApplication::command_line. The running launcher sees its own
// command line first, then the command line of every later start, which D-Bus
// hands to it; that later start exits with the status returned here.
static int my_application_command_line(GApplication* application,
                                       GApplicationCommandLine* command_line) {
  MyApplication* self = MY_APPLICATION(application);
  g_auto(GStrv) arguments =
      g_application_command_line_get_arguments(command_line, nullptr);
  if (!g_application_command_line_get_is_remote(command_line)) {
    // This process's own start: the arguments after the binary name go to
    // Dart's main().
    g_clear_pointer(&self->dart_entrypoint_arguments, g_strfreev);
    if (arguments != nullptr && arguments[0] != nullptr) {
      self->dart_entrypoint_arguments = g_strdupv(arguments + 1);
    }
    g_application_activate(application);
    return 0;
  }
  // A later start: it hands over its link, if it has one, and this window
  // comes forward.
  const gchar* link = session_link_argument(arguments);
  if (link != nullptr) {
    receive_link(self, link);
  }
  if (self->window != nullptr) {
    gtk_window_present(self->window);
  }
  return 0;
}

// Whether a process owns |name| on |bus|. When the bus cannot say, the answer
// is yes: running beside a launcher is safe, handing over to one is not.
static gboolean bus_name_has_owner(GDBusConnection* bus, const gchar* name) {
  g_autoptr(GError) error = nullptr;
  g_autoptr(GVariant) reply = g_dbus_connection_call_sync(
      bus, "org.freedesktop.DBus", "/org/freedesktop/DBus",
      "org.freedesktop.DBus", "NameHasOwner", g_variant_new("(s)", name),
      G_VARIANT_TYPE("(b)"), G_DBUS_CALL_FLAGS_NONE, 1000, nullptr, &error);
  if (reply == nullptr) {
    return TRUE;
  }
  gboolean owned = FALSE;
  g_variant_get(reply, "(b)", &owned);
  return owned;
}

// Implements GApplication::local_command_line. It only decides how this
// process registers; GApplication then negotiates over D-Bus as usual, and
// either runs the command line here or hands it to the running launcher.
static gboolean my_application_local_command_line(GApplication* application,
                                                  gchar*** arguments,
                                                  int* exit_status) {
  g_autoptr(GError) error = nullptr;
  g_autoptr(GDBusConnection) bus =
      g_bus_get_sync(G_BUS_TYPE_SESSION, nullptr, &error);
  // Without a session bus there is nothing to negotiate. An update health
  // check must never hand over to another launcher and quit, because the
  // update helper is waiting for it to report: when a launcher already owns
  // the name, the check runs beside it. A start that takes the name between
  // this check and registration is not covered; the update then rolls back.
  if (bus == nullptr ||
      (is_update_health_start(*arguments) &&
       bus_name_has_owner(bus, g_application_get_application_id(application)))) {
    g_application_set_flags(
        application, static_cast<GApplicationFlags>(
                         G_APPLICATION_NON_UNIQUE |
                         G_APPLICATION_HANDLES_COMMAND_LINE));
  }
  return G_APPLICATION_CLASS(my_application_parent_class)
      ->local_command_line(application, arguments, exit_status);
}

// Implements GApplication::startup.
static void my_application_startup(GApplication* application) {
  // MyApplication* self = MY_APPLICATION(object);

  // Perform any actions required at application startup.

  G_APPLICATION_CLASS(my_application_parent_class)->startup(application);
}

// Implements GApplication::shutdown.
static void my_application_shutdown(GApplication* application) {
  // MyApplication* self = MY_APPLICATION(object);

  // Perform any actions required at application shutdown.

  G_APPLICATION_CLASS(my_application_parent_class)->shutdown(application);
}

// Implements GObject::dispose.
static void my_application_dispose(GObject* object) {
  MyApplication* self = MY_APPLICATION(object);
  if (self->window != nullptr) {
    g_object_remove_weak_pointer(G_OBJECT(self->window),
                                 reinterpret_cast<gpointer*>(&self->window));
    self->window = nullptr;
  }
  g_clear_object(&self->instance_channel);
  g_clear_pointer(&self->held_method, g_free);
  g_clear_pointer(&self->held_value, g_free);
  g_clear_pointer(&self->dart_entrypoint_arguments, g_strfreev);
  G_OBJECT_CLASS(my_application_parent_class)->dispose(object);
}

static void my_application_class_init(MyApplicationClass* klass) {
  G_APPLICATION_CLASS(klass)->activate = my_application_activate;
  G_APPLICATION_CLASS(klass)->command_line = my_application_command_line;
  G_APPLICATION_CLASS(klass)->local_command_line =
      my_application_local_command_line;
  G_APPLICATION_CLASS(klass)->startup = my_application_startup;
  G_APPLICATION_CLASS(klass)->shutdown = my_application_shutdown;
  G_OBJECT_CLASS(klass)->dispose = my_application_dispose;
}

static void my_application_init(MyApplication* self) {}

// The data root a launcher gets without TOPIAFORGE_DATA_ROOT, as
// resolveTopiaForgeDataRoot in Dart resolves it on Linux.
static gchar* default_data_root() {
  const gchar* home = g_getenv("HOME");
  if (home == nullptr) {
    home = g_getenv("USERPROFILE");
  }
  g_autofree gchar* current = nullptr;
  if (home == nullptr) {
    current = g_get_current_dir();
    home = current;
  }
  g_autofree gchar* root = g_build_filename(home, ".topiaforge_launcher", nullptr);
  return g_canonicalize_filename(root, nullptr);
}

// The application id names one launcher per data root. The default data root
// keeps the plain id, however it is spelled; any other root gets an id of its
// own, so launchers with different data roots never take each other's links.
// Inside a snap the id stays the one its D-Bus slot declares.
static gchar* application_id() {
  const gchar* configured = g_getenv("TOPIAFORGE_DATA_ROOT");
  if (configured == nullptr || g_getenv("SNAP") != nullptr) {
    return g_strdup(APPLICATION_ID);
  }
  g_autofree gchar* root = g_strstrip(g_strdup(configured));
  if (root[0] == '\0') {
    return g_strdup(APPLICATION_ID);
  }
  g_autofree gchar* absolute = g_canonicalize_filename(root, nullptr);
  g_autofree gchar* fallback = default_data_root();
  if (g_strcmp0(absolute, fallback) == 0) {
    return g_strdup(APPLICATION_ID);
  }
  g_autofree gchar* digest =
      g_compute_checksum_for_string(G_CHECKSUM_SHA256, absolute, -1);
  return g_strdup_printf("%s.r%.16s", APPLICATION_ID, digest);
}

MyApplication* my_application_new() {
  // Set the program name to the application ID, which helps various systems
  // like GTK and desktop environments map this running application to its
  // corresponding .desktop file. This ensures better integration by allowing
  // the application to be recognized beyond its binary name.
  g_set_prgname(APPLICATION_ID);

  g_autofree gchar* id = application_id();
  return MY_APPLICATION(g_object_new(my_application_get_type(),
                                     "application-id", id, "flags",
                                     G_APPLICATION_HANDLES_COMMAND_LINE,
                                     nullptr));
}
