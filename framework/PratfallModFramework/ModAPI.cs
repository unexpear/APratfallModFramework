using Godot;

namespace PratfallModFramework;

public static class ModAPI
{
    public static T? LoadResource<T>(string path) where T : Resource
    {
        return ResourceLoader.Load<T>(path);
    }

    public static void BackupResource(string path)
    {
        var globalized = ProjectSettings.GlobalizePath(path);
        if (!System.IO.File.Exists(globalized)) return;
        var backup = globalized + ".bak";
        if (!System.IO.File.Exists(backup))
            System.IO.File.Copy(globalized, backup);
    }

    public static void RestoreResource(string path)
    {
        var globalized = ProjectSettings.GlobalizePath(path);
        var backup = globalized + ".bak";
        if (!System.IO.File.Exists(backup)) return;
        System.IO.File.Copy(backup, globalized, overwrite: true);
        System.IO.File.Delete(backup);
    }

    public static void SaveResource(Resource resource, string path)
    {
        ResourceSaver.Save(resource, path);
    }
}
