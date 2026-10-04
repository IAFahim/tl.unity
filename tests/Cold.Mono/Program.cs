using System;
using System.IO;
using Rifle;
using Tl;

internal static class Program
{
    private static void Main(string[] args)
    {
        if (args.Length != 2 || (args[1] != "cold" && args[1] != "prepared"))
            throw new ArgumentException("Expected recoil.tlb path and cold/prepared mode.");
        using var asset = TimelineAsset.LoadOwned(File.ReadAllBytes(args[0]));
        Span<ushort> indices = stackalloc ushort[] { asset.Index, asset.Index };
        Span<ushort> clocks = stackalloc ushort[] { 6, 6 };
        Span<RecoilFx> effects = stackalloc RecoilFx[2];
        effects.Clear();
        Span<RecoilStrength> inputs = stackalloc RecoilStrength[] { new() { Value = .5f }, new() { Value = 2f } };
        var columns = new ColumnSet();
        columns.Add<RecoilFx>(effects);
        columns.Add<RecoilStrength>(inputs);
        if (args[1] == "prepared") Timeline<RecoilTrack, RecoilClip>.Prepare(asset);
        Timeline<RecoilTrack, RecoilClip>.Apply<ushort, ushort>(indices, clocks, true, in columns);
        if (effects[0].Value != 6 || effects[1].Value != 24 || clocks[0] != 6 || clocks[1] != 6)
            throw new Exception("First Apply produced incorrect effects or moved caller-owned clocks.");
        effects[0] = default;
        effects[1] = default;
        clocks[0] = 7;
        clocks[1] = 7;
        Timeline<RecoilTrack, RecoilClip>.Apply<ushort, ushort>(indices, clocks, false, in columns);
        if (effects[0].Value != -6 || effects[1].Value != -24 || clocks[0] != 7 || clocks[1] != 7)
            throw new Exception("Backward Apply produced incorrect effects or moved caller-owned clocks.");
        clocks[0] = 6;
        clocks[1] = 6;
        for (var i = 0; i < 128; i++)
            Timeline<RecoilTrack, RecoilClip>.Apply<ushort, ushort>(indices, clocks, true, in columns);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1024; i++)
        {
            effects[0] = default;
            effects[1] = default;
            Timeline<RecoilTrack, RecoilClip>.Apply<ushort, ushort>(indices, clocks, true, in columns);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (allocated != 0) throw new Exception("Warm Apply allocated " + allocated + " bytes.");
        Timeline<RecoilTrack, RecoilClip>.ClearPrepared();
        Console.WriteLine("Mono " + args[1] + ": forward=6/24 backward=-6/-24 clocks=6/6 warmAllocatedBytes=" + allocated);
    }
}
