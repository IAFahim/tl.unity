#!/usr/bin/env bash
# usage: eng/sync-tl-core.sh [--check]
# Copies tl's src/Tl.Core from the pinned external/tl-kinds submodule into com.tl.unity/Core,
# so the package carries the exact Tl.Core it compiles against. --check fails if Core is stale.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
src="$root/external/tl-kinds/src/Tl.Core"
dst="$root/com.tl.unity/Core"
[ -f "$src/Tl.Core.csproj" ] || { echo "external/tl-kinds is empty: git submodule update --init external/tl-kinds" >&2; exit 1; }
commit="$(git -C "$root/external/tl-kinds" rev-parse HEAD)"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
cp "$src"/*.cs "$stage"/
cat > "$stage/Tl.Core.asmdef" <<'JSON'
{
  "name": "Tl.Core",
  "rootNamespace": "Tl",
  "allowUnsafeCode": true,
  "autoReferenced": true,
  "noEngineReferences": true
}
JSON
printf '%s\n' '-langversion:latest' '-nullable:enable' '-nowarn:UAL0010,UAL0013,UAC0005' > "$stage/csc.rsp"
cat > "$stage/GlobalUsings.cs" <<'CS'
global using global::System;
global using global::System.Collections.Generic;
global using global::System.IO;
global using global::System.Linq;
global using global::System.Threading;
global using global::System.Threading.Tasks;
CS
cat > "$stage/README.md" <<MD
# Tl.Core (generated)

Copied from [IAFahim/tl@${commit:0:12}](https://github.com/IAFahim/tl/tree/${commit}/src/Tl.Core) \`src/Tl.Core\` by \`eng/sync-tl-core.sh\`.
Do not edit here: change tl, move the \`external/tl-kinds\` submodule, and run the script again.
MD
if [ "${1:-}" = "--check" ]; then
  if diff -r -q -x '*.meta' "$stage" "$dst" >/dev/null 2>&1; then echo "com.tl.unity/Core matches tl@${commit:0:12}"; exit 0; fi
  diff -r -q -x '*.meta' "$stage" "$dst" >&2 || true
  echo "com.tl.unity/Core is stale against tl@${commit:0:12}: run eng/sync-tl-core.sh" >&2
  exit 1
fi
mkdir -p "$dst"
for file in "$dst"/*; do
  name="$(basename "$file")"
  case "$name" in *.meta) continue ;; esac
  [ -e "$stage/$name" ] || rm -f "$file" "$file.meta"
done
cp "$stage"/* "$dst"/
echo "com.tl.unity/Core synced to tl@${commit:0:12}"
