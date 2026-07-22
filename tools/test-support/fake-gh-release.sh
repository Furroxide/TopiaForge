#!/usr/bin/env bash
set -euo pipefail

state=${FAKE_GH_STATE:?FAKE_GH_STATE is required}
mkdir -p "$state"
assets="$state/assets.json"
release="$state/release.json"
uploads="$state/uploads"
[[ -f $assets ]] || printf '[]\n' >"$assets"
[[ -f $uploads ]] || printf '0\n' >"$uploads"

if [[ ${1:-} == api ]]; then
  shift
  method=GET
  if [[ ${1:-} == --method ]]; then
    method=$2
    shift 2
  fi
  endpoint=${1:-}
  case "$method:$endpoint" in
    GET:repos/*/releases/tags/*)
      [[ -f $release ]] || exit 1
      cat "$release"
      ;;
    POST:repos/*/releases)
      payload=$(cat)
      jq -e \
        '.draft == true and (.prerelease | type == "boolean") and (.target_commitish | not)' \
        <<<"$payload" >/dev/null
      jq '. + {id: 1}' <<<"$payload" >"$release.tmp"
      mv "$release.tmp" "$release"
      cat "$release"
      ;;
    GET:repos/*/releases/1/assets\?per_page=100)
      cat "$assets"
      ;;
    DELETE:repos/*/releases/assets/*)
      id=${endpoint##*/}
      jq --argjson id "$id" '[.[] | select(.id != $id)]' "$assets" >"$assets.tmp"
      mv "$assets.tmp" "$assets"
      ;;
    *)
      echo "Unsupported fake gh api request: $method $endpoint" >&2
      exit 2
      ;;
  esac
  exit 0
fi

if [[ ${1:-} == release && ${2:-} == upload && $# -eq 4 ]]; then
  path=$4
  name=$(basename "$path")
  digest=$(sha256sum "$path" | awk '{print $1}')
  size=$(wc -c <"$path" | tr -d ' ')
  next_id=$(jq '[.[].id] | max // 0 | . + 1' "$assets")
  jq \
    --arg name "$name" \
    --arg digest "sha256:$digest" \
    --argjson size "$size" \
    --argjson id "$next_id" \
    '. + [{id:$id,name:$name,state:"uploaded",digest:$digest,size:$size}]' \
    "$assets" >"$assets.tmp"
  mv "$assets.tmp" "$assets"
  count=$(<"$uploads")
  printf '%s\n' "$((count + 1))" >"$uploads"
  exit 0
fi

echo "Unsupported fake gh command: $*" >&2
exit 2
