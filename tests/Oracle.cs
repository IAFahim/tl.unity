using System.Text.Json;
using Rifle;
using Tl;
using Ui;

var root = args[0];
var output = new Dictionary<string,object>();
foreach (var source in Directory.GetFiles(root,"*.tlb").Order())
{
#if UNITY_PROFILE
    using var asset=TimelineAsset.LoadOwned(File.ReadAllBytes(source));
#else
    using var asset=TimelineAsset.Of(TimelineAsset.Load(File.ReadAllBytes(source)));
#endif
    var name=Path.GetFileNameWithoutExtension(source);
    if(name.StartsWith("diag")) output[name]=Read<DiagTrack,DiagClip>(asset);
    else if(name.StartsWith("slide")) output[name]=Read<SlideTrack,SlideClip>(asset);
    else
    {
        output[name]=Read<RecoilTrack,RecoilClip>(asset);
        ushort[] ids=[asset.Index,asset.Index];ushort[] clocks=[6,6];
        RecoilFx[] effects=[default,default];RecoilStrength[] inputs=[new(){Value=.5f},new(){Value=2f}];
        var columns=new ColumnSet();columns.Add<RecoilFx>(effects);columns.Add<RecoilStrength>(inputs);
        Timeline<RecoilTrack,RecoilClip>.Apply<ushort,ushort>(ids,clocks,true,in columns);
        if(clocks[0]!=6||clocks[1]!=6)throw new Exception("Apply moved a caller clock.");
        output[name+"Live"]=new {first=effects[0].Value,second=effects[1].Value};
        Timeline.Advance(ids,clocks,true);if(clocks[0]!=7||clocks[1]!=7)throw new Exception("Host advance did not move clocks.");
    }
}
Console.WriteLine(JsonSerializer.Serialize(output));

static object Read<TTrack,TClip>(TimelineAsset asset) where TTrack:unmanaged,IBlend<TClip> where TClip:unmanaged
{
    var view=Timeline<TTrack,TClip>.View(asset.Index);
    var forward=new uint[view.ResultCount][];var backward=new uint[view.ResultCount][];
#if UNITY_PROFILE
    var prepared=new PreparedTimeline(view);
    for(var lane=0;lane<forward.Length;lane++)
    {
        forward[lane]=new uint[view.Duration+1];backward[lane]=new uint[view.Duration+1];
        for(ushort tick=0;tick<view.Duration;tick++)
        {
            if(!prepared.TryRead(lane,tick,true,out uint f))throw new Exception("Active forward tick was rejected.");
            var p=(ushort)(tick+1==view.Duration&&view.Looping!=0?0:tick+1);
            if(!prepared.TryRead(lane,p,false,out uint b))throw new Exception("Active backward tick was rejected.");
            forward[lane][tick]=f;backward[lane][tick]=b;
        }
        if(prepared.TryRead(lane,view.Duration,true,out uint _))throw new Exception("Completed forward clock was active.");
        if(view.Looping!=0&&prepared.TryRead(lane,view.Duration,false,out uint _))throw new Exception("Inactive loop duration clock was active.");
    }
#else
    var copied=Inspection.CopyLanes<TTrack,TClip>(asset.Index)??throw new Exception("Prepared bank missing.");
    forward=copied.Lanes.Select(lane=>lane.Forward).ToArray();backward=copied.Lanes.Select(lane=>lane.Backward).ToArray();
#endif
    return new {duration=view.Duration,looping=view.Looping,abi=view.AbiVersion,forward,backward};
}
