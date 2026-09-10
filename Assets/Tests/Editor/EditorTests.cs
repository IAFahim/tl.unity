using System.IO;
using NUnit.Framework;
using UnityEditor.Compilation;

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

        private static bool Forbidden(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            return name == "Tl.Compiler"
                || name == "Tl.Gen.Unity"
                || name == "Microsoft.CodeAnalysis"
                || name == "Microsoft.CodeAnalysis.CSharp";
        }
    }
}
