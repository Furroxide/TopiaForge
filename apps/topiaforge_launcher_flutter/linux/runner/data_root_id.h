#ifndef RUNNER_DATA_ROOT_ID_H_
#define RUNNER_DATA_ROOT_ID_H_

#include <glib.h>

// The application id of a launcher, which names one launcher per data root.
//
// The data root comes from |configured| (TOPIAFORGE_DATA_ROOT), |home|
// (HOME), |user_profile| (USERPROFILE) and |current_directory| (getcwd), each
// null when unset, exactly as resolveTopiaForgeDataRoot in Dart resolves it on
// Linux. Roots compare the way isDefaultTopiaForgeDataRoot compares them, with
// package:path: the root a launcher gets without TOPIAFORGE_DATA_ROOT keeps
// |base_id| however it is spelled, and any other root gets |base_id| with
// ".r" and 16 hexadecimal characters of the SHA-256 of its normalized path,
// so one root always gets one id.
gchar* data_root_application_id(const gchar* base_id,
                                const gchar* configured,
                                const gchar* home,
                                const gchar* user_profile,
                                const gchar* current_directory);

#endif  // RUNNER_DATA_ROOT_ID_H_
