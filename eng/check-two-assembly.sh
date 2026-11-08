#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
src="$root/external/tl-kinds/tests/Tl.Systems.Tests/TwoAssembly"
dst="$root/com.tl.unity/Tests/TwoAssembly"
[ -f "$src/PushPack.cs" ] || { echo "external/tl-kinds is empty: git submodule update --init external/tl-kinds" >&2; exit 1; }
status=0
diff -q --strip-trailing-cr "$src/PushPack.cs" "$dst/Pack/PushPack.cs" >/dev/null || { echo "Tests/TwoAssembly/Pack/PushPack.cs differs from tl" >&2; status=1; }
diff -q --strip-trailing-cr "$src/PushGame.cs" "$dst/Game/PushGame.cs" >/dev/null || { echo "Tests/TwoAssembly/Game/PushGame.cs differs from tl" >&2; status=1; }
grep -q "$(base64 -w0 "$src/push.tlib")" "$dst/Tests/PushLibraryBytes.cs" || { echo "Tests/TwoAssembly/Tests/PushLibraryBytes.cs is not tl's push.tlib" >&2; status=1; }
[ "$status" = 0 ] && echo "TwoAssembly sources and library match tl"
exit $status
