using System;
using System.Collections;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Rifle;
using Tl;
using Ui;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.TestTools;

public unsafe class NativeV3Tests
{
    static TimelineAsset Load(string name) => TimelineAsset.LoadOwned(Resources.Load<TextAsset>(name).bytes);

    [TearDown]
    public void Cleanup()
    {
        Timeline<RecoilTrack,RecoilClip>.ClearPrepared();
        Timeline<DiagTrack,DiagClip>.ClearPrepared();
        Timeline<SlideTrack,SlideClip>.ClearPrepared();
    }

    [Test]
    public void EveryLaneWordMatchesPinnedV3InBothDirections()
    {
        var oracle = JObject.Parse(Resources.Load<TextAsset>("oracle").text);
        Compare<RecoilTrack,RecoilClip>("recoil", oracle);
        Compare<SlideTrack,SlideClip>("slide", oracle);
        Compare<DiagTrack,DiagClip>("diag", oracle);
        Compare<DiagTrack,DiagClip>("diag-loop", oracle);
    }

    static void Compare<TTrack,TClip>(string name, JObject oracle) where TTrack: unmanaged, IBlend<TClip> where TClip: unmanaged
    {
        using(var asset = Load(name))
        {
            var prepared = Timeline<TTrack,TClip>.Prepare(asset);
            var expected = oracle[name];
            Assert.AreEqual((int)expected["duration"], prepared.View.Duration);
            Assert.AreEqual(3, prepared.View.AbiVersion);
            for(var lane=0;lane<prepared.View.ResultCount;lane++)
            for(ushort tick=0;tick<prepared.View.Duration;tick++)
            {
                Assert.IsTrue(prepared.TryRead(lane,tick,true,out uint forward));
                var position = (ushort)(tick+1==prepared.View.Duration&&prepared.View.Looping!=0 ? 0 : tick+1);
                Assert.IsTrue(prepared.TryRead(lane,position,false,out uint backward));
                Assert.AreEqual((uint)expected["forward"][lane][(int)tick],forward,$"{name}/{lane}/{tick}/forward");
                Assert.AreEqual((uint)expected["backward"][lane][(int)tick],backward,$"{name}/{lane}/{tick}/backward");
            }
            Assert.IsFalse(prepared.TryRead(0,prepared.View.Duration,true,out uint _));
            if(prepared.View.Looping!=0) Assert.IsFalse(prepared.TryRead(0,prepared.View.Duration,false,out uint _));
        }
    }

    static int Lane<T>(PreparedTimeline timeline)
    {
        for(var lane=0;lane<timeline.View.ResultCount;lane++)
            if(timeline.View.LaneKeys[lane]==TypeKey<T>.Value) return lane;
        throw new InvalidOperationException("The typed result is absent.");
    }

    [Test]
    public void TypedWidthsAndNonfiniteBitsArePreserved()
    {
        using(var asset=Load("diag"))
        {
            var prepared=Timeline<DiagTrack,DiagClip>.Prepare(asset);
            prepared.TryRead(Lane<byte>(prepared),2,true,out byte step);
            prepared.TryRead(Lane<short>(prepared),2,true,out short level);
            prepared.TryRead(Lane<int>(prepared),2,true,out int phase);
            prepared.TryRead(Lane<long>(prepared),2,true,out long span);
            prepared.TryRead(Lane<float>(prepared),2,true,out float nan);
            Assert.AreEqual(128,step); Assert.AreEqual(short.MinValue,level);
            Assert.AreEqual(int.MaxValue,phase); Assert.AreEqual(long.MaxValue,span);
            Assert.IsTrue(float.IsNaN(nan));
            prepared.TryRead(Lane<float>(prepared),6,true,out float zero);
            Assert.AreEqual(unchecked((int)0x80000000),BitConverter.SingleToInt32Bits(zero));
            prepared.TryRead(Lane<float>(prepared),14,true,out float infinity);
            Assert.IsTrue(float.IsPositiveInfinity(infinity));
        }
    }

    [Test]
    public void LiveColumnsUseCurrentInputAndDoNotAdvanceClocks()
    {
        using(var first=Load("recoil"))
        using(var second=Load("recoil"))
        {
            Assert.AreEqual(first.Index,second.Index);
            ushort[] indices={first.Index,second.Index}; ushort[] clocks={6,6};
            RecoilFx[] effects={default,default};
            RecoilStrength[] inputs={new RecoilStrength{Value=.5f},new RecoilStrength{Value=2f}};
            var columns=new ColumnSet(); columns.Add<RecoilFx>(effects);columns.Add<RecoilStrength>(inputs);
            Timeline<RecoilTrack,RecoilClip>.Apply<ushort,ushort>(indices,clocks,true,in columns);
            Assert.AreEqual(6,effects[0].Value);Assert.AreEqual(24,effects[1].Value);
            CollectionAssert.AreEqual(new ushort[]{6,6},clocks);
            inputs[0].Value=3;
            effects[0]=default;effects[1]=default;
            Timeline<RecoilTrack,RecoilClip>.Apply<ushort,ushort>(indices,clocks,true,in columns);
            Assert.AreEqual(36,effects[0].Value);
            Timeline.Advance(indices,clocks,true);
            CollectionAssert.AreEqual(new ushort[]{7,7},clocks);
            first.Dispose();
            Assert.DoesNotThrow(()=>Timeline<RecoilTrack,RecoilClip>.Prepare(second));
        }
    }

    [Test]
    public void FiniteAndLoopMovementFollowCallerDirection()
    {
        using(var finite=Load("recoil"))
        using(var loop=Load("diag-loop"))
        {
            var f=Timeline<RecoilTrack,RecoilClip>.Prepare(finite);
            ushort clock=f.View.Duration;f.Advance(ref clock,true);Assert.AreEqual(f.View.Duration,clock);
            clock=0;f.Advance(ref clock,false);Assert.AreEqual(0,clock);
            var l=Timeline<DiagTrack,DiagClip>.Prepare(loop);
            clock=0;l.Advance(ref clock,false);Assert.AreEqual(l.View.Duration-1,clock);
            clock=(ushort)(l.View.Duration-1);l.Advance(ref clock,true);Assert.AreEqual(0,clock);
        }
    }

    [BurstCompile(CompileSynchronously=true,FloatMode=FloatMode.Strict)]
    struct ApplyPrepared : IJob
    {
        [NativeDisableUnsafePtrRestriction] public PreparedTimeline Timeline;
        public int Lane;
        public NativeArray<float> Output;
        public void Execute()
        {
            bool burst=true; ManagedMarker(ref burst);
            Output[1]=burst?1:0;
            if(Timeline.TryRead(Lane,6,true,out float impulse))
            {
                var effect=new RecoilFx();var input=new RecoilStrength{Value=2};
                Recoil.ExecuteActive(in impulse,ref effect,in input);
                Output[0]=effect.Value;
            }
        }
        [BurstDiscard] static void ManagedMarker(ref bool value) { value=false; }
    }

    [Test]
    public void RealBurstJobExecutesSharedConsumerOverPreparedUnmanagedView()
    {
        Assert.IsTrue(BurstCompiler.IsEnabled);
        using(var asset=Load("recoil"))
        using(var output=new NativeArray<float>(2,Allocator.TempJob))
        {
            var prepared=Timeline<RecoilTrack,RecoilClip>.Prepare(asset);
            new ApplyPrepared{Timeline=prepared,Lane=Lane<float>(prepared),Output=output}.Schedule().Complete();
            Assert.AreEqual(24,output[0]);
            Assert.AreEqual(1,output[1],"The job fell back to managed execution.");
        }
    }

    [Test]
    public void WarmApplyAllocatesZeroManagedBytes()
    {
        using(var asset=Load("recoil"))
        {
            ushort[] indices={asset.Index};ushort[] positions={6};
            RecoilFx[] effects={default};RecoilStrength[] inputs={new RecoilStrength{Value=1}};
            var columns=new ColumnSet();columns.Add<RecoilFx>(effects);columns.Add<RecoilStrength>(inputs);
            for(var i=0;i<32;i++)Timeline<RecoilTrack,RecoilClip>.Apply<ushort,ushort>(indices,positions,true,in columns);
            var before=GC.GetAllocatedBytesForCurrentThread();
            for(var i=0;i<1024;i++)Timeline<RecoilTrack,RecoilClip>.Apply<ushort,ushort>(indices,positions,true,in columns);
            Assert.AreEqual(0,GC.GetAllocatedBytesForCurrentThread()-before);
        }
    }

    [Test]
    public void InvalidBytesMissingPairAndDeadOwnershipRejectClearly()
    {
        Assert.Throws<ArgumentException>(()=>TimelineAsset.LoadOwned(new byte[4]));
        using(var asset=Load("recoil"))
            Assert.Throws<ArgumentException>(()=>Timeline<DiagTrack,DiagClip>.Prepare(asset));
        var disposed=Load("recoil");disposed.Dispose();
        Assert.Throws<InvalidOperationException>(()=>{var index=disposed.Index;});
    }

    [UnityTest]
    public IEnumerator PackageOnlyRuntimeSurvivesActualPlayModeFrames()
    {
        using(var asset=Load("recoil"))
        {
            ushort[] positions={6};float[] effects={0};
            Timeline<RecoilTrack,RecoilClip>.Apply(asset.Index,positions,true,effects);
            Assert.AreEqual(12,effects[0]);yield return null;
            Timeline.Advance(asset.Index,positions,true);
            Assert.AreEqual(7,positions[0]);
        }
    }
}
