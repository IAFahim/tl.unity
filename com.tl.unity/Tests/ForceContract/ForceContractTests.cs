using System;
using NUnit.Framework;
using Tl;
using Tl.Unity;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;

namespace Tl.Unity.ForceContract.Tests
{
    public class ForceContractTests
    {
        const string Assembly = ", Tl.Unity.ForceContract";

        [Test]
        public void TheGeneratorEmitsTheClipMirrorKindAndSystemIntoTheCoderAssembly()
        {
            Assert.IsNotNull(Type.GetType("Tl.Generated.ForceClip_TlData" + Assembly), "mirror");
            Assert.IsNotNull(Type.GetType("TlUnityGenerated.ForceKindEntry" + Assembly), "kind entry");
            Assert.IsNotNull(Type.GetType("TlUnityGenerated.ForceKindActive" + Assembly), "kind active");
            Assert.IsNotNull(Type.GetType("Tl.Generated.TlAuto_Tl_Unity_ForceContract_Jobs" + Assembly), "kind jobs");
            Assert.IsNotNull(Type.GetType("Tl.Generated.TlAuto_Tl_Unity_ForceContractChunkJob" + Assembly), "apply job");
        }

        [Test]
        public void TheMirrorIsASerializableClipDataThatWritesTheUnmanagedClip()
        {
            var type = Type.GetType("Tl.Generated.ForceClip_TlData" + Assembly);
            Assert.IsTrue(typeof(TlClipData).IsAssignableFrom(type));
            Assert.IsTrue(type.IsSerializable);
            var data = (TlClipData)Activator.CreateInstance(type);
            Assert.AreEqual("Force", data.MenuName);
            Assert.AreEqual(typeof(ForceClip), data.ClipType);
            Assert.AreEqual(UnsafeUtility.SizeOf<ForceClip>(), data.ClipSize);
            type.GetField("Strength").SetValue(data, 4f);
            type.GetField("Direction").SetValue(data, new float3(1f, 0f, 0f));
            var bytes = new byte[data.ClipSize];
            data.WriteClip(bytes, 0);
            Assert.AreEqual(4f, BitConverter.ToSingle(bytes, 12));
            Assert.AreEqual(1f, BitConverter.ToSingle(bytes, 0));
            Assert.AreEqual(1f, BitConverter.ToSingle(bytes, 16));
            Assert.AreEqual(0f, BitConverter.ToSingle(bytes, 16 + 15 * 4), 1e-6f);
            Assert.AreNotSame(data, data.CreateDefault());
        }

        [Test]
        public void TheKindCapacityIsComputedWithoutAKindsFile()
        {
            var kind = Type.GetType("TlUnityGenerated.ForceKindKind" + Assembly);
            Assert.AreEqual(6, (int)kind.GetField("InlineCapacity").GetValue(null));
        }

        [Test]
        public void TheApplySystemIsAnUnmanagedSystemAfterTheTimelineGroup()
        {
            var jobs = Type.GetType("Tl.Generated.TlAuto_Tl_Unity_ForceContract_Jobs" + Assembly);
            var system = typeof(TlKindSystem<>).MakeGenericType(jobs);
            Assert.IsTrue(UnsafeUtility.IsUnmanaged(system));
            using var world = new World("TlForceContract");
            var handle = world.CreateSystem(system);
            Assert.AreNotEqual(SystemHandle.Null, handle);
            using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<Force>());
            Assert.AreEqual(0, query.CalculateEntityCount());
        }

        [Test]
        public void TheForceSystemRunsLastInTheTlGroupAfterTheTimelineGroup()
        {
            var group = (UpdateInGroupAttribute)Attribute.GetCustomAttribute(typeof(ForceSystem), typeof(UpdateInGroupAttribute));
            Assert.AreEqual(typeof(TlSimulationGroup), group.GroupType);
            Assert.IsTrue(group.OrderLast);
            var legacy = (UpdateInGroupAttribute)Attribute.GetCustomAttribute(typeof(TimelineSimulationGroup), typeof(UpdateInGroupAttribute));
            Assert.AreEqual(typeof(TlSimulationGroup), legacy.GroupType);
        }

        [Test]
        public void TheCurveAndFrameGiveTheClipProgress()
        {
            var curve = TlCurveAuthoring.Sample(TlCurveAuthoring.Default());
            Assert.AreEqual(1f, curve.Evaluate(0f), 1e-6f);
            Assert.AreEqual(0.5f, curve.Evaluate(0.5f), 1e-5f);
            Assert.AreEqual(0f, curve.Evaluate(1f), 1e-6f);
            var clip = new ForceClip { Direction = new float3(1f, 0f, 0f), Strength = 4f, Falloff = curve };
            var frame = new TlFrame<ForceClip>(new Frame<TlNoSettings<ForceClip>, ForceClip>(default, clip, 30, 60, 30, 0, FrameFlags.None));
            Assert.AreEqual(0.5f, frame.Progress, 1e-6f);
            Assert.AreEqual(2f, ForceTrack.Evaluate(in frame).x, 1e-4f);
        }
    }
}
