#!/usr/bin/env bash
# usage: eng/test-com-tl-unity.sh [EditMode] [PlayMode]
# Tests com.tl.unity the way a user installs it: a Unity project whose only Tl dependency is this package.
# env: UNITY (editor binary), TL_UNITY_VERSION (default 7000.0.0a12), TL_UNITY_PROBE (reused project dir),
#      TL_TLB (tlb or Tl.Bake.dll with the kinds command; the capacity bake test is skipped without it)
set -uo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
version="${TL_UNITY_VERSION:-7000.0.0a12}"
unity="${UNITY:-$HOME/Unity/Hub/Editor/$version/Editor/Unity}"
probe="${TL_UNITY_PROBE:-${XDG_CACHE_HOME:-$HOME/.cache}/tl-unity-probe-$version}"
out="$root/artifacts/com-tl-unity"
[ -x "$unity" ] || { echo "no Unity $version at $unity; set UNITY" >&2; exit 1; }
bash "$root/eng/sync-tl-core.sh" --check || exit 1
if pgrep -af 'Unity' | grep -F -- "-projectPath $probe" >/dev/null; then echo "an Editor already has $probe open" >&2; exit 1; fi
mkdir -p "$out"
if [ ! -f "$probe/ProjectSettings/ProjectVersion.txt" ]; then
  rm -rf "$probe"
  "$unity" -batchmode -nographics -quit -createProject "$probe" -logFile "$out/create.log" || { echo "project creation failed: $out/create.log" >&2; exit 1; }
fi
cat > "$probe/Packages/manifest.json" <<JSON
{
  "dependencies": {
    "com.tl.unity": "file:$root/com.tl.unity",
    "com.unity.burst": "1.8.25",
    "com.unity.collections": "6.7.0",
    "com.unity.entities": "6.7.0",
    "com.unity.test-framework": "1.6.0",
    "com.unity.test-framework.performance": "6.7.0",
    "com.unity.sdk.linux-x86_64": "1.1.1",
    "com.unity.toolchain.linux-x86_64-linux": "1.1.1"
  },
  "testables": ["com.tl.unity"]
}
JSON
[ "$#" -gt 0 ] || set -- EditMode PlayMode
status=0
for mode in "$@"; do
  xml="$out/$mode.xml"; log="$out/$mode.log"; rm -f "$xml"
  "$unity" -batchmode -nographics -projectPath "$probe" -runTests -testPlatform "$mode" -testResults "$xml" -logFile "$log"
  code=$?
  errors="$(grep -c 'error CS' "$log" 2>/dev/null || true)"
  read -r total passed failed skipped < <(python3 - "$xml" <<'PY'
import sys, xml.etree.ElementTree as ET
try:
    run = ET.parse(sys.argv[1]).getroot()
    print(run.get("total", 0), run.get("passed", 0), run.get("failed", 0), run.get("skipped", 0))
except Exception:
    print(0, 0, 0, 0)
PY
)
  echo "$mode exit=$code errorCS=${errors:-0} total=$total passed=$passed failed=$failed skipped=$skipped"
  if [ "$code" -ne 0 ] || [ "${errors:-0}" -ne 0 ] || [ "$total" -eq 0 ] || [ "$failed" -ne 0 ]; then
    status=1
    grep -E 'error CS|Failed|Exception' "$log" | sort -u | head -20 >&2
  fi
done
exit $status
