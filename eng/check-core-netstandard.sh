#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
core="$root/com.tl.unity/Core"
case "$(uname -s)" in MINGW* | MSYS* | CYGWIN*) windows=1 ;; *) windows=0 ;; esac
native() { if [ "$windows" = 1 ]; then cygpath -m "$1"; else printf '%s' "$1"; fi; }
found="$(find "/c/Program Files/Unity/Hub/Editor" -maxdepth 3 -name Unity.exe -print 2>/dev/null | head -1 || true)"
if [ -z "$found" ]; then
  found="$(find ~/Unity/Hub/Editor -maxdepth 3 -name Unity -print 2>/dev/null | head -1 || true)"
fi
found="${found%/Unity.exe}"; found="${found%/Unity}"
if [ -z "$found" ]; then
  found="$(find "/Applications/Unity/Hub/Editor" -maxdepth 3 -name Unity -print 2>/dev/null | head -1 || true)"
  found="${found%/Unity}"
fi
[ -n "$found" ] || { echo "no Unity editor found" >&2; exit 1; }
csc="$(find "$found/Data/DotNetSdk/sdk" -name csc.dll -print 2>/dev/null | sort | tail -1)"
[ -n "$csc" ] || { echo "Unity Roslyn csc.dll not found under $found" >&2; exit 1; }
netstandard="$(find "$found/Data/NetStandard/ref/2.1.0" -maxdepth 1 -name netstandard.dll -print 2>/dev/null | head -1)"
[ -n "$netstandard" ] || { echo "netstandard 2.1 ref assembly not found under $found" >&2; exit 1; }
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
rsp="$stage/core.rsp"
{
  printf '%s\n' "-nologo" "-target:library" "-unsafe" "-langversion:latest" "-nullable:enable" "-nowarn:UAL0010,UAL0013,UAC0005"
  printf -- '-out:"%s"\n' "$(native "$stage/Tl.Core.dll")"
  printf -- '-r:"%s"\n' "$(native "$netstandard")"
  find "$core" -maxdepth 1 -name '*.cs' -print | sort | while IFS= read -r file; do
    printf '%s\n' "$(native "$file")"
  done
} >"$rsp"
dotnetbin="$found/Data/DotNetSdk/dotnet.exe"
[ -x "$dotnetbin" ] || dotnetbin="$found/Data/DotNetSdk/dotnet"
"$dotnetbin" "$(native "$csc")" "@$(native "$rsp")" 2>&1 | sed 's/^/compile /' >"$stage/log" || { cat "$stage/log"; exit 1; }
grep -q 'error CS' "$stage/log" && { cat "$stage/log"; exit 1; }
printf 'Tl.Core compiles for netstandard2.1 at langversion:latest (%s files)\n' "$(find "$core" -maxdepth 1 -name '*.cs' | wc -l)"
