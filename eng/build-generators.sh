#!/usr/bin/env bash
# usage: eng/build-generators.sh [--check]
# Builds the Roslyn source generators in com.tl.unity/Tools~ from the pinned external/tl-kinds sources
# and copies them into com.tl.unity/Generators, where Unity scopes each one to the assemblies that
# reference its marker assembly. The build is deterministic; --check fails if a shipped dll is stale.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
case "$(uname -s)" in MINGW* | MSYS* | CYGWIN*) windows=1 ;; *) windows=0 ;; esac
native() { if [ "$windows" = 1 ]; then cygpath -m "$1"; else printf '%s' "$1"; fi; }
[ -f "$root/external/tl-kinds/src/Tl.Gen.CSharp/Tl.Gen.CSharp.csproj" ] || { echo "external/tl-kinds is empty: git submodule update --init external/tl-kinds" >&2; exit 1; }
commit="$(git -C "$root/external/tl-kinds" rev-parse HEAD)"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
status=0
for pair in "Tl.Gen.Unity:Ecs" "Tl.Gen.Plain:Plain"; do
  project="${pair%%:*}"; folder="${pair##*:}"
  out="$stage/$project/"
  (cd "$root/external/tl-kinds" && dotnet build "$(native "$root/com.tl.unity/Tools~/$project/$project.csproj")" -c Release --nologo -v q --no-incremental \
    -p:Deterministic=true -p:ContinuousIntegrationBuild=true -p:DebugType=none -p:DebugSymbols=false \
    -p:GenerateDocumentationFile=false -p:PathMap="$(native "$root")=/_/" -p:OutDir="$(native "$out")") >"$stage/$project.log" 2>&1 \
    || { cat "$stage/$project.log" >&2; exit 1; }
  if grep -q 'warning ' "$stage/$project.log"; then grep 'warning ' "$stage/$project.log" | sort -u >&2; echo "$project built with warnings" >&2; exit 1; fi
  built="$out$project.dll"
  shipped="$root/com.tl.unity/Generators/$folder/$project.dll"
  if [ "${1:-}" = "--check" ]; then
    if cmp -s "$built" "$shipped"; then echo "$project.dll matches tl@${commit:0:12}"; else echo "com.tl.unity/Generators/$folder/$project.dll is stale against tl@${commit:0:12}: run eng/build-generators.sh" >&2; status=1; fi
  else
    mkdir -p "$(dirname "$shipped")"
    cp "$built" "$shipped"
    echo "$project.dll built from tl@${commit:0:12}"
  fi
done
exit $status
