using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Tl.Gen.Tlb;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace Tl.Unity.Editor
{
    public static class TlSceneRunner
    {
        public static TlScene Read(byte[] sceneJson, byte[] library)
            => TlScene.Read(sceneJson, BakerAssemblyResolver.FromAssemblies(TlAssemblies.Loaded()), TlScene.ManifestOf(library, out _));

        public static unsafe ulong Run(TlScene scene, byte[] library, int steps, Action<int, ulong> checkpoint = null)
        {
            using var tl = TlLibrary.FromBytes(library);
            using var world = new World("TlScene");
            var manager = world.EntityManager;
            tl.Publish(manager);
            var group = AddSystems(world);
            var rows = Spawn(manager, scene);
            using var stepQuery = manager.CreateEntityQuery(typeof(TimelineStep));
            var hash = TlStateHash.Begin();
            var forward = true;
            for (var step = 0; step < steps; step++)
            {
                forward = scene.ForwardAt(step, forward);
                stepQuery.SetSingleton(new TimelineStep { Forward = forward });
                if (step == 0)
                    for (var e = 0; e < rows.Length; e++)
                        foreach (var (timeline, tick) in scene.Entities[e].Play)
                            manager.GetBuffer<TimelineRequest>(rows[e]).Add(new TimelineRequest { Slot = -1, Timeline = timeline, At = tick, Op = RequestOp.Start });
                foreach (var request in scene.Requests)
                    if (request.Step == step)
                        manager.GetBuffer<TimelineRequest>(rows[request.Entity]).Add(new TimelineRequest { Slot = -1, Timeline = request.Target, At = request.Value, Op = request.Op });
                group.Update();
                manager.CompleteAllTrackedJobs();
                for (var e = 0; e < rows.Length; e++)
                {
                    var playbacks = manager.GetBuffer<Playback>(rows[e], true);
                    hash.Slots((global::Tl.Playback*)playbacks.GetUnsafeReadOnlyPtr(), playbacks.Length);
                    foreach (var type in scene.Columns)
                    {
                        var size = UnsafeUtility.SizeOf(type);
                        if (manager.HasComponent(rows[e], ComponentType.ReadOnly(type)))
                            {
                            var bytes = stackalloc byte[size];
                            Raw(nameof(ReadState), type).Invoke(null, new object[] { manager, rows[e], (IntPtr)bytes });
                            hash.Bytes(bytes, size);
                        }
                        else
                        {
                            var zero = stackalloc byte[size];
                            UnsafeUtility.MemClear(zero, size);
                            hash.Bytes(zero, size);
                        }
                    }
                }
                checkpoint?.Invoke(step + 1, hash.Value);
            }
            return hash.Value;
        }

        public static unsafe Entity[] Spawn(EntityManager manager, TlScene scene)
        {
            var registry = KindRegistry.Discover();
            var rows = new Entity[scene.Entities.Count];
            for (var e = 0; e < rows.Length; e++)
            {
                var entity = scene.Entities[e];
                var states = entity.States.Select(s => s.Type).ToList();
                var types = new List<ComponentType> { typeof(Playback), typeof(TimelineRequest), typeof(TimelinePlaying) };
                types.AddRange(states.Select(t => ComponentType.ReadWrite(t)));
                var kinds = new List<KindDescriptor>();
                for (var k = 0; k < registry.Count; k++)
                {
                    var kind = registry[k];
                    if (kind.Columns.Length == 0 || !kind.Columns.All(states.Contains))
                        continue;
                    kinds.Add(kind);
                    foreach (var type in new[] { kind.Entry, kind.Active, kind.Enabled })
                        if (!types.Contains(ComponentType.ReadWrite(type)))
                            types.Add(ComponentType.ReadWrite(type));
                }
                rows[e] = manager.CreateEntity(types.ToArray());
                manager.SetComponentEnabled<TimelinePlaying>(rows[e], false);
                foreach (var kind in kinds)
                    manager.SetComponentEnabled(rows[e], ComponentType.ReadWrite(kind.Active), false);
                foreach (var (type, bytes) in entity.States)
                    fixed (byte* data = bytes)
                        Raw(nameof(WriteState), type).Invoke(null, new object[] { manager, rows[e], (IntPtr)data });
            }
            return rows;
        }

        static MethodInfo Raw(string name, Type type)
            => typeof(TlSceneRunner).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(type);

        static unsafe void ReadState<T>(EntityManager manager, Entity entity, IntPtr bytes) where T : unmanaged, IComponentData
            => *(T*)bytes = manager.GetComponentData<T>(entity);

        static unsafe void WriteState<T>(EntityManager manager, Entity entity, IntPtr bytes) where T : unmanaged, IComponentData
            => manager.SetComponentData(entity, *(T*)bytes);

        static ComponentSystemGroup AddSystems(World world)
        {
            var systems = new List<Type> { typeof(SimulationSystemGroup), typeof(FixedStepSimulationSystemGroup) };
            var all = DefaultWorldInitialization.GetAllSystemTypeIndices(WorldSystemFilterFlags.Default);
            for (var i = 0; i < all.Length; i++)
                {
                    var type = TypeManager.GetSystemType(all[i]);
                    if (Under(type) && !systems.Contains(type))
                        systems.Add(type);
                }
            DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, systems);
            return world.GetExistingSystemManaged<global::Tl.TlSimulationGroup>();
        }

        static bool Under(Type type)
        {
            for (var depth = 0; depth < 16 && type != null; depth++)
            {
                if (type == typeof(global::Tl.TlSimulationGroup))
                    return true;
                var group = (UpdateInGroupAttribute)type.GetCustomAttributes(typeof(UpdateInGroupAttribute), true).FirstOrDefault();
                type = group?.GroupType;
            }
            return false;
        }
    }
}
