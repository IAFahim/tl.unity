using System.IO;
using NUnit.Framework;
using UnityEditor.PackageManager;

namespace PlainGame.Tests
{
    public class PlainSampleSyncTests
    {
        static readonly string[] Files =
        {
            "PlainGame.cs",
            "PlainHost.cs",
            "PlainRunner.cs",
            "PlainScript.cs",
            "csc.rsp",
            "Generated/Timelines.g.cs",
        };

        static string Root() => PackageInfo.FindForAssembly(typeof(PlainSampleSyncTests).Assembly).resolvedPath;

        static byte[] Read(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (path.EndsWith(".bytes"))
                return bytes;
            var text = System.Text.Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n");
            return System.Text.Encoding.UTF8.GetBytes(text);
        }

        [Test]
        public void TheSampleSourcesAreTheSameFilesTheTestsCompile()
        {
            var root = Root();
            foreach (var file in Files)
            {
                var sample = Path.Combine(root, "Samples~", "PlainCSharp", file);
                var copy = Path.Combine(root, "Tests", "Plain", "Sample", file);
                Assert.IsTrue(File.Exists(sample), sample);
                Assert.IsTrue(File.Exists(copy), copy);
                CollectionAssert.AreEqual(Read(sample), Read(copy), file);
            }
        }

        [Test]
        public void PackageJsonListsThePlainSample()
        {
            var json = File.ReadAllText(Path.Combine(Root(), "package.json"));
            StringAssert.Contains("\"path\": \"Samples~/PlainCSharp\"", json);
            var asmdef = Path.Combine(Root(), "Samples~", "PlainCSharp", "Tl.Unity.PlainCSharp.asmdef");
            Assert.IsTrue(File.Exists(asmdef));
            StringAssert.Contains("\"Tl.Unity.Generate.Plain\"", File.ReadAllText(asmdef));
            Assert.IsFalse(Directory.Exists(Path.Combine(Root(), "Samples~", "PlainCSharp", "Tools~")));
        }
    }
}
