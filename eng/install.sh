#!/usr/bin/env bash
set -euo pipefail

repo="${TL_REPO:-IAFahim/tl.unity}"
project="$PWD"
tag=""
from=""
while [ $# -gt 0 ]; do
  case "$1" in
    --project) project="$2"; shift 2 ;;
    --tag) tag="$2"; shift 2 ;;
    --from) from="$2"; shift 2 ;;
    -h|--help) echo "usage: install.sh [--project <unity project>] [--tag <release tag>] [--from <dir with release assets>]"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

fail() { echo "tl install: $*" >&2; exit 1; }
for tool in curl unzip sha256sum; do command -v "$tool" >/dev/null || fail "$tool is required"; done
[ -f "$project/ProjectSettings/ProjectVersion.txt" ] || fail "$project is not a Unity project (no ProjectSettings/ProjectVersion.txt)"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

if [ -n "$from" ]; then
  cp "$from"/Tl.Runtime.Unity.*.nupkg "$from"/SHA256SUMS "$work"/
  tag="local"
else
  if [ -n "$tag" ]; then api="https://api.github.com/repos/$repo/releases/tags/$tag"; else api="https://api.github.com/repos/$repo/releases?per_page=1"; fi
  curl -fsSL -H "Accept: application/vnd.github+json" "$api" -o "$work/release.json" || fail "no release found at $api"
  tag="$(sed -n 's/.*"tag_name": *"\([^"]*\)".*/\1/p' "$work/release.json" | head -n1)"
  [ -n "$tag" ] || fail "release response has no tag_name"
  base="https://github.com/$repo/releases/download/$tag"
  name="$(sed -n "s|.*releases/download/$tag/\(Tl\.Runtime\.Unity\.[^\"]*\.nupkg\)\".*|\1|p" "$work/release.json" | head -n1)"
  [ -n "$name" ] || fail "release $tag has no Tl.Runtime.Unity nupkg asset"
  curl -fsSL "$base/$name" -o "$work/$name"
  curl -fsSL "$base/SHA256SUMS" -o "$work/SHA256SUMS"
fi

package="$(basename "$(ls "$work"/Tl.Runtime.Unity.*.nupkg)")"
(cd "$work" && grep " $package\$" SHA256SUMS | sha256sum -c -) || fail "checksum mismatch for $package"

unzip -q -o "$work/$package" 'lib/netstandard2.1/Tl.Core.dll' -d "$work/x"
target="$project/Assets/Plugins/Tl"
mkdir -p "$target"
cp "$work/x/lib/netstandard2.1/Tl.Core.dll" "$target/Tl.Core.dll"
version="${package#Tl.Runtime.Unity.}"
version="${version%.nupkg}"
sha="$(sha256sum "$target/Tl.Core.dll" | cut -d' ' -f1)"
printf '{"package":"Tl.Runtime.Unity","version":"%s","release":"%s","source":"%s","sha256":"%s"}\n' "$version" "$tag" "$repo" "$sha" > "$target/tl-install.json"
echo "installed Tl.Runtime.Unity $version (release $tag) into $target"
echo "reopen or refocus the Unity Editor to compile; reference Tl.Core.dll from asmdefs with overrideReferences."
