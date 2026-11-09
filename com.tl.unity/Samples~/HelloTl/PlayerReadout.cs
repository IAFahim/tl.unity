using HelloTl;
using Tl.Unity;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;

public sealed class PlayerReadout : MonoBehaviour
{
    World _world;
    EntityQuery _player;
    float _last = float.NaN;
    bool _started;
    string _text = "waiting for the SubScene to load...";

    void Update()
    {
        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null || !world.IsCreated)
            return;
        if (_world != world)
        {
            _world = world;
            _player = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Teleported>(), ComponentType.ReadOnly<LocalTransform>(), ComponentType.ReadOnly<Playback>());
        }
        if (_player.CalculateEntityCount() != 1)
            return;
        var manager = world.EntityManager;
        var player = _player.GetSingletonEntity();
        var x = manager.GetComponentData<LocalTransform>(player).Position.x;
        var tick = -1;
        foreach (var playback in manager.GetBuffer<Playback>(player, true))
            if (playback.Timeline != 0)
                tick = playback.Tick;
        _started |= tick >= 0;
        _text = tick >= 0 ? "player x = " + x + "   timeline tick " + tick : _started ? "player x = " + x + "   (timeline finished)" : "player x = " + x + "   (starting)";
        if (x != _last)
        {
            Debug.Log("Hello Tl: the player is at x = " + x + (tick >= 0 ? " (timeline tick " + tick + ")" : _started ? " (timeline finished)" : " (starting)"));
            _last = x;
        }
    }

    void OnGUI()
    {
        GUI.Label(new Rect(16, 12, 900, 40), _text, new GUIStyle(GUI.skin.label) { fontSize = 22, normal = { textColor = Color.white } });
    }
}
