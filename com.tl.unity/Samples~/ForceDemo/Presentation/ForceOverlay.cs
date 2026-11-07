using System.Globalization;
using System.Text;
using Tl.Unity;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;

public sealed class ForceOverlay : MonoBehaviour
{
    public int TicksPerSecond = 60;

    World _world;
    EntityQuery _cubes;
    GUIStyle _style;
    string _text = string.Empty;

    void Update()
    {
        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null || !world.IsCreated)
            return;
        if (_world != world)
        {
            _world = world;
            _cubes = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<FollowTarget>(), ComponentType.ReadOnly<ForceShown>(),
                ComponentType.ReadOnly<LocalTransform>(), ComponentType.ReadOnly<Playback>(), ComponentType.ReadOnly<TlTimelineSet>());
        }
        if (_cubes.CalculateEntityCount() != 1)
            return;
        var manager = world.EntityManager;
        var cube = _cubes.GetSingletonEntity();
        var force = manager.GetComponentData<ForceShown>(cube).Value;
        var x = manager.GetComponentData<LocalTransform>(cube).Position.x;
        var name = "-";
        var seconds = 0f;
        foreach (var playback in manager.GetBuffer<Playback>(cube, true))
        {
            if (playback.Timeline == 0)
                continue;
            seconds = playback.Tick / (float)TicksPerSecond;
            foreach (var entry in manager.GetBuffer<TlTimelineSet>(cube, true))
                if (entry.Id == playback.Timeline)
                    name = NameOf(entry);
        }
        _text = string.Format(CultureInfo.InvariantCulture, "{0}  {1:0.0} s   force {2:+0.0;-0.0;0.0} X   x {3:0.00} m", name, seconds, force.x, x);
    }

    void OnGUI()
    {
        _style ??= new GUIStyle(GUI.skin.label) { fontSize = 22, normal = { textColor = Color.white } };
        GUI.Label(new Rect(16, 12, 900, 40), _text, _style);
    }

    static string NameOf(TlTimelineSet entry)
    {
        ref var bytes = ref entry.Blob.Value.Name;
        var text = new byte[bytes.Length];
        for (var i = 0; i < text.Length; i++)
            text[i] = bytes[i];
        return Encoding.UTF8.GetString(text);
    }
}
