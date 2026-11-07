using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using Tl.Gen.Tlb;

namespace Tl.Unity.Bake.Tests
{
    public sealed class TlBakeTests
    {
        [Test]
        public void PacksTheKindGameFixtureLikeTlb()
        {
            Assert.That(ChildProcessWatch.Supported, "no way to list child processes on this platform");
            var fixture = KindGameFixture.Load();
            var expected = KindGameFixture.TlibTimelines(File.ReadAllBytes(Path.Combine(KindGameFixture.Folder, "game.tlib")));
            Assert.That(expected.Keys.OrderBy(k => k, StringComparer.Ordinal), Is.EqualTo(fixture.Names));
            var baked = new List<byte[]>();
            var path = Environment.GetEnvironmentVariable("PATH");
            var tlb = Environment.GetEnvironmentVariable("TL_TLB");
            IReadOnlyCollection<int> spawned;
            int polls;
            Environment.SetEnvironmentVariable("PATH", "");
            Environment.SetEnvironmentVariable("TL_TLB", null);
            try
            {
                using var watch = new ChildProcessWatch();
                for (var round = 0; round < 20; round++)
                {
                    baked.Clear();
                    foreach (var timeline in fixture.Timelines) baked.Add(TlBake.BakeTyped(timeline, fixture.Manifest));
                }
                watch.Dispose();
                spawned = watch.Spawned;
                polls = watch.Polls;
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", path);
                Environment.SetEnvironmentVariable("TL_TLB", tlb);
            }
            Assert.That(polls, Is.GreaterThan(1));
            Assert.That(spawned, Is.Empty, "child processes started during the in-process bake");
            for (var i = 0; i < fixture.Names.Count; i++)
                Assert.That(baked[i], Is.EqualTo(expected[fixture.Names[i]]), fixture.Names[i]);
        }

        [Test]
        public void AssemblesTheKindGameLibraryLikeTlbFromPerTimelineBakes()
        {
            var fixture = KindGameFixture.Load();
            var game = File.ReadAllBytes(Path.Combine(KindGameFixture.Folder, "game.tlib"));
            var tlbs = fixture.Timelines.Select(t => TlBake.BakeTyped(t, fixture.Manifest)).ToList();
            var catalog = TlBake.Catalog(new[] { KindGameFixture.Game });
            var analyses = fixture.Names.Select((name, i) => TlBake.Analyze(name, tlbs[i], catalog)).ToList();
            Assert.That(Assemble(fixture, tlbs, catalog, analyses, fixture.Settings), Is.EqualTo(game));
            var unset = Assemble(fixture, tlbs, catalog, analyses, new KindSettings());
            var differing = Enumerable.Range(0, Math.Min(game.Length, unset.Length - 32)).Where(i => game[i] != unset[i]).ToList();
            TestContext.Out.WriteLine("S2 without library.json kinds: " + string.Join(", ", Fields(game, differing)));
        }

        [Test]
        public void PacksAndAnalyzesATlKindFedThroughAnITlTrack()
        {
            var timeline = new TypedTimeline { Name = "push", Duration = 10 };
            var track = new TypedTrack { Type = typeof(TlNoSettings<ForceClip>) };
            track.Clips.Add(new TypedClip { Type = typeof(ForceClip), Start = 0, End = 10 });
            timeline.Tracks.Add(track);
            var tlb = TlBake.BakeTyped(timeline);
            var catalog = TlBake.Catalog(new[] { typeof(ForceTrack).Assembly });
            var index = catalog.Kinds.ToList().IndexOf("ForceKind");
            Assert.That(index, Is.GreaterThanOrEqualTo(0), string.Join(", ", catalog.Kinds));
            Assert.That(TlBake.Analyze("push", tlb, catalog).KindMask, Is.EqualTo(1ul << index));
        }

        static byte[] Assemble(KindGameFixture fixture, List<byte[]> tlbs, TlKindCatalog catalog, List<TlKindAnalysis> analyses, KindSettings settings)
        {
            var timelines = analyses.Select(a => a.Value).ToList();
            byte[]? section = null;
            if (timelines.Any(t => t.KindMask != 0))
                section = KindLayout.Section(catalog.Value, timelines, KindLayout.Capacities(catalog.Value, timelines, fixture.Channels.Count, settings));
            return Emit(fixture.Names, tlbs, fixture.TickRate, section);
        }

        static IEnumerable<string> Fields(byte[] tlib, List<int> offsets)
        {
            var kindOff = (int)BitConverter.ToUInt32(tlib, 28);
            var kindCount = (int)BitConverter.ToUInt32(tlib, kindOff + 8);
            var names = new HashSet<string>();
            foreach (var at in offsets)
            {
                var rel = at - kindOff - 64;
                if (kindOff != 0 && rel >= 0 && rel < 40 * kindCount)
                {
                    var row = kindOff + 64 + 40 * (rel / 40);
                    var stringsAt = kindOff + (int)BitConverter.ToUInt32(tlib, kindOff + 32);
                    var name = Encoding.UTF8.GetString(tlib, stringsAt + (int)BitConverter.ToUInt32(tlib, row), (int)BitConverter.ToUInt32(tlib, row + 4));
                    var field = (rel % 40) switch { >= 16 and < 20 => "Capacity", >= 36 => "GroupCapacity", _ => "byte " + rel % 40 };
                    names.Add("kind " + name + " " + field);
                }
                else names.Add(at >= tlib.Length - 32 ? "digest" : "offset " + at);
            }
            return names.OrderBy(n => n, StringComparer.Ordinal);
        }

        static byte[] Emit(List<string> names, List<byte[]> tlbs, uint tickRate, byte[]? kindSection)
        {
            var uniq = new List<byte[]>();
            var uniqOff = new List<uint>();
            var index = new int[names.Count];
            for (var i = 0; i < names.Count; i++)
            {
                var idx = uniq.FindIndex(u => u.AsSpan().SequenceEqual(tlbs[i]));
                if (idx < 0) { idx = uniq.Count; uniq.Add(tlbs[i]); uniqOff.Add(0); }
                index[i] = idx;
            }
            var nameBytes = names.Select(n => Encoding.UTF8.GetBytes(n)).ToList();
            const uint manifestOff = 64;
            var entries = (uint)names.Count * 32u;
            var strings = (uint)nameBytes.Sum(b => b.Length);
            var blobOff = (manifestOff + entries + strings + 7u) & ~7u;
            var cursor = blobOff;
            for (var u = 0; u < uniq.Count; u++) { uniqOff[u] = cursor; cursor += (uint)((uniq[u].Length + 7) & ~7); }
            var kindOff = kindSection == null ? 0u : cursor;
            if (kindSection != null) cursor += (uint)kindSection.Length;
            var total = cursor + 32u;
            var bytes = new byte[total];
            void Put(long at, uint value) => BitConverter.GetBytes(value).CopyTo(bytes, at);
            Put(0, 0x42494C54u);
            Put(4, kindSection == null ? 1u : 2u);
            Put(8, (uint)names.Count);
            Put(12, manifestOff);
            Put(16, blobOff);
            Put(20, total);
            Put(24, tickRate);
            Put(28, kindOff);
            Put(32, kindSection == null ? 0u : (uint)kindSection.Length);
            var nameAt = manifestOff + entries;
            for (var i = 0; i < names.Count; i++)
            {
                var at = manifestOff + (uint)i * 32u;
                var channel = tlbs[i].Length >= 80 && BitConverter.ToUInt32(tlbs[i], 4) == 6u ? BitConverter.ToUInt32(tlbs[i], 56) : 0u;
                Put(at, (uint)(i + 1));
                Put(at + 4, channel);
                Put(at + 8, nameAt);
                Put(at + 12, (uint)nameBytes[i].Length);
                Put(at + 16, uniqOff[index[i]]);
                Put(at + 20, (uint)tlbs[i].Length);
                nameBytes[i].CopyTo(bytes, nameAt);
                nameAt += (uint)nameBytes[i].Length;
            }
            for (var u = 0; u < uniq.Count; u++) uniq[u].CopyTo(bytes, uniqOff[u]);
            kindSection?.CopyTo(bytes, kindOff);
            using var sha = SHA256.Create();
            sha.ComputeHash(bytes, 0, (int)(total - 32u)).CopyTo(bytes, total - 32u);
            return bytes;
        }
    }
}
