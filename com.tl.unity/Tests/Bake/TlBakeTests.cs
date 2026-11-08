using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
                    foreach (var timeline in fixture.Timelines) baked.Add(LibraryBake.BakeTyped(timeline, fixture.Manifest));
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
            var tlbs = fixture.Timelines.Select(t => LibraryBake.BakeTyped(t, fixture.Manifest)).ToList();
            var kinds = LibraryBake.Kinds(new[] { KindGameFixture.Game });
            Assert.That(Assemble(fixture, tlbs, kinds, fixture.Settings), Is.EqualTo(game));
            var unset = Assemble(fixture, tlbs, kinds, LibrarySettings.Default);
            var differing = Enumerable.Range(0, Math.Min(game.Length, unset.Length - 32)).Where(i => game[i] != unset[i]).ToList();
            TestContext.Out.WriteLine("S2 without tl.project.json kinds: " + string.Join(", ", Fields(game, differing)));
        }

        [Test]
        public void PacksAndAnalyzesATlKindFedThroughAnITlTrack()
        {
            var timeline = new TypedTimeline { Name = "push", Duration = 10 };
            var track = new TypedTrack { Type = typeof(TlNoSettings<ForceClip>) };
            track.Clips.Add(new TypedClip { Type = typeof(ForceClip), Start = 0, End = 10 });
            timeline.Tracks.Add(track);
            var tlb = LibraryBake.BakeTyped(timeline);
            var kinds = LibraryBake.Kinds(new[] { typeof(ForceTrack).Assembly });
            var index = kinds.Names.ToList().IndexOf("ForceKind");
            Assert.That(index, Is.GreaterThanOrEqualTo(0), string.Join(", ", kinds.Names));
            Assert.That(LibraryBake.KindMask("push", tlb, kinds), Is.EqualTo(1ul << index));
        }

        static byte[] Assemble(KindGameFixture fixture, List<byte[]> tlbs, BakeKinds kinds, LibrarySettings settings)
        {
            settings.ChannelCount = fixture.Channels.Count;
            var parts = fixture.Names.Select((name, i) => new LibraryPart { Name = name, Tlb = tlbs[i], Kinds = LibraryBake.KindPayload(name, tlbs[i], kinds), Next = (ushort)LibraryAssembler.NextOf(tlbs[i]) }).ToList();
            return LibraryAssembler.Assemble(parts, fixture.TickRate, settings);
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
    }
}
