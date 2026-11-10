using Unity.Entities;
using UnityEditor;
using UnityEngine;

namespace Tl.Unity.Editor
{
    public static class KindReportMenu
    {
        [MenuItem("Tools/Tl/Kind Report")]
        public static void Show()
        {
            var registry = TimelineKindSets.Registry;
            var shown = 0;
            foreach (var world in global::Unity.Entities.World.All)
            {
                if (!world.IsCreated || (world.Flags & (WorldFlags.Conversion | WorldFlags.Shadow | WorldFlags.Staging)) != 0)
                    continue;
                var rows = KindReport.Capture(world.EntityManager, registry);
                if (rows.Count == 0)
                    continue;
                shown++;
                var text = KindReport.Text(world.Name, rows);
                if (KindReport.Warnings(rows).Count > 0)
                    Debug.LogWarning(text);
                else
                    Debug.Log(text);
            }
            if (shown == 0)
                Debug.Log("Tl Kind Report: no world has entities with kinds. Open a scene whose subscene bakes Timeline Players, or enter Play mode.");
        }
    }
}
