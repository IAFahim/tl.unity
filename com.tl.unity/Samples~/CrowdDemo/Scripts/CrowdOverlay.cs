using Unity.Entities;
using Unity.Profiling;
using UnityEngine;

public sealed class CrowdOverlay : MonoBehaviour
{
    static readonly int[] Sizes = { 1000, 10000, 100000 };

    World _world;
    EntityQuery _crowds;
    EntityQuery _dancers;
    ProfilerRecorder _tl;
    GUIStyle _style;
    string _text = string.Empty;

    void OnEnable()
    {
        _tl = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Tl.TlSimulationGroup", 30);
    }

    void OnDisable()
    {
        _tl.Dispose();
    }

    void Update()
    {
        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null || !world.IsCreated)
            return;
        if (_world != world)
        {
            _world = world;
            _crowds = world.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<Crowd>());
            _dancers = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Dancer>());
        }
        var ms = _tl.Valid && _tl.Count > 0 ? Average(_tl) / 1e6 : double.NaN;
        _text = "dancers " + _dancers.CalculateEntityCount() + (double.IsNaN(ms) ? "" : "   Tl step " + ms.ToString("0.00") + " ms");
    }

    void OnGUI()
    {
        _style ??= new GUIStyle(GUI.skin.label) { fontSize = 22, normal = { textColor = Color.white } };
        GUI.Label(new Rect(16, 12, 900, 40), _text, _style);
        for (var i = 0; i < Sizes.Length; i++)
            if (GUI.Button(new Rect(16 + i * 110, 52, 100, 32), Sizes[i] >= 1000 ? Sizes[i] / 1000 + "k" : Sizes[i].ToString()))
                Resize(Sizes[i]);
    }

    void Resize(int count)
    {
        if (_world == null || !_world.IsCreated || _crowds.CalculateEntityCount() != 1)
            return;
        _crowds.CompleteDependency();
        var crowd = _crowds.GetSingleton<Crowd>();
        crowd.Count = count;
        _crowds.SetSingleton(crowd);
    }

    static double Average(ProfilerRecorder recorder)
    {
        double sum = 0;
        var count = recorder.Count;
        for (var i = 0; i < count; i++)
            sum += recorder.GetSample(i).Value;
        return sum / count;
    }
}
