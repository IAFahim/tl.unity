#!/usr/bin/env bash
# usage: eng/check-samples-together.sh <URP project that lists com.tl.unity and com.unity.test-framework, without testables>
# Imports every com.tl.unity sample into the project, builds every demo scene and the hub, runs the PlayMode tests and checks the
# demo numbers (Force 4.0333 m, Events 15 strikes and health 10, Crowd two rows at 0 B) and that no second timeline library appears.
set -uo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
case "$(uname -s)" in MINGW* | MSYS* | CYGWIN*) windows=1 ;; *) windows=0 ;; esac
native() { if [ "$windows" = 1 ]; then cygpath -m "$1"; else printf '%s' "$1"; fi; }
project="$(cd "${1:?usage: eng/check-samples-together.sh <project>}" && pwd)"
unity="${UNITY:-$(python3 "$root/eng/unity_editor.py" editor)}"
out="$root/artifacts/samples-together"
mkdir -p "$out" "$project/Assets/TlCheck/Editor"
helper="$project/Assets/TlCheck/Editor/TlCheckSamples.cs"
cat > "$helper" <<'CS'
using System;
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
        foreach (var name in new[] { "ForceDemoBuilder, Assembly-CSharp-Editor|Build", "ForceDemoBuilder, Assembly-CSharp-Editor|BuildControl", "EventsDemoBuilder, Tl.Demo.Events.Editor|Build", "CrowdDemoBuilder, Tl.Demo.Crowd.Editor|Build", "DemoHubBuilder, Tl.Demo.Hub.Editor|Build" })
        {
            var parts = name.Split('|');
            var builder = Type.GetType(parts[0]) ?? throw new InvalidOperationException(parts[0] + " did not compile");
            builder.GetMethod(parts[1]).Invoke(null, null);
        }
        Debug.Log("TL-CHECK built");
    }
}
CS
status=0
"$unity" -batchmode -nographics -quit -projectPath "$(native "$project")" -executeMethod TlCheckSamples.Import -logFile "$(native "$out/import.log")"
"$unity" -batchmode -nographics -quit -projectPath "$(native "$project")" -executeMethod TlCheckSamples.Build -logFile "$(native "$out/build.log")"
rm -rf "$project/Assets/TlCheck" "$project/Assets/TlCheck.meta"
"$unity" -batchmode -nographics -projectPath "$(native "$project")" -runTests -testPlatform PlayMode -testResults "$(native "$out/play.xml")" -logFile "$(native "$out/play.log")"
imported="$(grep -c 'TL-CHECK imported' "$out/import.log" || true)"
errors="$(cat "$out/import.log" "$out/build.log" "$out/play.log" | grep -c 'error CS' || true)"
line="$(grep -o 'TL-DEMO force .*' "$out/play.log" | tail -n 1)"
moved="$(printf '%s' "$line" | sed -n 's/.* x=\([^ ]*\).*/\1/p')"
events="$(grep -o 'TL-DEMO events strikes=[0-9]* health=[0-9-]*' "$out/play.log" | tail -n 1)"
crowd="$(grep -c 'TL-BENCH crowd .*warmB=0 ' "$out/play.log" || true)"
clash="$(grep -c 'already has a timeline library' "$out/play.log" || true)"
run="$(grep -o '<test-run [^>]*>' "$out/play.xml" | head -n 1)"
failed="$(printf '%s' "$run" | sed -n 's/.* failed="\([0-9]*\)".*/\1/p')"
passed="$(printf '%s' "$run" | sed -n 's/.* passed="\([0-9]*\)".*/\1/p')"
echo "samples imported=$imported errorCS=$errors forceX=${moved:-none} ${events:-events=none} crowdRowsAt0B=$crowd libraryClash=$clash playmode passed=${passed:-0} failed=${failed:-?}"
[ "$imported" -ge 6 ] && [ "$errors" -eq 0 ] && [ "$moved" = 4.0333 ] && [ "$events" = "TL-DEMO events strikes=15 health=10" ] && [ "$crowd" -eq 2 ] && [ "$clash" -eq 0 ] && [ "${failed:-1}" = 0 ] || status=1
exit $status
