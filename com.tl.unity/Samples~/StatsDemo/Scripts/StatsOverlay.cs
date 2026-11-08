using Tl.Pack.Stats;
using Unity.Entities;
using UnityEngine;

public sealed class StatsOverlay : MonoBehaviour
{
    World _world;
    EntityQuery _heroes;
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
            _heroes = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Health>(), ComponentType.ReadOnly<Attack>());
        }
        if (_heroes.CalculateEntityCount() != 1)
            return;
        _heroes.CompleteDependency();
        var health = _heroes.GetSingleton<Health>();
        var attack = _heroes.GetSingleton<Attack>();
        _text = "Health " + health.Value + " / " + health.Max + "   Attack " + attack.Value + (attack.Bonus > 0 ? " (haste +" + attack.Bonus + ")" : "");
    }

    void OnGUI()
    {
        _style ??= new GUIStyle(GUI.skin.label) { fontSize = 22, normal = { textColor = Color.white } };
        GUI.Label(new Rect(16, 12, 900, 40), _text, _style);
    }
}
