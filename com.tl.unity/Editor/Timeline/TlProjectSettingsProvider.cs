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
                    root.Add(new Label("Channel names") { style = { unityFontStyleAndWeight = UnityEngine.FontStyle.Bold, marginTop = 12, marginLeft = 9 } });
                    var problem = new HelpBox(string.Empty, HelpBoxMessageType.Warning) { style = { display = DisplayStyle.None } };
                    root.Add(problem);
                    for (var i = 1; i <= TlProjectSettings.ChannelCount; i++)
                    {
                        var channel = i;
                        var name = new TextField("Channel " + channel) { value = TlProjectSettings.instance.ChannelName(channel), isDelayed = true, tooltip = "The name written to JSON and shown in the Timeline Channel menu. Empty means the number." };
                        name.RegisterValueChangedCallback(change =>
                        {
                            if (TlProjectSettings.instance.TrySetChannelName(channel, change.newValue, out var error))
                            {
                                problem.style.display = DisplayStyle.None;
                                name.SetValueWithoutNotify(TlProjectSettings.instance.ChannelName(channel));
                                return;
                            }
                            problem.text = error;
                            problem.style.display = DisplayStyle.Flex;
                            name.SetValueWithoutNotify(TlProjectSettings.instance.ChannelName(channel));
                        });
                        root.Add(name);
                    }
                },
                keywords = new[] { "Tl", "Ticks", "Tick rate", "Timeline" },
            };
        }
    }
}
