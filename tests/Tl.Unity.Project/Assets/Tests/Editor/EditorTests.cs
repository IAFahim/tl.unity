using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor.Compilation;
using UnityEditor.PackageManager;

namespace Tl.Unity.Tests
{
    public sealed class EditorTests
    {
        [Test]
        public void PlayerGraphContainsRuntimeWithoutCompilerOrRoslyn()
        {
            Assembly runtime = null;
            foreach (var assembly in CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies))
            {
                if (assembly.name == "Tl.Unity")
                    runtime = assembly;
                Assert.IsFalse(Forbidden(assembly.outputPath), assembly.outputPath);
                foreach (var reference in assembly.compiledAssemblyReferences)
                    Assert.IsFalse(Forbidden(reference), reference);
            }
            Assert.IsNotNull(runtime);
            Assert.IsTrue(runtime.sourceFiles.Any(path => path.Replace('\\', '/').EndsWith("/Runtime/Jobs.cs")));
            Assert.IsFalse(runtime.sourceFiles.Any(path => path.Replace('\\', '/').EndsWith("/Runtime/Playback.cs")));
            Assert.IsFalse(runtime.sourceFiles.Any(path => path.Replace('\\', '/').EndsWith("/Runtime/Timeline.cs")));
        }

        [Test]
        public void MaterializedJobsAreCanonicalAndCompileForPlayer()
        {
            var package = PackageInfo.FindForAssetPath("Packages/com.iafahim.tl/package.json");
            Assert.IsNotNull(package);
            Assert.AreEqual("1.0.0-alpha.3", package.version);
            Assert.IsFalse(Directory.Exists(Path.Combine(package.resolvedPath, "Samples~")));
            Assert.IsTrue(CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies)
                .Any(assembly => assembly.name == "Tl.Unity.GeneratedJobs"));
            var report = File.ReadAllText("Assets/Samples/GeneratedJobs/Generated/TlGenCompile.report.txt");
            StringAssert.Contains("catalog\tTl.Samples.GeneratedJobs.Combat\tschemas=3\tassets=4\toperation-kinds=3\tmax-stages=5\tscheduled-jobs-per-step=20", report);
        }

        [Test]
        public void GeneratedAndTestAssembliesEnableCheckedArithmetic()
        {
            var assets = Path.GetFullPath("Assets");
            var files = new[]
            {
                "Player/csc.rsp",
                "Tests/Runtime/csc.rsp"
            };
            foreach (var file in files)
                Assert.AreEqual("-checked+\n", File.ReadAllText(Path.Combine(assets, file)).Replace("\r\n", "\n"));
        }

        private static bool Forbidden(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            return name == "Tl.Compiler"
                || name == "Tl.Gen"
                || name == "Tl.Gen.CSharp"
                || name == "Tl.Gen.Unity"
                || name.StartsWith("Microsoft.CodeAnalysis.")
                || name == "Microsoft.CodeAnalysis";
        }
    }
}
