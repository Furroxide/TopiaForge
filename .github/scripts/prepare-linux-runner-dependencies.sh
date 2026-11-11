#!/usr/bin/env bash
# Install the prerequisites used by the launcher's Linux application-id tests.
set -euo pipefail

if (( $# != 0 )); then
  echo "This helper takes no arguments." >&2
  exit 2
fi
if [[ "$(lsb_release -is)" != Ubuntu || "$(lsb_release -cs)" != noble ]]; then
  echo "This helper requires Ubuntu 24.04 (noble)." >&2
  exit 2
fi
if ! c++ --version >/dev/null 2>&1; then
  echo "The Linux runner tests require the image's C++ compiler." >&2
  exit 1
fi
if pkg-config --exists glib-2.0; then
  echo "The Linux runner test prerequisites are already available."
  exit 0
fi

run_apt() {
  # Bound each acquisition phase, including mirrors that fail to resolve.
  sudo timeout --kill-after=5s 120s apt-get \
    -o Acquire::Retries=0 \
    -o Acquire::http::Timeout=20 \
    -o Acquire::https::Timeout=20 \
    -o APT::Update::Error-Mode=any \
    "$@"
}

install_dependencies() {
  # This function is used as an if condition: explicitly propagate both failures.
  run_apt "$@" update &&
    run_apt "$@" install -y pkg-config libglib2.0-dev
}

if ! install_dependencies; then
  echo "Default package acquisition failed; trying the signed Ubuntu archive at MIT."
  source_file=$(mktemp "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/topiaforge-linux-deps-XXXXXX.sources")
  trap 'rm -f -- "$source_file"' EXIT
  cat >"$source_file" <<'SOURCES'
Types: deb
URIs: https://mirrors.mit.edu/ubuntu/
Suites: noble noble-updates noble-security
Components: main
Signed-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg
SOURCES
  chmod 644 "$source_file"
  # Only these commands use the fallback. Keep the image's sources and resolver intact.
  install_dependencies \
    -o "Dir::Etc::sourcelist=$source_file" \
    -o Dir::Etc::sourceparts=-
fi

# Match the Dart suite's skip checks so a successful setup cannot hide skipped native tests.
c++ --version >/dev/null
pkg-config --exists glib-2.0
echo "The Linux runner test prerequisites are available."
