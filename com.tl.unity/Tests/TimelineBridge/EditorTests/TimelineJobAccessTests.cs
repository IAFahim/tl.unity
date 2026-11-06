using NUnit.Framework;
using Tl.Unity;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;

namespace KindGame.Bridge
{
    public struct WalkSpeed : IComponentData
    {
        public float Value;
        public int Clips;
    }

    [BurstCompile]
    public partial struct WalkSpeedJob : IJobEntity
    {
        [NativeDisableUnsafePtrRestriction] public global::Tl.LibraryView View;
        public ulong WalkPair;

        void Execute(in DynamicBuffer<Playback> playbacks, ref WalkSpeed speed)
        {
            var best = new WalkClip();
            var clips = 0;
            for (var i = 0; i < playbacks.Length; i++)
            {
                if (!TimelineClips.TryRead(in View, playbacks[i], WalkPair, out WalkTrack track, out WalkClip clip))
                    continue;
                if (clips == 0 || clip.Priority >= best.Priority)
                    best = new WalkClip { Speed = clip.Speed * track.Scale, Priority = clip.Priority };
                clips++;
            }
            speed = new WalkSpeed { Value = best.Speed, Clips = clips };
        }
    }

    [DisableAutoCreation]
    [BurstCompile]
    public partial struct WalkSpeedSystem : ISystem
    {
        ulong _walkPair;

        public void OnCreate(ref SystemState state)
        {
            _walkPair = TimelineClips.PairKey<WalkTrack, WalkClip>();
            state.RequireForUpdate<TimelineLibrary>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var view = SystemAPI.GetSingleton<TimelineLibrary>().View;
            state.Dependency = new WalkSpeedJob { View = view, WalkPair = _walkPair }.ScheduleParallel(state.Dependency);
        }
    }

    [BurstCompile]
    public struct ArcVelocityJob : IJob
    {
        [NativeDisableUnsafePtrRestriction] public global::Tl.LibraryView View;
        public ulong ArcPair;
        public Playback Playback;
        public NativeReference<float> Velocity;

        public void Execute()
        {
            Velocity.Value = TimelineClips.TryRead(in View, Playback, ArcPair, out ArcTrack _, out ArcClip clip) ? clip.Velocity : -1f;
        }
    }

    [BurstCompile]
    public struct WalkSpeedChunkJob : IJobChunk
    {
        [NativeDisableUnsafePtrRestriction] public global::Tl.LibraryView View;
        public ulong WalkPair;
        [ReadOnly] public BufferTypeHandle<Playback> Playbacks;
        public ComponentTypeHandle<WalkSpeed> Speeds;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in global::Unity.Burst.Intrinsics.v128 chunkEnabledMask)
        {
            var playbacks = chunk.GetBufferAccessor(ref Playbacks);
            var speeds = chunk.GetNativeArray(ref Speeds);
            for (var row = 0; row < chunk.Count; row++)
            {
                var slots = playbacks[row];
                var speed = new WalkSpeed();
                for (var i = 0; i < slots.Length; i++)
                    if (TimelineClips.TryRead(in View, slots[i], WalkPair, out WalkTrack track, out WalkClip clip))
                    {
                        speed.Value = clip.Speed * track.Scale;
                        speed.Clips++;
                    }
                speeds[row] = speed;
            }
        }
    }

    [DisableAutoCreation]
    [BurstCompile]
    public partial struct WalkSpeedChunkSystem : ISystem
    {
        ulong _walkPair;
        EntityQuery _query;

        public void OnCreate(ref SystemState state)
        {
            _walkPair = TimelineClips.PairKey<WalkTrack, WalkClip>();
            _query = SystemAPI.QueryBuilder().WithAll<Playback>().WithAllRW<WalkSpeed>().Build();
            state.RequireForUpdate<TimelineLibrary>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var job = new WalkSpeedChunkJob
            {
                View = SystemAPI.GetSingleton<TimelineLibrary>().View,
                WalkPair = _walkPair,
                Playbacks = SystemAPI.GetBufferTypeHandle<Playback>(true),
                Speeds = SystemAPI.GetComponentTypeHandle<WalkSpeed>(false),
            };
            state.Dependency = job.ScheduleParallel(_query, state.Dependency);
        }
    }

    public sealed class BridgeLibraryWorld : System.IDisposable
    {
        public const string LibraryFile = "Packages/com.tl.unity/Tests/Kinds/Timelines~/game.tlib";

        public readonly World World;
        public readonly EntityManager Entities;

        public BridgeLibraryWorld()
        {
            TlGeneratedBootstrap.InstallDiscoveredModules();
            World = new World("TlBridgeJobs");
            Entities = World.EntityManager;
            TimelineLibraryBootstrap.Ensure(Entities, "bridge.tlib", System.IO.File.ReadAllBytes(Tl.Unity.Editor.TimelineKindSets.PhysicalPath(LibraryFile)));
        }

        public Entity Spawn(params Playback[] playbacks)
        {
            var entity = Entities.CreateEntity(typeof(Playback), typeof(WalkSpeed));
            var buffer = Entities.GetBuffer<Playback>(entity);
            foreach (var playback in playbacks)
                buffer.Add(playback);
            return entity;
        }

        public global::Tl.LibraryView View => Entities.CreateEntityQuery(typeof(TimelineLibrary)).GetSingleton<TimelineLibrary>().View;

        public void Dispose()
        {
            Entities.CompleteAllTrackedJobs();
            TimelineLibraryBootstrap.Shutdown(Entities);
            if (World.IsCreated)
                World.Dispose();
        }
    }

    public class TimelineJobAccessTests
    {
        static Playback At(ushort timeline, ushort tick) => new Playback { Timeline = timeline, Tick = tick };

        [Test]
        public void AHandWrittenIJobChunkReadsTheClipOfEachPlaybackAndWritesAComponent()
        {
            using var world = new BridgeLibraryWorld();
            var runner = world.Spawn(At(Timelines.Run, 5));
            var vaulter = world.Spawn(At(Timelines.Vault, 1));
            var idle = world.Spawn();
            var system = world.World.CreateSystem<WalkSpeedChunkSystem>();
            system.Update(world.World.Unmanaged);
            world.Entities.CompleteAllTrackedJobs();
            var run = world.Entities.GetComponentData<WalkSpeed>(runner);
            var vault = world.Entities.GetComponentData<WalkSpeed>(vaulter);
            Assert.AreEqual(1, run.Clips);
            Assert.AreEqual(2f, run.Value);
            Assert.AreEqual(1, vault.Clips);
            Assert.AreEqual(9f, vault.Value);
            Assert.AreEqual(0, world.Entities.GetComponentData<WalkSpeed>(idle).Clips);
        }

        [Test]
        public void AHandWrittenIJobEntityReadsTheClipOfEachPlaybackAndWritesAComponent()
        {
            using var world = new BridgeLibraryWorld();
            var row = world.Spawn(At(Timelines.Run, 7));
            var system = world.World.CreateSystem<WalkSpeedSystem>();
            system.Update(world.World.Unmanaged);
            world.Entities.CompleteAllTrackedJobs();
            var speed = world.Entities.GetComponentData<WalkSpeed>(row);
            Assert.AreEqual(1, speed.Clips);
            Assert.AreEqual(2f, speed.Value);
            world.Entities.GetBuffer<Playback>(row).Add(At(Timelines.SpeedBoost, 0));
            system.Update(world.World.Unmanaged);
            world.Entities.CompleteAllTrackedJobs();
            speed = world.Entities.GetComponentData<WalkSpeed>(row);
            Assert.AreEqual(2, speed.Clips);
            Assert.AreEqual(5f, speed.Value);
        }

        [Test]
        public void AHandWrittenIJobReadsOneClipFromTheLibraryView()
        {
            using var world = new BridgeLibraryWorld();
            var view = world.View;
            using var velocity = new NativeReference<float>(Allocator.TempJob);
            var arc = TimelineClips.PairKey<ArcTrack, ArcClip>();
            new ArcVelocityJob { View = view, ArcPair = arc, Playback = At(Timelines.Jump, 3), Velocity = velocity }.Schedule().Complete();
            Assert.AreEqual(3f, velocity.Value);
            new ArcVelocityJob { View = view, ArcPair = arc, Playback = At(Timelines.Run, 3), Velocity = velocity }.Schedule().Complete();
            Assert.AreEqual(-1f, velocity.Value);
            new ArcVelocityJob { View = view, ArcPair = arc, Playback = At(Timelines.PotionHop, 5), Velocity = velocity }.Schedule().Complete();
            Assert.AreEqual(1f, velocity.Value);
        }
    }
}
