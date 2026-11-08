using Tl.Pack.Inventory;
using Tl.Unity;
using Unity.Entities;
using UnityEngine;

public sealed class BagOverlay : MonoBehaviour
{
    World _world;
    EntityQuery _bags;
    GUIStyle _style;
    string _text = string.Empty;

    public static string Show(in Bag bag)
    {
        return "Wood " + bag.Wood + " Stone " + bag.Stone + " Plank " + bag.Plank + " Potion " + bag.Potion + " Sword " + bag.Sword + " Coin " + bag.Coin + " Key " + bag.Key + " Gem " + bag.Gem;
    }

    void Update()
    {
        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null || !world.IsCreated)
            return;
        if (_world != world)
        {
            _world = world;
            _bags = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Bag>(), ComponentType.ReadOnly<Playback>());
        }
        if (_bags.CalculateEntityCount() != 1)
            return;
        _bags.CompleteDependency();
        var entity = _bags.GetSingletonEntity();
        var playbacks = world.EntityManager.GetBuffer<Playback>(entity, true);
        var tick = playbacks.Length > 0 ? playbacks[0].Tick : 0;
        _text = "Tick " + tick + "\n" + Show(_bags.GetSingleton<Bag>());
    }

    void OnGUI()
    {
        _style ??= new GUIStyle(GUI.skin.label) { fontSize = 22, normal = { textColor = Color.white } };
        GUI.Label(new Rect(16, 12, 1200, 80), _text, _style);
    }
}
