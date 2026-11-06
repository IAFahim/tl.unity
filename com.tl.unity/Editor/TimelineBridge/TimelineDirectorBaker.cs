using Unity.Entities;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;

namespace Tl.Unity.Editor
{
    public sealed class TimelineDirectorBaker : Baker<TimelineDirectorAuthoring>
    {
        public override void Bake(TimelineDirectorAuthoring authoring)
        {
            DependsOn(authoring.Bridge);
            var director = GetComponent<PlayableDirector>();
            if (director != null)
                DependsOn(director.playableAsset);
            if (authoring.MayStart != null)
                foreach (var timeline in authoring.MayStart)
                    DependsOn(timeline);
            GetComponent<TimelinePlayerAuthoring>();
            if (authoring.Bridge == null)
                Debug.LogError("Tl baking: GameObject '" + authoring.name + "' has a Timeline Director without a Bridge.", authoring);
            else if (!authoring.InSync())
                Debug.LogError("Tl baking: GameObject '" + authoring.name + "' Timeline Player does not match its PlayableDirector (expected start-with [" + string.Join(", ", authoring.ExpectedStartWith()) + "], may-start [" + string.Join(", ", authoring.ExpectedMayStart()) + "]). Run Tools > Tl > Timeline Bridge > Sync Directors.", authoring);
        }
    }

    static class TimelineDirectorSync
    {
        [MenuItem("Tools/Tl/Timeline Bridge/Sync Directors")]
        static void SyncOpenScenes()
        {
            var changed = 0;
            for (var s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded)
                    continue;
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var director in root.GetComponentsInChildren<TimelineDirectorAuthoring>(true))
                    {
                        var player = director.GetComponent<TimelinePlayerAuthoring>();
                        if (player == null)
                            continue;
                        Undo.RecordObject(player, "Sync Timeline Director");
                        if (!director.Sync())
                            continue;
                        EditorUtility.SetDirty(player);
                        changed++;
                    }
            }
            Debug.Log("Tl timeline bridge: synced " + changed + " Timeline Players with their directors.");
        }
    }
}
