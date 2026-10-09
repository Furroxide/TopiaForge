#!/usr/bin/env bash
set -euo pipefail

# RC2 needs its own native admission protocol and isolated persistent-data proof.
# A Wine prefix or alternate BepInEx profile does not establish that boundary.
# Do not restore the retired schema2 runner as an isolation fallback.
if [[ $# == 1 && ( "$1" == "--help" || "$1" == "-h" ) ]]; then
  printf '%s\n' 'Proton acceptance is unavailable until native isolation is supported.'
  exit 0
fi
printf '%s\n' 'Proton acceptance: native isolation is not supported; Linux RC2 remains blocked.' >&2
exit 1
