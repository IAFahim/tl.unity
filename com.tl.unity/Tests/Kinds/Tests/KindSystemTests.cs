using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Tl.Unity;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace KindGame
{
    [TestFixture]
    [BurstCompile]
    public unsafe class KindSystemTests
    {
        public delegate void UpdateFn(IntPtr system, IntPtr state);

        [BurstCompile]
        public static void TickUpdate(IntPtr system, IntPtr state) => ((TimelineKindTickSystem*)system)->OnUpdate(ref *(SystemState*)state);

        [BurstCompile]
        public static void RequestUpdate(IntPtr system, IntPtr state) => ((TimelineRequestSystem*)system)->OnUpdate(ref *(SystemState*)state);

        [BurstCompile]
        public static void AdvanceUpdate(IntPtr system, IntPtr state) => ((TimelineAdvanceSystem*)system)->OnUpdate(ref *(SystemState*)state);

        [BurstCompile]
        public static void EventUpdate(IntPtr system, IntPtr state) => ((TimelineEventSystem*)system)->OnUpdate(ref *(SystemState*)state);

        [BurstCompile]
        public static void MovementUpdate(IntPtr system, IntPtr state) => ((MovementSystem*)system)->OnUpdate(ref *(SystemState*)state);

        [BurstCompile]
        public static void VitalsUpdate(IntPtr system, IntPtr state) => ((VitalsSystem*)system)->OnUpdate(ref *(SystemState*)state);

        [Test]
        public void EveryStepSystemUpdateCompilesUnderBurst()
        {
            Assert.IsTrue(BurstCompiler.IsEnabled);
            BurstCompiler.CompileFunctionPointer<UpdateFn>(TickUpdate);
            BurstCompiler.CompileFunctionPointer<UpdateFn>(RequestUpdate);
            BurstCompiler.CompileFunctionPointer<UpdateFn>(AdvanceUpdate);
            BurstCompiler.CompileFunctionPointer<UpdateFn>(EventUpdate);
            BurstCompiler.CompileFunctionPointer<UpdateFn>(MovementUpdate);
            BurstCompiler.CompileFunctionPointer<UpdateFn>(VitalsUpdate);
        }

        [Test]
        public void TheStepPathHasNoManagedSystemOrManagedSingleton()
        {
            var assembly = typeof(TimelineKindTickSystem).Assembly;
            var inGroup = new List<Type>();
            foreach (var type in assembly.GetTypes())
            {
                Assert.IsFalse(type.IsSubclassOf(typeof(SystemBase)) && !type.IsSubclassOf(typeof(ComponentSystemGroup)), type.FullName + " is a SystemBase");
                var group = type.GetCustomAttribute<UpdateInGroupAttribute>();
                if (group != null && group.GroupType == typeof(TimelineSimulationGroup))
                    inGroup.Add(type);
            }
            CollectionAssert.AreEquivalent(new[] { typeof(TlRequestResolveSystem), typeof(TimelineRequestSystem), typeof(TimelineAdvanceSystem), typeof(TimelineEventSystem), typeof(TimelineKindTickSystem) }, inGroup);
            foreach (var type in inGroup)
            {
                Assert.IsTrue(type.IsValueType && typeof(ISystem).IsAssignableFrom(type), type.Name + " is not an ISystem struct");
                Assert.IsTrue(UnsafeUtility.IsUnmanaged(type), type.Name + " holds managed data");
                Assert.IsNotNull(type.GetCustomAttribute<BurstCompileAttribute>(), type.Name + " has no [BurstCompile]");
                var update = type.GetMethod("OnUpdate", new[] { typeof(SystemState).MakeByRefType() });
                Assert.IsNotNull(update, type.Name + " has no OnUpdate");
                Assert.IsNotNull(update.GetCustomAttribute<BurstCompileAttribute>(), type.Name + ".OnUpdate has no [BurstCompile]");
            }
            foreach (var singleton in new[] { typeof(TimelineLibrary), typeof(TimelineStep), typeof(TimelineKindTick), typeof(KindIndex) })
                Assert.IsTrue(UnsafeUtility.IsUnmanaged(singleton), singleton.Name + " holds managed data");
        }

        [Test]
        public void TheLibrarySingletonCarriesKindsAndStepDirectionPerWorld()
        {
            using (var game = new KindWorld(1))
            {
                using var query = game.Entities.CreateEntityQuery(typeof(TimelineLibrary), typeof(TimelineStep));
                var library = query.GetSingleton<TimelineLibrary>();
                Assert.Greater(library.Kinds.KindCount, 0);
                Assert.AreEqual(4, global::Unity.Mathematics.math.countbits(library.Kinds.Bound));
                Assert.AreEqual(1u, library.Generation);
                Assert.IsTrue(query.GetSingleton<TimelineStep>().Forward);
                Assert.IsTrue(game.Tick.IsCreated);
            }
        }

        sealed class TwinModule : IKindModule
        {
            readonly string _name;

            public TwinModule(string name)
            {
                _name = name;
            }

            public void Register(KindRegistry registry)
            {
                registry.Add(new KindDescriptor(_name, typeof(DamageEntry), typeof(DamageActive), typeof(DamageEnabled), 4, 12, string.Empty, null));
            }
        }

        [Test]
        public void ADuplicateKindNamesBothAssemblies()
        {
            var registry = new KindRegistry();
            registry.Include(new TwinModule("Damage"), "Game.Logic");
            var error = Assert.Throws<ArgumentException>(() => registry.Include(new TwinModule("Damage"), "Tl.Unity.Kinds"));
            StringAssert.Contains("Kind 'Damage' is registered twice", error.Message);
            StringAssert.Contains("'Game.Logic'", error.Message);
            StringAssert.Contains("'Tl.Unity.Kinds'", error.Message);
            Assert.AreEqual("Game.Logic", registry.SourceOf(0));
        }
    }
}
