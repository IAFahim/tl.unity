#!/usr/bin/env bash
# usage: eng/check-samples-together.sh <URP project that lists com.tl.unity and com.unity.test-framework, without testables>
# Imports every com.tl.unity sample into the project, builds the Force demo scenes, runs the PlayMode tests and checks that the
# Force demo still pushes the cube 4.0333 m and that no second timeline library appears (IAFahim/tl.unity#23).
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
        var builder = Type.GetType("ForceDemoBuilder, Assembly-CSharp-Editor") ?? throw new InvalidOperationException("ForceDemoBuilder did not compile");
        builder.GetMethod("Build").Invoke(null, null);
        builder.GetMethod("BuildControl").Invoke(null, null);
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
line="$(grep -o 'ForceDemo result .*' "$out/play.log" | tail -n 1)"
moved="$(printf '%s' "$line" | sed -n 's/.*movedX=\([^ ]*\).*/\1/p')"
clash="$(grep -c 'already has a timeline library' "$out/play.log" || true)"
run="$(grep -o '<test-run [^>]*>' "$out/play.xml" | head -n 1)"
failed="$(printf '%s' "$run" | sed -n 's/.* failed="\([0-9]*\)".*/\1/p')"
passed="$(printf '%s' "$run" | sed -n 's/.* passed="\([0-9]*\)".*/\1/p')"
echo "samples imported=$imported errorCS=$errors forceMovedX=${moved:-none} libraryClash=$clash playmode passed=${passed:-0} failed=${failed:-?}"
[ "$imported" -ge 3 ] && [ "$errors" -eq 0 ] && [ "$moved" = 4.0333 ] && [ "$clash" -eq 0 ] && [ "${failed:-1}" = 0 ] || status=1
exit $status
