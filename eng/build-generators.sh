#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
tools="$root/com.tl.unity/Tools~"
stamp="$tools/generators.stamp"
case "$(uname -s)" in MINGW* | MSYS* | CYGWIN*) windows=1 ;; *) windows=0 ;; esac
native() { if [ "$windows" = 1 ]; then cygpath -m "$1"; else printf '%s' "$1"; fi; }
[ -f "$root/external/tl-kinds/src/Tl.Gen.CSharp/Tl.Gen.CSharp.csproj" ] || { echo "external/tl-kinds is empty: git submodule update --init external/tl-kinds" >&2; exit 1; }
commit="$(git -C "$root/external/tl-kinds" rev-parse HEAD)"
projects=("Tl.Gen.Unity:Runtime" "Tl.Gen.Plain:Plain")

sources_hash() {
  {
    printf 'tl-kinds %s\n' "$commit"
    cd "$tools"
    for pair in "${projects[@]}"; do
      find "${pair%%:*}" -type f \( -name '*.cs' -o -name '*.csproj' \) -not -path '*/obj/*' -not -path '*/bin/*'
    done | LC_ALL=C sort | while IFS= read -r file; do
      printf 'file %s\n' "$file"
      tr -d '\r' <"$file"
    done
  } | git hash-object --stdin
}

dll_path() { case "${1##*:}" in Runtime) printf '%s/com.tl.unity/Runtime/%s.dll' "$root" "${1%%:*}" ;; *) printf '%s/com.tl.unity/Tests/Plain/Generator/%s.dll' "$root" "${1%%:*}" ;; esac; }

stamp_text() {
  printf 'tl-kinds=%s\n' "$commit"
  printf 'sources=%s\n' "$(sources_hash)"
  for pair in "${projects[@]}"; do
    printf '%s.dll=%s\n' "${pair%%:*}" "$(git hash-object --no-filters "$(dll_path "$pair")")"
  done
}

if [ "${1:-}" = "--check" ]; then
  [ -f "$stamp" ] || { echo "com.tl.unity/Tools~/generators.stamp is missing: run eng/build-generators.sh" >&2; exit 1; }
  expected="$(tr -d '\r' <"$stamp")"
  actual="$(stamp_text)"
  if [ "$expected" = "$actual" ]; then
    echo "generators match tl@${commit:0:12}"
    exit 0
  fi
  diff <(printf '%s\n' "$expected") <(printf '%s\n' "$actual") >&2 || true
  echo "com.tl.unity/Generators dlls are stale against the generator sources or tl@${commit:0:12}: run eng/build-generators.sh" >&2
  exit 1
fi

stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
for pair in "${projects[@]}"; do
  project="${pair%%:*}"; folder="${pair##*:}"
  out="$stage/$project/"
  (cd "$root/external/tl-kinds" && dotnet build "$(native "$tools/$project/$project.csproj")" -c Release --nologo -v q --no-incremental \
    -p:Deterministic=true -p:ContinuousIntegrationBuild=true -p:DebugType=none -p:DebugSymbols=false \
    -p:GenerateDocumentationFile=false -p:PathMap="$(native "$root")=/_/" -p:OutDir="$(native "$out")") >"$stage/$project.log" 2>&1 \
    || { cat "$stage/$project.log" >&2; exit 1; }
  if grep -q 'warning ' "$stage/$project.log"; then grep 'warning ' "$stage/$project.log" | sort -u >&2; echo "$project built with warnings" >&2; exit 1; fi
  mkdir -p "$(dirname "$(dll_path "$pair")")"
  cp "$out$project.dll" "$(dll_path "$pair")"
  echo "$project.dll built from tl@${commit:0:12}"
done
stamp_text >"$stamp"
echo "generators.stamp written"
