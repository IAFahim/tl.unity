using System;
using System.Collections.Generic;
using Tl.Generated;
using Tl.Unity;
using TlUnityGenerated;
using Unity.Entities;

namespace DirectGame
{
    public sealed class DirectWorld : IDisposable
    {
        public const ushort Stroll = 1;

        public readonly World World;
        public readonly EntityManager Entities;
        public readonly Entity[] Rows;
        readonly SystemHandle _tick;
        readonly SystemHandle _apply;

        public DirectWorld(int count)
        {
            World = new World("TlDirect");
            Entities = World.EntityManager;
            TimelineLibraryBootstrap.Use("direct.tlib", DirectLibraryBytes.Bytes());
            World.CreateSystem<TimelineLibrarySystem>().Update(World.Unmanaged);
            _tick = World.CreateSystem<TimelineKindTickSystem>();
            _apply = World.CreateSystem<TlKindSystem<TlAuto_Tl_Unity_Direct_Jobs>>();
            Rows = new Entity[count];
            for (var i = 0; i < count; i++)
                Rows[i] = Spawn();
        }

        Entity Spawn()
        {
            var types = new List<ComponentType> { typeof(Playback), typeof(TimelineRequest), typeof(TimelinePlaying), typeof(Stride), typeof(StrideStat) };
            types.AddRange(PacingKind.Types);
            var entity = Entities.CreateEntity(types.ToArray());
            Entities.SetComponentEnabled<TimelinePlaying>(entity, false);
            PacingKind.Init(Entities, entity);
            Entities.SetComponentData(entity, new StrideStat { Value = 1f });
            return entity;
        }

        public void Start(int row, ushort timeline)
        {
            Entities.GetBuffer<TimelineRequest>(Rows[row]).Add(TimelineRequest.Start(0, timeline, 0));
        }

        public void Step()
        {
            _tick.Update(World.Unmanaged);
            _apply.Update(World.Unmanaged);
        }

        public Stride Stride(int row)
        {
            Entities.CompleteAllTrackedJobs();
            return Entities.GetComponentData<Stride>(Rows[row]);
        }

        public void Dispose()
        {
            Entities.CompleteAllTrackedJobs();
            TimelineLibraryBootstrap.Release();
            if (World.IsCreated)
                World.Dispose();
        }
    }
}
