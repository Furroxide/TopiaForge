#!/usr/bin/env bash
set -euo pipefail
[[ $# == 2 ]] || { echo 'Usage: release-asset-policy.sh <repository-root> <version>' >&2; exit 64; }
root=$1
version=$2
for contract in release/release-policy.json release/catalog.json; do
  [[ -f $root/$contract && ! -L $root/$contract ]] || {
    echo "Release asset contract is missing or unsafe: $contract" >&2; exit 1;
  }
done
jq -en --arg version "$version" \
  --slurpfile policy "$root/release/release-policy.json" \
  --slurpfile catalog "$root/release/catalog.json" '
  def names: type == "array" and length > 0 and length < 4096 and
    all(.[]; type == "string" and test("^[A-Za-z0-9][A-Za-z0-9._+-]*$")) and
    length == (unique | length);
  [$catalog[0].releases[] | select(.version == $version)] |
  select(length == 1) | .[0].artifacts as $payloads |
  $policy[0].artifactPolicy.generatedMetadata as $generated |
  $policy[0].artifactPolicy.platformArchives as $archives |
  ($policy[0].signingIdentities.windowsDistribution // "signed") as $mode |
  if (($payloads | names) and ($generated | names) and ($archives | names) and
      ($mode == "signed" or $mode == "unsigned")) then
    (["release-handoff-v1.json", "release-candidate-readiness-v1.json",
      "release-candidate-acceptance-v1.json"] +
     (if $mode == "signed" then ["release-handoff-v1.json.p7s"] else [] end) +
     [$archives[] | "release-platform-bundle-v1-" +
       (ltrimstr("TopiaForge-") | rtrimstr(".zip")) + ".json"] + $payloads) as $human |
    if (($human | names) and
        all($human[]; . as $name | ($generated | index($name)) == null)) then
      {human: ($human | sort), generated: ($generated | sort),
       all: (($human + $generated) | sort)}
    else error("Human qualification/payload assets cannot be generated metadata") end
  else error("Invalid release artifact inventory") end'
