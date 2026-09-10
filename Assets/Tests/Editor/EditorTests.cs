using System.IO;
using NUnit.Framework;
using UnityEditor.Compilation;
using UnityEditor.PackageManager;
using UnityEngine;

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
        public void ImportedBurstCombatMatchesPackage()
        {
            var package = PackageInfo.FindForAssetPath("Packages/com.iafahim.tl/package.json");
            Assert.IsNotNull(package);
            var packageSample = Path.Combine(package.resolvedPath, "Samples~", "BurstCombat");
            var importedSample = Path.Combine(Application.dataPath, "Samples", "Tl", package.version, "Burst Combat");
            var names = new[]
            {
                "Attack.Generated.cs",
                "AttackAuthoring.cs",
                "AttackBlob.Generated.cs",
                "AttackSystem.cs",
                "Combat.cs",
                "README.md",
                "Tl.Unity.BurstCombat.asmdef"
            };
            foreach (var name in names)
                CollectionAssert.AreEqual(
                    File.ReadAllBytes(Path.Combine(packageSample, name)),
                    File.ReadAllBytes(Path.Combine(importedSample, name)),
                    name);
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
