using System;
using System.IO;
using UnityEditor;

// Run by the asset-store workflow in batch mode. Unity only gets this far when
// every script in Assets/Hibira compiles, so a successful export also proves
// the package builds in a real Editor.
public static class HibiraAssetStoreExport
{
    const string Folder = "Assets/Hibira";

    public static void Export()
    {
        var output = Argument("-hibiraOutput") ?? "build/Hibira-Realtime.unitypackage";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
        AssetDatabase.ExportPackage(Folder, output, ExportPackageOptions.Recurse);
        if (!File.Exists(output))
            throw new Exception("ExportPackage did not write " + output);
        Console.WriteLine("Exported " + Folder + " to " + output);
    }

    static string Argument(string name)
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
