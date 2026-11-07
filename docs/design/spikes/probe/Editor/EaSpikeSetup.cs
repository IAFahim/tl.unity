using UnityEditor;
public static class EaSpikeSetup
{
    public static void Run()
    {
        const string path = "Assets/SpikeA/SpikeStateGen.dll";
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        var importer = (PluginImporter)AssetImporter.GetAtPath(path);
        importer.SetCompatibleWithAnyPlatform(false);
        importer.SetCompatibleWithEditor(false);
        importer.SaveAndReimport();
        AssetDatabase.SetLabels(AssetDatabase.LoadMainAssetAtPath(path), new[] { "RoslynAnalyzer" });
        AssetDatabase.SaveAssets();
        UnityEngine.Debug.Log("EaSpikeSetup done " + string.Join(",", AssetDatabase.GetLabels(AssetDatabase.LoadMainAssetAtPath(path))));
    }
}
