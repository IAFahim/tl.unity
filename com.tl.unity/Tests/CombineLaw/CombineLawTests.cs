using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Tl.Gen.Tlb;
using Tl.Unity;
using Unity.Burst;
using Unity.Entities;
using LogType = UnityEngine.LogType;

namespace CombineLaw
{
    [BurstCompile]
    [DisableAutoCreation]
    public partial struct LawSystem : ISystem
    {
        public TimelineJobs.Fused Jobs;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<TimelineLibrary>();
            Jobs.Create(ref state);
            Jobs.DenseEnabled = false;
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var library = SystemAPI.GetSingleton<TimelineLibrary>();
            state.Dependency = Jobs.Schedule(ref state, library.View, library.Kinds, true, state.Dependency);
        }
    }

    [TestFixture]
    public class CombineLawTests
    {
        static unsafe byte[] Raw<T>(T value) where T : unmanaged
        {
            var bytes = new byte[sizeof(T)];
            fixed (byte* at = bytes) *(T*)at = value;
            return bytes;
        }

        static byte[] Library()
        {
            var track = new TypedTrack { Type = typeof(WrongTrack), Data = Raw(new WrongTrack { Scale = 1f }) };
            foreach (var (start, end, value, priority) in new[] { (0u, 10u, 3, 1), (5u, 15u, -4, 1), (12u, 20u, 5, 1) })
                track.Clips.Add(new TypedClip { Type = typeof(WrongClip), Start = start, End = end, Data = Raw(new WrongClip { V = value, P = priority }) });
            var timeline = new TypedTimeline { Name = "wrong", Duration = 20, Loops = true, Tracks = { track } };
            var manifest = LibraryBake.Manifest(new List<string> { "wrong" }, new List<string>(), 60);
            var kinds = LibraryBake.Kinds(new[] { typeof(ToSum).Assembly });
            var tlb = LibraryBake.BakeTyped(timeline, manifest);
            return global::Tl.LibraryAssembler.Assemble(new List<global::Tl.LibraryPart> { new global::Tl.LibraryPart { Name = "wrong", Tlb = tlb, Kinds = LibraryBake.KindPayload("wrong", tlb, kinds) } }, 60, global::Tl.LibrarySettings.Default);
        }

        [Test]
        public void ADeclaredCombineThatDisagreesWithTheKindsCombineThrowsWhenTheTableIsBuilt()
        {
            var law = new Regex("^Tl: kind 'CombineLaw\\.(\\w+)' declares Combine = (Sum|Pick|Or), but CombineLaw\\.\\1\\.Combine gives a different result on values from the library; fix Combine or the declaration\\.$", RegexOptions.Multiline);
            var thrown = new List<string>();
            var quiet = 0;
            for (var system = 0; system < 4; system++)
            {
                var library = TlLibrary.FromBytes(Library());
                var world = new World("CombineLaw" + system);
                library.Publish(world.EntityManager);
                world.CreateSystem<TimelineKindTickSystem>();
                var fused = world.CreateSystem<LawSystem>();
                world.Unmanaged.GetUnsafeSystemRef<LawSystem>(fused).Jobs.Skip = ~(1ul << system);
                InvalidOperationException caught = null;
                try { fused.Update(world.Unmanaged); }
                catch (InvalidOperationException e) { caught = e; }
                world.Dispose();
                library.Dispose();
                if (caught == null)
                {
                    quiet++;
                    continue;
                }
                Assert.IsTrue(law.IsMatch(caught.Message), "system " + system + ": " + caught.Message);
                thrown.Add(law.Match(caught.Message).Groups[1].Value);
            }
            Assert.AreEqual(1, quiet, "exactly one system's kinds agree with their declarations and must build their tables without a throw");
            CollectionAssert.AreEquivalent(new[] { "SumIsMax", "FirstWins", "OrIsSum" }, thrown, "each disagreeing kind must be named once");
        }
    }
}
