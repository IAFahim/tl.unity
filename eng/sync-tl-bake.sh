#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
src="$root/external/tl-kinds/tools/Tl.Gen.Tlb"
dst="$root/com.tl.unity/Editor/Bake/Tlb"
stamp="$root/com.tl.unity/Tools~/tl-bake.stamp"
skip=" BakeCacheKey.cs TlbInspection.cs TlbReport.cs "
[ -f "$src/Tl.Gen.Tlb.csproj" ] || { echo "external/tl-kinds is empty: git submodule update --init external/tl-kinds" >&2; exit 1; }
commit="$(git -C "$root/external/tl-kinds" rev-parse HEAD)"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
for file in "$src"/*.cs; do
  name="$(basename "$file")"
  case "$skip" in *" $name "*) continue ;; esac
  tr -d '\r' <"$file" >"$stage/$name"
done
cat > "$stage/GlobalUsings.cs" <<'CS'
global using global::System;
global using global::System.Collections.Generic;
global using global::System.IO;
global using global::System.Linq;
global using global::System.Threading;
global using global::System.Threading.Tasks;
CS
cat > "$stage/README.md" <<MD
# Tl.Gen.Tlb (generated)

Copied from [IAFahim/tl@${commit:0:12}](https://github.com/IAFahim/tl/tree/${commit}/tools/Tl.Gen.Tlb) \`tools/Tl.Gen.Tlb\` by \`eng/sync-tl-bake.sh\`,
without the CLI-only files${skip% }.
Do not edit here: change tl, move the \`external/tl-kinds\` submodule, and run the script again.
MD
stamp_text() {
  printf 'tl-kinds=%s\n' "$commit"
  printf 'sources=%s\n' "$(cd "$stage" && LC_ALL=C ls | while IFS= read -r file; do printf 'file %s\n' "$file"; cat "$file"; done | git hash-object --stdin)"
}
if [ "${1:-}" = "--check" ]; then
  status=0
  if ! diff -r -q --strip-trailing-cr -x '*.meta' "$stage" "$dst" >/dev/null 2>&1; then
    diff -r -q --strip-trailing-cr -x '*.meta' "$stage" "$dst" >&2 || true
    echo "com.tl.unity/Editor/Bake/Tlb is stale against tl@${commit:0:12}: run eng/sync-tl-bake.sh" >&2
    status=1
  fi
  if [ ! -f "$stamp" ] || [ "$(tr -d '\r' <"$stamp")" != "$(stamp_text)" ]; then
    echo "com.tl.unity/Tools~/tl-bake.stamp is stale against tl@${commit:0:12}: run eng/sync-tl-bake.sh" >&2
    status=1
  fi
  [ "$status" = 0 ] && echo "com.tl.unity/Editor/Bake/Tlb matches tl@${commit:0:12}"
  exit $status
fi
mkdir -p "$dst"
for file in "$dst"/*; do
  [ -e "$file" ] || continue
  name="$(basename "$file")"
  case "$name" in *.meta) continue ;; esac
  [ -e "$stage/$name" ] || rm -f "$file" "$file.meta"
done
cp "$stage"/* "$dst"/
stamp_text >"$stamp"
echo "com.tl.unity/Editor/Bake/Tlb synced to tl@${commit:0:12}"
