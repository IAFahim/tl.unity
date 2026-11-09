using UnityEditor;
using UnityEngine;

namespace Tl.Unity.Editor
{
    public static class TlPanelMenu
    {
        [MenuItem("GameObject/Tl/Panel", false, 10)]
        static void Add()
        {
            var panel = new GameObject("Tl Panel");
            panel.AddComponent<TlPanel>();
            Undo.RegisterCreatedObjectUndo(panel, "Add Tl Panel");
            Selection.activeGameObject = panel;
        }
    }
}
