#!/usr/bin/env bash
set -euo pipefail

state=${FAKE_GH_STATE:?FAKE_GH_STATE is required}
mkdir -p "$state/asset-content"
assets="$state/assets.json"
release="$state/release.json"
uploads="$state/uploads"
[[ -f $assets ]] || printf '[]\n' >"$assets"
[[ -f $uploads ]] || printf '0\n' >"$uploads"

if [[ ${1:-} == api ]]; then
  shift
  method=GET
  input=false
  endpoint=
  accept_json=true
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --method)
        method=$2
        shift 2
        ;;
      --input)
        input=true
        shift 2
        ;;
      -H)
        [[ $2 != 'Accept: application/octet-stream' ]] || accept_json=false
        shift 2
        ;;
      -*)
        echo "Unsupported fake gh api option: $1" >&2
        exit 2
        ;;
      *)
        endpoint=$1
        shift
        ;;
    esac
  done
  [[ -n $endpoint ]] || exit 64
  case "$method:$endpoint" in
    GET:repos/*/immutable-releases)
      [[ ${FAKE_GH_IMMUTABLE_ENABLED:-true} == true ]] || exit 1
      printf '%s\n' "${GH_TOKEN:-}" >"$state/immutable-token"
      printf '{"enabled":true,"enforced_by_owner":false}\n'
      ;;
    GET:repos/*/collaborators/*/permission)
      author=${endpoint#*collaborators/}
      author=${author%/permission}
      jq -n \
        --arg author "$author" \
        --arg permission "${FAKE_GH_AUTHOR_PERMISSION:-admin}" \
        --argjson id "${FAKE_GH_AUTHOR_ID:-221987073}" \
        --arg type "${FAKE_GH_AUTHOR_TYPE:-User}" \
        '{permission:$permission,user:{login:$author,id:$id,type:$type}}'
      ;;
    GET:repos/*/releases/tags/*)
      [[ -f $release ]] || exit 1
      cat "$release"
      ;;
    GET:repos/*/releases/1/assets\?per_page=100)
      asset_get_count_file="$state/asset-get-count"
      [[ -f $asset_get_count_file ]] || printf '0\n' >"$asset_get_count_file"
      asset_get_count=$(<"$asset_get_count_file")
      asset_get_count=$((asset_get_count + 1))
      printf '%s\n' "$asset_get_count" >"$asset_get_count_file"
      if [[ -n ${FAKE_GH_MUTATE_ASSETS_ON_GET:-} &&
            $asset_get_count == "$FAKE_GH_MUTATE_ASSETS_ON_GET" ]]; then
        jq '.[0].size += 1' "$assets" >"$assets.tmp"
        mv "$assets.tmp" "$assets"
      fi
      cat "$assets"
      ;;
    GET:repos/*/releases/assets/*)
      [[ $accept_json == false ]] || {
        echo "Asset download requires the octet-stream media type." >&2
        exit 2
      }
      id=${endpoint##*/}
      [[ -f $state/asset-content/$id ]] || exit 1
      cat "$state/asset-content/$id"
      ;;
    GET:repos/*)
      printf '{"permissions":{"admin":%s}}\n' \
        "${FAKE_GH_CALLER_ADMIN:-true}"
      ;;
    POST:repos/*/releases)
      [[ $input == true ]] || exit 2
      payload=$(cat)
      jq -e \
        '.draft == true and
         (.prerelease | type == "boolean") and
         (.target_commitish | not)' \
        <<<"$payload" >/dev/null
      jq \
        --arg author_login "${FAKE_GH_RELEASE_AUTHOR_LOGIN:-furroxide}" \
        --argjson author_id "${FAKE_GH_RELEASE_AUTHOR_ID:-221987073}" \
        --arg author_type "${FAKE_GH_RELEASE_AUTHOR_TYPE:-User}" \
        '. + {
          id: 1,
          immutable: false,
          published_at: null,
          author: {
            login:$author_login,
            id:$author_id,
            type:$author_type
          }
        }' \
        <<<"$payload" >"$release.tmp"
      mv "$release.tmp" "$release"
      cat "$release"
      ;;
    PATCH:repos/*/releases/1)
      [[ $input == true ]] || exit 2
      payload=$(cat)
      jq -e '. == {draft:false}' <<<"$payload" >/dev/null
      jq \
        --argjson immutable "${FAKE_GH_PUBLISHED_IMMUTABLE:-true}" \
        '.draft=false |
         .immutable=$immutable |
         .published_at="2026-07-31T00:00:00Z"' \
        "$release" >"$release.tmp"
      mv "$release.tmp" "$release"
      cat "$release"
      ;;
    DELETE:repos/*/releases/assets/*)
      id=${endpoint##*/}
      jq --argjson id "$id" '[.[] | select(.id != $id)]' \
        "$assets" >"$assets.tmp"
      mv "$assets.tmp" "$assets"
      rm -f "$state/asset-content/$id"
      ;;
    *)
      echo "Unsupported fake gh api request: $method $endpoint" >&2
      exit 2
      ;;
  esac
  exit 0
fi

if [[ ${1:-} == release && ${2:-} == upload && $# -eq 6 &&
      ${5:-} == --repo && ${6:-} == owner/repo ]]; then
  path=$4
  name=$(basename "$path")
  digest=$(sha256sum "$path" | awk '{print $1}')
  size=$(wc -c <"$path" | tr -d ' ')
  next_id=$(jq '[.[].id] | max // 0 | . + 1' "$assets")
  upload_principal=${FAKE_GH_UPLOAD_PRINCIPAL:-staging}
  case "$upload_principal" in
    staging)
      uploader_login=furroxide
      uploader_id=221987073
      uploader_type=User
      integration_id=null
      ;;
    workflow)
      uploader_login='github-actions[bot]'
      uploader_id=41898282
      uploader_type=Bot
      integration_id=15368
      ;;
    *)
      echo "Unsupported fake upload principal: $upload_principal" >&2
      exit 2
      ;;
  esac
  jq \
    --arg name "$name" \
    --arg digest "sha256:$digest" \
    --argjson size "$size" \
    --argjson id "$next_id" \
    --arg uploader_login "$uploader_login" \
    --argjson uploader_id "$uploader_id" \
    --arg uploader_type "$uploader_type" \
    --argjson integration_id "$integration_id" \
    '. + [{
      id:$id,
      name:$name,
      state:"uploaded",
      digest:$digest,
      size:$size,
      uploader:{
        login:$uploader_login,
        id:$uploader_id,
        type:$uploader_type
      },
      performed_via_github_app:(
        if $integration_id == null then null
        else {id:$integration_id}
        end
      )
    }]' \
    "$assets" >"$assets.tmp"
  mv "$assets.tmp" "$assets"
  cp "$path" "$state/asset-content/$next_id"
  count=$(<"$uploads")
  printf '%s\n' "$((count + 1))" >"$uploads"
  exit 0
fi

echo "Unsupported fake gh command: $*" >&2
exit 2
