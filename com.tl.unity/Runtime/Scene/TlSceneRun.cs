using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace Tl.Unity
{
    public static class TlSceneRun
    {
        static readonly Dictionary<string, Type> Resolved = new Dictionary<string, Type>(StringComparer.Ordinal);

        public static Type ResolveType(string fullName)
        {
            if (Resolved.TryGetValue(fullName, out var cached))
                return cached;
            foreach (var assembly in TlAssemblies.Loaded())
            {
                var type = assembly.GetType(fullName, false);
                if (type == null)
                    continue;
                Resolved[fullName] = type;
                return type;
            }
            throw new InvalidOperationException("Tl scene: no loaded assembly has the state type " + fullName + ".");
        }

        public static unsafe ulong Run(TlSceneData scene, byte[] library, int steps, Action<int, ulong> checkpoint = null)
        {
            var columns = scene.Columns.Select(ResolveType).ToArray();
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
                        foreach (var play in scene.Entities[e].Plays)
                            manager.GetBuffer<TimelineRequest>(rows[e]).Add(new TimelineRequest { Slot = -1, Timeline = play.Timeline, At = play.Tick, Op = global::Tl.RequestOp.Start });
                foreach (var request in scene.Requests)
                    if (request.Step == step)
                        manager.GetBuffer<TimelineRequest>(rows[request.Entity]).Add(new TimelineRequest { Slot = -1, Timeline = request.Target, At = request.Value, Op = request.Op });
                group.Update();
                manager.CompleteAllTrackedJobs();
                for (var e = 0; e < rows.Length; e++)
                {
                    var playbacks = manager.GetBuffer<Playback>(rows[e], true);
                    hash.Slots((global::Tl.Playback*)playbacks.GetUnsafeReadOnlyPtr(), playbacks.Length);
                    foreach (var type in columns)
                    {
                        var size = UnsafeUtility.SizeOf(type);
                        if (manager.HasComponent(rows[e], ComponentType.ReadOnly(type)))
                            hash.Bytes(Pointer(manager, rows[e], type, size, false), size);
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

        public static unsafe Entity[] Spawn(EntityManager manager, TlSceneData scene)
        {
            var registry = KindRegistry.Discover();
            var rows = new Entity[scene.Entities.Count];
            for (var e = 0; e < rows.Length; e++)
            {
                var entity = scene.Entities[e];
                var states = entity.States.Select(s => ResolveType(s.Type)).ToList();
                var types = new List<ComponentType> { typeof(Playback), typeof(TimelineRequest), typeof(TimelinePlaying), typeof(TimelineHorizon) };
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
                foreach (var state in entity.States)
                {
                    var type = ResolveType(state.Type);
                    var size = UnsafeUtility.SizeOf(type);
                    if (size != state.Bytes.Length)
                        throw new InvalidOperationException("Tl scene: " + state.Type + " is " + size + " bytes here but " + state.Bytes.Length + " in the scene.");
                    var target = Pointer(manager, rows[e], type, size, true);
                    for (var i = 0; i < size; i++)
                        target[i] = state.Bytes[i];
                }
            }
            return rows;
        }

        public static unsafe byte[] ReadState(EntityManager manager, Entity entity, Type type)
        {
            if (!manager.HasComponent(entity, ComponentType.ReadOnly(type)))
                return null;
            var size = UnsafeUtility.SizeOf(type);
            var bytes = new byte[size];
            var source = Pointer(manager, entity, type, size, false);
            for (var i = 0; i < size; i++)
                bytes[i] = source[i];
            return bytes;
        }

        static unsafe byte* Pointer(EntityManager manager, Entity entity, Type type, int size, bool write)
        {
            var handle = manager.GetDynamicComponentTypeHandle(write ? ComponentType.ReadWrite(type) : ComponentType.ReadOnly(type));
            var chunk = manager.GetChunk(entity);
            var entities = chunk.GetNativeArray(manager.GetEntityTypeHandle());
            var index = 0;
            while (index < entities.Length && entities[index] != entity)
                index++;
            var column = chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref handle, size);
            var origin = write ? (byte*)NativeArrayUnsafeUtility.GetUnsafePtr(column) : (byte*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(column);
            return origin + (long)index * size;
        }

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
