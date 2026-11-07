using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SpikeA;
using SpikeC;
using Tl;
using Tl.Spike;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using U = Unity.Mathematics;
using T = Tl.Mathematics;

public class SpikeTests
{
    const string Out = "D:/GitHub/_work/ea-spike-results.txt";

    static void Log(string line)
    {
        File.AppendAllText(Out, line + "\n");
        UnityEngine.Debug.Log("Spike: " + line);
    }

    [Test]
    public void SA_GeneratedComponentRegistersAndRunsInBurstJobs()
    {
        Log("SA burst=" + BurstCompiler.IsEnabled + " typeIndex=" + TypeManager.GetTypeIndex<SpikeHealth>() + " isComponent=" + typeof(IComponentData).IsAssignableFrom(typeof(SpikeHealth)));
        using var world = new World("spikeA");
        var em = world.EntityManager;
        for (var i = 0; i < 1000; i++)
        {
            var e = em.CreateEntity(typeof(SpikeHealth));
            em.SetComponentData(e, new SpikeHealth { Value = 1f });
        }
        var chunk = world.CreateSystem<SpikeChunkSystem>();
        var entity = world.CreateSystem<SpikeAGame.SpikeEntitySystem>();
        var query = world.CreateSystem<SpikeAGame.SpikeQuerySystem>();
        SpikeProbe.Managed = 0;
        chunk.Update(world.Unmanaged);
        em.CompleteAllTrackedJobs();
        entity.Update(world.Unmanaged);
        em.CompleteAllTrackedJobs();
        query.Update(world.Unmanaged);
        em.CompleteAllTrackedJobs();
        using var all = em.CreateEntityQuery(typeof(SpikeHealth));
        using var values = all.ToComponentDataArray<SpikeHealth>(Allocator.Temp);
        Log("SA values first=" + values[0].Value + " last=" + values[values.Length - 1].Value + " managedMarks=" + SpikeProbe.Managed);
        Assert.IsTrue(values.ToArray().All(v => v.Value == 104f));
        if (BurstCompiler.IsEnabled)
            Assert.AreEqual(0, SpikeProbe.Managed);
    }

    [Test]
    public void SB_PackCompilesInUnityAndGetsATimelineClip()
    {
        var pack = typeof(SpikeForceClip).Assembly;
        var mirrors = pack.GetTypes().Where(t => typeof(Tl.Unity.TlClipData).IsAssignableFrom(t) && !t.IsAbstract).ToArray();
        var menus = mirrors.Select(t => ((Tl.Unity.TlClipData)Activator.CreateInstance(t)).MenuName).ToArray();
        Log("SB assembly=" + pack.GetName().Name + " location=" + pack.Location + " mirrors=" + string.Join(",", mirrors.Select(m => m.FullName)) + " menus=" + string.Join(",", menus));
        Assert.Contains("Spike Force", menus);
    }

    static UClip[] UClips(int n, out SpikeForceClip[] tClips)
    {
        var u = new UClip[n];
        tClips = new SpikeForceClip[n];
        var rng = new Random(7);
        for (var i = 0; i < n; i++)
        {
            float x = (float)rng.NextDouble() - 0.5f, y = (float)rng.NextDouble(), z = (float)rng.NextDouble() * 3f, s = (float)rng.NextDouble() * 9f;
            var curve = default(TlCurve);
            for (var k = 0; k < TlCurve.Samples; k++) curve[k] = (float)rng.NextDouble();
            u[i] = new UClip { Direction = new U.float3(x, y, z), Strength = s, Falloff = curve };
            tClips[i] = new SpikeForceClip { Direction = new T.float3(x, y, z), Strength = s, Falloff = curve };
        }
        return u;
    }

    static double Time(Action step, int steps)
    {
        var w = Stopwatch.StartNew();
        for (var i = 0; i < steps; i++) step();
        return w.Elapsed.TotalMilliseconds * 1e6 / steps;
    }

    [Test]
    public void SC_TlFloat3MatchesUnityFloat3InBurst()
    {
        const int N = 100000, Warm = 10, Steps = 200;
        var uc = UClips(N, out var tc);
        using var uClips = new NativeArray<UClip>(uc, Allocator.Persistent);
        using var tClips = new NativeArray<SpikeForceClip>(tc, Allocator.Persistent);
        using var uForce = new NativeArray<U.float3>(N, Allocator.Persistent);
        using var tForce = new NativeArray<T.float3>(N, Allocator.Persistent);
        using var uTotal = new NativeArray<U.float3>(uc.Select(c => c.Direction).ToArray(), Allocator.Persistent);
        using var tTotal = new NativeArray<T.float3>(tc.Select(c => c.Direction).ToArray(), Allocator.Persistent);
        Probe.Managed = 0;
        Action ue = () => new UEvaluate { Clips = uClips, Force = uForce, Progress = 0.37f }.Run(N);
        Action te = () => new TEvaluate { Clips = tClips, Force = tForce, Progress = 0.37f }.Run(N);
        Action ua = () => new UApply { Total = uTotal, Force = uForce }.Run(N);
        Action ta = () => new TApply { Total = tTotal, Force = tForce }.Run(N);
        Action uep = () => new UEvaluate { Clips = uClips, Force = uForce, Progress = 0.37f }.ScheduleParallel(N, 256, default).Complete();
        Action tep = () => new TEvaluate { Clips = tClips, Force = tForce, Progress = 0.37f }.ScheduleParallel(N, 256, default).Complete();
        foreach (var a in new[] { ue, te, ua, ta, uep, tep }) for (var i = 0; i < Warm; i++) a();
        var mismatch = 0;
        for (var i = 0; i < N; i++)
            if (BitConverter.SingleToInt32Bits(uForce[i].x) != BitConverter.SingleToInt32Bits(tForce[i].x) || BitConverter.SingleToInt32Bits(uForce[i].y) != BitConverter.SingleToInt32Bits(tForce[i].y) || BitConverter.SingleToInt32Bits(uForce[i].z) != BitConverter.SingleToInt32Bits(tForce[i].z))
                mismatch++;
        Log("SC burst=" + BurstCompiler.IsEnabled + " managedMarks=" + Probe.Managed + " bitMismatches=" + mismatch + " of " + N);
        for (var round = 0; round < 7; round++)
        {
            var swap = round % 2 == 1;
            double eu, et, au, at, pu, pt;
            if (swap) { et = Time(te, Steps); eu = Time(ue, Steps); at = Time(ta, Steps); au = Time(ua, Steps); pt = Time(tep, Steps); pu = Time(uep, Steps); }
            else { eu = Time(ue, Steps); et = Time(te, Steps); au = Time(ua, Steps); at = Time(ta, Steps); pu = Time(uep, Steps); pt = Time(tep, Steps); }
            Log("SC round=" + round + (swap ? " T-first" : " U-first")
                + " evaluateRun U=" + (eu / N).ToString("F3") + " T=" + (et / N).ToString("F3")
                + " applyRun U=" + (au / N).ToString("F3") + " T=" + (at / N).ToString("F3")
                + " evaluateParallel U=" + (pu / N).ToString("F3") + " T=" + (pt / N).ToString("F3") + " ns/entity");
        }
        Assert.AreEqual(0, mismatch);
        if (BurstCompiler.IsEnabled) Assert.AreEqual(0, Probe.Managed);
    }
}
