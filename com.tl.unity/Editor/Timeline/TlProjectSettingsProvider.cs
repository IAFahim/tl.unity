using UnityEditor;
using UnityEngine.UIElements;

namespace Tl.Unity.Editor
{
    static class TlProjectSettingsProvider
    {
        [SettingsProvider]
        static SettingsProvider Create()
        {
            return new SettingsProvider("Project/Tl", SettingsScope.Project)
            {
                label = "Tl",
                activateHandler = (search, root) =>
                {
                    var field = new IntegerField("Ticks per second") { value = (int)TlProjectSettings.instance.TicksPerSecond, tooltip = "How many simulation ticks the game runs per second. Timeline clips are rounded to ticks when they bake." };
                    field.RegisterValueChangedCallback(change => TlProjectSettings.instance.SetTicksPerSecond(change.newValue));
                    root.Add(new Label("Tl") { style = { unityFontStyleAndWeight = UnityEngine.FontStyle.Bold, fontSize = 19, marginBottom = 8, marginLeft = 9, marginTop = 2 } });
                    root.Add(field);
                },
                keywords = new[] { "Tl", "Ticks", "Tick rate", "Timeline" },
            };
        }
    }
}
