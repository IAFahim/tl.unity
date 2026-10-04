#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
mkdir -p artifacts/packages artifacts/tests
dotnet build external/v3/src/Tl.Gen.CSharp -c Release -f net10.0
dotnet build external/v3/tools/Tl.Bake -c Release
dotnet build tests/V3.Oracle -c Release
dotnet build tests/Profile.Oracle -c Release
python3 eng/prepare-v3.py
dotnet pack src/Tl.Runtime.Unity -c Release -o artifacts/packages
dotnet pack samples/Game.Domain -c Release -o artifacts/packages
dotnet tool restore
python3 eng/restore-v3.py
if [ "${1:-dotnet}" = unity ]; then
  for mode in EditMode PlayMode; do
    DISPLAY=:0 unity test "$root/tests/Tl.V3.Project" --mode "$mode" --output "$root/artifacts/tests/v3-$mode.xml" --timeout 600 -- -logFile "$root/artifacts/v3-$mode.log"
    python3 - "$root/artifacts/tests/v3-$mode.xml" <<'PY'
import sys,xml.etree.ElementTree as E
r=E.parse(sys.argv[1]).getroot()
assert int(r.get('total','0'))>=8 and r.get('failed')=='0' and r.get('skipped')=='0',r.attrib
print(r.get('passed')+' native Unity tests passed')
PY
  done
fi
