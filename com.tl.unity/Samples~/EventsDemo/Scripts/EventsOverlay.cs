using Unity.Entities;
using UnityEngine;

public sealed class EventsOverlay : MonoBehaviour
{
    World _world;
    EntityQuery _scores;
    EntityQuery _dummies;
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
            _scores = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Score>());
            _dummies = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<DummyHealth>(), ComponentType.ReadOnly<Wounds>());
        }
        if (_scores.CalculateEntityCount() != 1 || _dummies.CalculateEntityCount() != 1)
            return;
        _scores.CompleteDependency();
        _dummies.CompleteDependency();
        var score = _scores.GetSingleton<Score>();
        var max = _dummies.GetSingleton<DummyHealth>().Max;
        var health = max - _dummies.GetSingleton<Wounds>().Taken;
        _text = "Strikes " + score.Strikes + "   Dummy health " + health + " / " + max + "   Sparks " + score.Sparks;
    }

    void OnGUI()
    {
        _style ??= new GUIStyle(GUI.skin.label) { fontSize = 22, normal = { textColor = Color.white } };
        GUI.Label(new Rect(16, 12, 900, 40), _text, _style);
    }
}
