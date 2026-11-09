using System;
using System.Linq;
using Tl.Gen.Tlb;

namespace Tl.Unity.Editor
{
    public static class TlSceneRunner
    {
        public static TlScene Read(byte[] sceneJson, byte[] library)
            => TlScene.Read(sceneJson, BakerAssemblyResolver.FromAssemblies(TlAssemblies.Loaded()), TlScene.ManifestOf(library, out _));

        public static TlSceneData Data(TlScene scene)
        {
            var data = new TlSceneData();
            data.Columns.AddRange(scene.Columns.Select(t => t.FullName));
            foreach (var entity in scene.Entities)
            {
                var item = new TlSceneData.EntityData { Name = entity.Name ?? string.Empty };
                foreach (var (type, bytes) in entity.States)
                    item.States.Add(new TlSceneData.State(type.FullName, bytes));
                foreach (var (timeline, tick) in entity.Play)
                    item.Plays.Add(new TlSceneData.Play(timeline, tick));
                data.Entities.Add(item);
            }
            foreach (var request in scene.Requests)
                data.Requests.Add(new TlSceneData.Request(request.Step, request.Entity, request.Op, request.Target, request.Value));
            foreach (var (step, forward) in scene.Direction)
                data.Turns.Add(new TlSceneData.Turn(step, forward));
            return data;
        }

        public static ulong Run(TlScene scene, byte[] library, int steps, Action<int, ulong> checkpoint = null)
            => TlSceneRun.Run(Data(scene), library, steps, checkpoint);
    }
}
