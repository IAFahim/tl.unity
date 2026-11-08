#!/usr/bin/env bash
# usage: [TL_UNITY_PACKS=1] eng/check-samples-together.sh <URP project that lists com.tl.unity and com.unity.test-framework, without testables>
# Imports every com.tl.unity sample into the project, builds every demo scene and the hub, runs the PlayMode tests and checks the
# demo numbers (Force 4.0333 m, Events 15 strikes and health 10, Crowd two rows at 0 B) and that no second timeline library appears.
# TL_UNITY_PACKS=1 first adds every pack under external/tl-kinds/packs to the project (the Force pack next to the Force demo's own
# ForceClip) and also checks the Motion (0 mismatches), Stats and Inventory (final bag, pickups at ticks 420 and 480) demos.
set -uo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
case "$(uname -s)" in MINGW* | MSYS* | CYGWIN*) windows=1 ;; *) windows=0 ;; esac
native() { if [ "$windows" = 1 ]; then cygpath -m "$1"; else printf '%s' "$1"; fi; }
project="$(cd "${1:?usage: eng/check-samples-together.sh <project>}" && pwd)"
unity="${UNITY:-$(python3 "$root/eng/unity_editor.py" editor)}"
packs="${TL_UNITY_PACKS:-0}"
out="$root/artifacts/samples-together"
mkdir -p "$out" "$project/Assets/TlCheck/Editor"
if [ "$packs" = 1 ]; then
  for pack in "$root"/external/tl-kinds/packs/*/package.json; do
    python3 - "$project/Packages/manifest.json" "$(sed -n 's/.*"name": *"\([^"]*\)".*/\1/p' "$pack" | head -1)" "file:$(native "$(dirname "$pack")")" <<'PY'
import json, sys
path, name, source = sys.argv[1:]
manifest = json.load(open(path, encoding="utf-8"))
manifest["dependencies"][name] = source
json.dump(manifest, open(path, "w", encoding="utf-8"), indent=2)
PY
  done
fi
helper="$project/Assets/TlCheck/Editor/TlCheckSamples.cs"
cat > "$helper" <<'CS'
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager.UI;
using UnityEngine;

public static class TlCheckSamples
{
    public static void Import()
    {
        foreach (var sample in Sample.FindByPackage("com.tl.unity", null))
        {
            sample.Import(Sample.ImportOptions.OverridePreviousImports);
            Debug.Log("TL-CHECK imported " + sample.displayName);
        }
        AssetDatabase.Refresh();
    }

    public static void Build()
    {
        var builders = "ForceDemoBuilder|Build;ForceDemoBuilder|BuildControl;EventsDemoBuilder|Build;CrowdDemoBuilder|Build";
        if (Environment.GetEnvironmentVariable("TL_UNITY_PACKS") == "1")
            builders += ";MotionDemoBuilder|Build;StatsDemoBuilder|Build;InventoryDemoBuilder|Build";
        foreach (var name in (builders + ";DemoHubBuilder|Build").Split(';'))
        {
            var parts = name.Split('|');
            var builder = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(parts[0])).FirstOrDefault(t => t != null) ?? throw new InvalidOperationException(parts[0] + " did not compile");
            builder.GetMethod(parts[1]).Invoke(null, null);
        }
        Debug.Log("TL-CHECK built");
    }
}
CS
status=0
"$unity" -batchmode -nographics -quit -projectPath "$(native "$project")" -executeMethod TlCheckSamples.Import -logFile "$(native "$out/import.log")"
TL_UNITY_PACKS="$packs" "$unity" -batchmode -nographics -quit -projectPath "$(native "$project")" -executeMethod TlCheckSamples.Build -logFile "$(native "$out/build.log")"
rm -rf "$project/Assets/TlCheck" "$project/Assets/TlCheck.meta"
"$unity" -batchmode -nographics -projectPath "$(native "$project")" -runTests -testPlatform PlayMode -testResults "$(native "$out/play.xml")" -logFile "$(native "$out/play.log")"
imported="$(grep -c 'TL-CHECK imported' "$out/import.log" || true)"
built="$(grep -c 'TL-CHECK built' "$out/build.log" || true)"
errors="$(cat "$out/import.log" "$out/build.log" "$out/play.log" | grep -c 'error CS' || true)"
line="$(grep -o 'TL-DEMO force .*' "$out/play.log" | tail -n 1)"
moved="$(printf '%s' "$line" | sed -n 's/.* x=\([^ ]*\).*/\1/p')"
events="$(grep -o 'TL-DEMO events strikes=[0-9]* health=[0-9-]*' "$out/play.log" | tail -n 1)"
crowd="$(grep -c 'TL-BENCH crowd .*warmB=0 ' "$out/play.log" || true)"
clash="$(grep -c 'already has a timeline library' "$out/play.log" || true)"
run="$(grep -o '<test-run [^>]*>' "$out/play.xml" | head -n 1)"
failed="$(printf '%s' "$run" | sed -n 's/.* failed="\([0-9]*\)".*/\1/p')"
passed="$(printf '%s' "$run" | sed -n 's/.* passed="\([0-9]*\)".*/\1/p')"
extra=""
if [ "$packs" = 1 ]; then
  motion="$(grep -o 'TL-DEMO motion samples=[0-9]* mismatches=[0-9]*' "$out/play.log" | tail -n 1)"
  stats="$(grep -o 'TL-DEMO stats health200=64/20 health450=53/20 health600=72/10' "$out/play.log" | tail -n 1)"
  inventory="$(grep -o 'TL-DEMO inventory final=Wood_1_Stone_3_Plank_2_Potion_1_Sword_1_Coin_10_Key_0_Gem_1 coinAt=420 gemAt=480' "$out/play.log" | tail -n 1)"
  extra=" ${motion:-motion=none} ${stats:-stats=none} ${inventory:-inventory=none}"
  printf '%s' "$motion" | grep -q ' mismatches=0$' && [ -n "$stats" ] && [ -n "$inventory" ] || status=1
fi
echo "samples packs=$packs imported=$imported built=$built errorCS=$errors forceX=${moved:-none} ${events:-events=none} crowdRowsAt0B=$crowd libraryClash=$clash$extra playmode passed=${passed:-0} failed=${failed:-?}"
[ "$imported" -ge 6 ] && [ "$built" = 1 ] && [ "$errors" -eq 0 ] && [ "$moved" = 4.0333 ] && [ "$events" = "TL-DEMO events strikes=15 health=10" ] && [ "$crowd" -eq 2 ] && [ "$clash" -eq 0 ] && [ "${failed:-1}" = 0 ] || status=1
exit $status
