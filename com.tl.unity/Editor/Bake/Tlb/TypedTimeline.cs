namespace Tl.Gen.Tlb;

public sealed class TypedTimeline
{
    public string? Name { get; set; }
    public uint Duration { get; set; }
    public bool Loops { get; set; }
    public bool Hold { get; set; }
    public string? Next { get; set; }
    public string? Channel { get; set; }
    public List<TypedTrack> Tracks { get; } = new();
    public List<TypedMarker> Markers { get; } = new();
}

public sealed class TypedTrack
{
    public string? Name { get; set; }
    public Type Type { get; set; } = null!;
    public byte[]? Data { get; set; }
    public List<TypedClip> Clips { get; } = new();
}

public sealed class TypedClip
{
    public string? Name { get; set; }
    public Type Type { get; set; } = null!;
    public uint Start { get; set; }
    public uint End { get; set; }
    public byte[]? Data { get; set; }
}

public sealed class TypedMarker
{
    public Type Type { get; set; } = null!;
    public uint Tick { get; set; }
    public byte[]? Data { get; set; }
}

internal static class TypedBake
{
    internal static byte[] Bake(TypedTimeline timeline, BakerAssemblyResolver resolver, LibraryManifest? manifest)
    {
        var doc = new FastDoc
        {
            Resolver = resolver,
            RootName = timeline.Name,
            Duration = timeline.Duration,
            Loops = timeline.Loops,
            Hold = timeline.Hold,
            NextName = timeline.Next,
            ChannelName = timeline.Channel,
            Manifest = manifest,
            Utf8 = [],
        };
        for (var ti = 0; ti < timeline.Tracks.Count; ti++)
        {
            var track = timeline.Tracks[ti];
            if (track.Type == null)
                throw new BakeDiagnosticException($"missing track type: track {ti} has no type.");
            var info = new FastTrackInfo
            {
                Name = track.Name,
                TrackType = track.Type,
                HaveType = true,
                PopulateDone = true,
                TrackBytes = TimelineBakerFast.IsUnmanagedCached(track.Type) ? Sized(track.Type, track.Data, $"track {ti} ({track.Type.Name})") : null,
            };
            doc.Tracks.Add(info);
            for (var ci = 0; ci < track.Clips.Count; ci++)
            {
                var source = track.Clips[ci];
                if (source.Type == null)
                    throw new BakeDiagnosticException($"missing clip type: clip {ci} on track {ti} has no type.");
                var clip = new FastClip
                {
                    Start = source.Start,
                    End = source.End,
                    ClipIndex = ci,
                    AuthoredIndex = doc.Clips.Count,
                    Name = source.Name,
                    ClipType = source.Type,
                    PopulateDone = true,
                };
                clip.PairId = TimelineBakerFast.EnsurePair(doc, track.Type, source.Type);
                if (clip.PairId >= 0)
                {
                    var pair = doc.Pairs[clip.PairId];
                    var bytes = Sized(source.Type, source.Data, $"clip {ci} on track {ti} ({source.Type.Name})");
                    pair.EnsurePool(pair.ClipSize);
                    clip.PayloadOffset = pair.PoolLength;
                    pair.PoolLength += pair.ClipSize;
                    bytes.CopyTo(pair.Pool, clip.PayloadOffset);
                }
                info.ClipIds.Add(doc.Clips.Count);
                doc.Clips.Add(clip);
            }
        }
        for (var mi = 0; mi < timeline.Markers.Count; mi++)
        {
            var source = timeline.Markers[mi];
            if (source.Type == null)
                throw new BakeDiagnosticException($"missing marker type: marker {mi} has no type.");
            doc.Markers.Add(new FastMarker
            {
                MarkerType = source.Type,
                Tick = source.Tick,
                Payload = TimelineBakerFast.IsUnmanagedCached(source.Type) ? Sized(source.Type, source.Data, $"marker {mi} ({source.Type.Name})") : [],
            });
        }
        return TimelineBakerFastCore.BakeFast(doc, resolver);
    }

    static byte[] Sized(Type type, byte[]? data, string context)
    {
        var size = TimelineBakerFast.HelperFor(type).Size;
        if (data == null)
            return new byte[size];
        if (data.Length != size)
            throw new BakeDiagnosticException($"wrong data size for {context}: {data.Length} bytes, the type is {size} bytes.");
        return (byte[])data.Clone();
    }
}
