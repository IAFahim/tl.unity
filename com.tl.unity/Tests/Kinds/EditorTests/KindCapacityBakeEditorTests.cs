using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Tl.Unity;
using Tl.Unity.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace KindGame
{
    public class KindCapacityBakeEditorTests
    {
        const string Root = "Assets/TlKindBakeEditorTests";
        const string Folder = Root + "/library";
        const string Output = Root + "/Kinds.g.txt";

        string _scratch;
        string _fake;
        EditorApplication.CallbackFunction _delayCall;

        [SetUp]
        public void SetUp()
        {
            _delayCall = EditorApplication.delayCall;
            EditorApplication.delayCall = null;
            _scratch = Path.GetFullPath("Temp/TlKindBakeEditorTests");
            if (Directory.Exists(_scratch))
                Directory.Delete(_scratch, true);
            Directory.CreateDirectory(_scratch);
            _fake = WriteFake(_scratch);
            if (AssetDatabase.IsValidFolder(Root))
                AssetDatabase.DeleteAsset(Root);
            AssetDatabase.CreateFolder("Assets", "TlKindBakeEditorTests");
            AssetDatabase.CreateFolder(Root, "library");
            Json("a.json");
            SetCapacity(4);
        }

        [TearDown]
        public void TearDown()
        {
            EditorApplication.delayCall = _delayCall;
            AssetDatabase.DeleteAsset(Root);
            if (Directory.Exists(_scratch))
                Directory.Delete(_scratch, true);
        }

        static string WriteFake(string directory)
        {
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                var cmd = Path.Combine(directory, "fake-tlb.cmd");
                File.WriteAllText(cmd,
                    "@echo off\r\n" +
                    "set \"cap=\"\r\n" +
                    "set /p cap=<\"%~dp0capacity.txt\"\r\n" +
                    "set \"cur=\"\r\n" +
                    "if exist \"%~7\" set /p cur=<\"%~7\"\r\n" +
                    "set \"written=false\"\r\n" +
                    "if not \"%cur%\"==\"// capacity %cap%\" (\r\n" +
                    "  >\"%~7\" echo // capacity %cap%\r\n" +
                    "  set \"written=true\"\r\n" +
                    ")\r\n" +
                    "echo {\"timelines\":1,\"kinds\":[{\"name\":\"Damage\",\"worst\":1,\"capacity\":%cap%,\"entryBytes\":8,\"inlineBytes\":32,\"setBy\":\"fake\"}],\"kindsFile\":\"\",\"kindsWritten\":%written%}\r\n");
                return cmd;
            }
            var sh = Path.Combine(directory, "fake-tlb.sh");
            File.WriteAllText(sh,
                "#!/bin/sh\n" +
                "cap=$(head -n 1 \"$(dirname \"$0\")/capacity.txt\")\n" +
                "want=\"// capacity $cap\"\n" +
                "if [ -f \"$7\" ] && [ \"$(head -n 1 \"$7\")\" = \"$want\" ]; then written=false; else printf '%s\\n' \"$want\" > \"$7\"; written=true; fi\n" +
                "printf '{\"timelines\":1,\"kinds\":[{\"name\":\"Damage\",\"worst\":1,\"capacity\":%s,\"entryBytes\":8,\"inlineBytes\":32,\"setBy\":\"fake\"}],\"kindsFile\":\"\",\"kindsWritten\":%s}\\n' \"$cap\" \"$written\"\n");
            var chmod = new ProcessStartInfo("chmod") { UseShellExecute = false, CreateNoWindow = true };
            chmod.ArgumentList.Add("+x");
            chmod.ArgumentList.Add(sh);
            using (var process = Process.Start(chmod))
                process.WaitForExit();
            return sh;
        }

        void SetCapacity(int capacity)
        {
            File.WriteAllText(Path.Combine(_scratch, "capacity.txt"), capacity + "\n");
        }

        static void Json(string name)
        {
            File.WriteAllText(Path.Combine(TimelineKindSets.PhysicalPath(Folder), name), "{}");
            AssetDatabase.ImportAsset(Folder + "/" + name);
        }

        TimelineLibraryAsset Library(string name, string kindsFile, string tlb = null, string folder = Folder)
        {
            var library = ScriptableObject.CreateInstance<TimelineLibraryAsset>();
            library.Folder = folder;
            library.TimelineIds = "KindGame.Timelines";
            library.GameAssembly = "Tl.Unity.Kinds";
            library.Namespace = "KindGame";
            library.KindsFile = kindsFile;
            library.Tlb = tlb ?? _fake;
            AssetDatabase.CreateAsset(library, Root + "/" + name + ".asset");
            return library;
        }

        static string Physical(string path) => TimelineKindSets.PhysicalPath(path);

        static string Written(string path) => File.ReadAllText(Physical(path)).Trim();

        static void Postprocess(params string[] imported)
        {
            var type = typeof(KindCapacityBake).Assembly.GetType("Tl.Unity.Editor.KindCapacityPostprocessor", true);
            var method = type.GetMethod("OnPostprocessAllAssets", BindingFlags.Static | BindingFlags.NonPublic);
            method.Invoke(null, new object[] { imported, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>() });
        }

        static void AfterReload()
        {
            var type = typeof(KindCapacityBake).Assembly.GetType("Tl.Unity.Editor.KindCapacityAfterCompile", true);
            type.GetMethod("OnReload", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
        }

        [Test]
        public void BakeRefreshesTheAssetDatabaseOnlyWhenACapacityChanged()
        {
            var library = Library("Lib", Output);

            var first = KindCapacityBake.Bake(library);
            Assert.IsTrue(first.kindsWritten);
            Assert.AreEqual(4, first.kinds[0].capacity);
            Assert.AreEqual("// capacity 4", Written(Output));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<TextAsset>(Output), "a written Kinds file is imported by the refresh");

            var marker = Root + "/Marker.txt";
            File.WriteAllText(Physical(marker), "marker");
            var stamp = File.GetLastWriteTimeUtc(Physical(Output));
            var second = KindCapacityBake.Bake(library);
            Assert.IsFalse(second.kindsWritten);
            Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(Physical(Output)));
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<TextAsset>(marker), "an unchanged bake must not refresh the asset database");

            SetCapacity(2);
            var third = KindCapacityBake.Bake(library);
            Assert.IsTrue(third.kindsWritten);
            Assert.AreEqual(2, third.kinds[0].capacity);
            Assert.AreEqual("// capacity 2", Written(Output));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<TextAsset>(marker), "a changed bake refreshes the asset database");
        }

        [Test]
        public void BakeReportsTheCapacitiesItWasGiven()
        {
            var result = KindCapacityBake.Bake(Library("Lib", Output));
            StringAssert.Contains("Damage: capacity 4 (worst 1, set by fake), 32 B inline", result.Report());
        }

        [Test]
        public void AnImportedLibraryJsonDefersTheBakeToDelayCall()
        {
            Library("Lib", Output);
            Postprocess(Folder + "/a.json");
            Assert.IsFalse(File.Exists(Physical(Output)), "the import callback must not run tlb itself");
            var deferred = EditorApplication.delayCall;
            Assert.IsNotNull(deferred, "a library .json import queues a delayCall");

            deferred.Invoke();
            Assert.AreEqual("// capacity 4", Written(Output));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<TextAsset>(Output));
        }

        [Test]
        public void ImportsOutsideTheLibraryJsonQueueNothing()
        {
            Library("Lib", Output);
            File.WriteAllText(Path.Combine(TimelineKindSets.PhysicalPath(Folder), "notes.txt"), "text");
            AssetDatabase.ImportAsset(Folder + "/notes.txt");
            File.WriteAllText(Path.Combine(TimelineKindSets.PhysicalPath(Root), "other.json"), "{}");
            AssetDatabase.ImportAsset(Root + "/other.json");
            Postprocess(Folder + "/notes.txt", Root + "/other.json", Root + "/Lib.asset");
            Assert.IsNull(EditorApplication.delayCall);
        }

        [Test]
        public void ALibraryWithoutAKindsFileIsNeverBakedOnImport()
        {
            Library("Lib", string.Empty);
            Postprocess(Folder + "/a.json");
            Assert.IsNull(EditorApplication.delayCall);
        }

        [Test]
        public void BakeAllBakesEveryLibraryWithAKindsFileAndLogsTheOnesThatFail()
        {
            Library("Good", Output);
            Library("NoKinds", string.Empty);
            Library("Broken", Root + "/Broken.g.txt", Path.Combine(_scratch, "no-such-tlb"));
            LogAssert.Expect(LogType.Error, new Regex("Tl kind capacities: library 'Broken': could not run"));
            KindCapacityBake.BakeAll();
            Assert.AreEqual("// capacity 4", Written(Output));
            Assert.IsFalse(File.Exists(Physical(Root + "/Broken.g.txt")));
        }

        [Test]
        public void TheAfterReloadHookBakesEveryLibrary()
        {
            Library("Lib", Output);
            AfterReload();
            Assert.AreEqual("// capacity 4", Written(Output));
            SetCapacity(3);
            AfterReload();
            Assert.AreEqual("// capacity 3", Written(Output));
        }

        [Test]
        public void ARunWithoutAKindsFileOrNamespaceIsRejected()
        {
            var empty = Assert.Throws<InvalidOperationException>(() => KindCapacityBake.Run(_fake, Physical(Folder), "KindGame", string.Empty, "x.dll"));
            StringAssert.Contains("Kinds File is empty", empty.Message);
            var unnamed = Assert.Throws<InvalidOperationException>(() => KindCapacityBake.Run(_fake, Physical(Folder), string.Empty, Physical(Output), "x.dll"));
            StringAssert.Contains("Namespace is empty", unnamed.Message);
        }
    }
}
