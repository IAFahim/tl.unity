#!/usr/bin/env bash
# usage: eng/test-com-tl-unity.sh [EditMode] [PlayMode]
# Tests com.tl.unity the way a user installs it: a Unity project whose only Tl dependency is this package.
# Runs on Linux and on Windows under Git Bash.
# env: UNITY (editor binary; default: TL_UNITY_VERSION's editor, else the newest installed editor),
#      TL_UNITY_VERSION (pick one installed editor), TL_UNITY_PROBE (reused project dir),
#      TL_TLB (tlb or Tl.Bake.dll with the kinds command; the capacity bake test is skipped without it)
set -uo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
case "$(uname -s)" in MINGW* | MSYS* | CYGWIN*) windows=1 ;; *) windows=0 ;; esac
native() { if [ "$windows" = 1 ]; then cygpath -m "$1"; else printf '%s' "$1"; fi; }
if [ "$windows" = 1 ]; then
  hub="${UNITY_HUB_EDITORS:-${PROGRAMFILES:-C:/Program Files}/Unity/Hub/Editor}"
  exe="Editor/Unity.exe"
else
  hub="${UNITY_HUB_EDITORS:-$HOME/Unity/Hub/Editor}"
  exe="Editor/Unity"
fi
unity="${UNITY:-}"
if [ -z "$unity" ]; then
  version="${TL_UNITY_VERSION:-$(ls -1 "$hub" 2>/dev/null | sort -V | tail -n 1)}"
  [ -n "$version" ] || { echo "no Unity editor under $hub; set UNITY" >&2; exit 1; }
  unity="$hub/$version/$exe"
fi
[ -x "$unity" ] || { echo "no Unity editor at $unity; set UNITY" >&2; exit 1; }
version="$(basename "$(dirname "$(dirname "$unity")")")"
probe="${TL_UNITY_PROBE:-${XDG_CACHE_HOME:-$HOME/.cache}/tl-unity-probe-$version}"
out="$root/artifacts/com-tl-unity"
bash "$root/eng/sync-tl-core.sh" --check || exit 1
if [ "$windows" = 1 ]; then
  open="$(powershell.exe -NoProfile -Command "Get-CimInstance Win32_Process -Filter \"Name='Unity.exe'\" | ForEach-Object { \$_.CommandLine }" | tr -d '\r' | grep -F -i -- "$(native "$probe")" || true)"
else
  open="$(pgrep -af 'Unity' | grep -F -- "-projectPath $probe" || true)"
fi
if [ -n "$open" ]; then echo "an Editor already has $probe open" >&2; exit 1; fi
mkdir -p "$out"
if [ ! -f "$probe/ProjectSettings/ProjectVersion.txt" ]; then
  rm -rf "$probe"
  "$unity" -batchmode -nographics -quit -createProject "$(native "$probe")" -logFile "$(native "$out")/create.log" || { echo "project creation failed: $out/create.log" >&2; exit 1; }
fi
platform=""
if [ "$(uname -s)" = Linux ]; then
  platform=$',\n    "com.unity.sdk.linux-x86_64": "1.1.1",\n    "com.unity.toolchain.linux-x86_64-linux": "1.1.1"'
fi
cat > "$probe/Packages/manifest.json" <<JSON
{
  "dependencies": {
    "com.tl.unity": "file:$(native "$root")/com.tl.unity",
    "com.unity.burst": "1.8.25",
    "com.unity.collections": "6.7.0",
    "com.unity.entities": "6.7.0",
    "com.unity.test-framework": "1.6.0",
    "com.unity.test-framework.performance": "6.7.0"$platform
  },
  "testables": ["com.tl.unity"]
}
JSON
[ "$#" -gt 0 ] || set -- EditMode PlayMode
status=0
for mode in "$@"; do
  xml="$out/$mode.xml"; log="$out/$mode.log"; rm -f "$xml"
  "$unity" -batchmode -nographics -projectPath "$(native "$probe")" -runTests -testPlatform "$mode" -testResults "$(native "$xml")" -logFile "$(native "$log")"
  code=$?
  errors="$(grep -c 'error CS' "$log" 2>/dev/null || true)"
  run="$(grep -m1 -o '<test-run [^>]*>' "$xml" 2>/dev/null || true)"
  attr() { local value; value="$(printf '%s' "$run" | sed -n "s/.* $1=\"\([0-9]*\)\".*/\1/p")"; echo "${value:-0}"; }
  total="$(attr total)"; passed="$(attr passed)"; failed="$(attr failed)"; skipped="$(attr skipped)"
  echo "$mode exit=$code errorCS=${errors:-0} total=$total passed=$passed failed=$failed skipped=$skipped"
  if [ "$code" -ne 0 ] || [ "${errors:-0}" -ne 0 ] || [ "$total" -eq 0 ] || [ "$failed" -ne 0 ]; then
    status=1
    grep -E 'error CS|Failed|Exception' "$log" | sort -u | head -20 >&2
  fi
done
exit $status
