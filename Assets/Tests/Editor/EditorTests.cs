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
            var runtime = false;
            var entities = false;
            foreach (var assembly in CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies))
            {
                runtime |= assembly.name == "Tl.Unity";
                entities |= assembly.name == "Tl.Unity.Entities";
                Assert.IsFalse(Forbidden(assembly.outputPath), assembly.outputPath);
                foreach (var reference in assembly.compiledAssemblyReferences)
                    Assert.IsFalse(Forbidden(reference), reference);
            }
            Assert.IsTrue(runtime);
            Assert.IsTrue(entities);
        }

        [Test]
        public void ExternalBurstCombatIsCanonicalAndCompilesForPlayer()
        {
            var package = PackageInfo.FindForAssetPath("Packages/com.iafahim.tl/package.json");
            Assert.IsNotNull(package);
            Assert.IsFalse(Directory.Exists(Path.Combine(package.resolvedPath, "Samples~")));
            Assert.IsTrue(CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies)
                .Any(assembly => assembly.name == "Tl.Unity.BurstCombat"));
        }

        [Test]
        public void GeneratedAndTestAssembliesEnableCheckedArithmetic()
        {
            var assets = Path.GetFullPath("Assets");
            var files = new[]
            {
                "Player/csc.rsp",
                "Samples/BurstCombat/csc.rsp",
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
