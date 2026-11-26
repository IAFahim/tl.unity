#!/usr/bin/env bash
set -uo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
unity="${UNITY:-$(python3 -c "import sys; sys.path.insert(0, '$root/eng'); import unity_editor as u; f = u.installed(); print(f[max(f, key=u.order)].as_posix() if f else '')")}"
[ -x "$unity" ] || { echo "no installed Unity editor; set UNITY" >&2; exit 1; }
out="$root/artifacts/worker-scaling"
probe="$out/probe"
mkdir -p "$out"
if [ ! -f "$probe/ProjectSettings/ProjectVersion.txt" ]; then
  rm -rf "$probe"
  "$unity" -batchmode -nographics -quit -createProject "$probe" -logFile "$out/create.log" || { echo "project creation failed" >&2; exit 1; }
fi
platform=""
if [ "$(uname -s)" = Linux ]; then
  platform=$',\n    "com.unity.sdk.linux-x86_64": "1.1.1",\n    "com.unity.toolchain.linux-x86_64-linux": "1.1.1"'
fi
cat > "$probe/Packages/manifest.json" <<JSON
{
  "dependencies": {
    "com.tl.unity": "file:$root/com.tl.unity",
    "com.unity.burst": "1.8.25",
    "com.unity.collections": "6.7.0",
    "com.unity.entities": "6.7.0",
    "com.unity.test-framework": "1.6.0",
    "com.unity.test-framework.performance": "6.7.0"$platform
  },
  "testables": ["com.tl.unity"]
}
JSON
"$unity" -batchmode -nographics -quit -projectPath "$probe" -logFile "$out/import.log" || { grep -E 'error CS' "$out/import.log" | sort -u | head -20; exit 1; }
shapes="${TL_SCALING_SHAPES:-20000x200}"
durations="${TL_SCALING_DURATIONS:-120,3000}"
modes="${TL_SCALING_MODES:-eager,lazy}"
counts="${TL_SCALING_WORKERS:-1 2 4 8 $(( $(nproc) - 1 ))}"
runs="${TL_SCALING_RUNS:-3}"
bench="$out/bench.log"
rm -f "$bench"
for run in $(seq 1 "$runs"); do
  for n in $counts; do
    echo "TlOwnerBench: arm run=$run requestedWorkers=$n" >> "$bench"
    "$unity" -batchmode -nographics -quit -projectPath "$probe" -job-worker-count "$n" -executeMethod KindGame.OwnerScaleBench.RunBatch -ownerLog "$bench" -ownerShapes "$shapes" -ownerDurations "$durations" -ownerModes "$modes" -ownerMeasure 256 -logFile "$out/run-$run-$n.log" || { echo "bench run $run workers $n failed" >&2; tail -40 "$out/run-$run-$n.log" >&2; exit 1; }
  done
done
cat "$bench"
