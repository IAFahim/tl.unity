using System.Text;
using Tl.Unity;
using Unity.Entities;
using UnityEditor;
using UnityEngine;

namespace Tl.Tools
{
    public static class KindReportMenu
    {
        [MenuItem("Tools/Tl/Kind Report")]
        public static void CaptureAndLog()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
            {
                Debug.LogWarning("Tl Kind Report: no default world.");
                return;
            }
            var rows = KindReport.CaptureRows(world, KindRegistry.Discover());
            var warnings = KindReport.Analyze(rows);
            var text = new StringBuilder();
            text.AppendLine("Tl Kind Report: " + rows.Count + " archetypes, " + warnings.Count + " warnings.");
            foreach (var row in rows)
                text.AppendLine(row.Archetype + ": entities/chunk=" + row.Entities + " kinds=" + row.Kinds + " kindBytes=" + row.KindBytesPerEntity);
            foreach (var warning in warnings)
                text.AppendLine(warning.Code + " " + warning.Archetype + ": " + warning.Detail);
            if (warnings.Count > 0)
                Debug.LogWarning(text.ToString());
            else
                Debug.Log(text.ToString());
        }
    }
}
