namespace PratfallModFramework;

// Small path-handling helpers shared by code that walks multiple filesystem
// roots (ManifestManager mod-scan paths, ModZipDropInstaller zip-extract
// paths). Kept tiny on purpose — anything larger belongs in a dedicated
// helper class, not here.
internal static class PathUtil
{
    // Normalize an absolute path (full path + trailing separator stripped,
    // case-insensitive for Windows filesystems) and try to add it to the
    // caller's already-seen set. Returns true if the path is new (not yet
    // seen) so the caller should process it, false if it's a duplicate.
    //
    // Fails permissive: if Path.GetFullPath throws (bad chars, IO error),
    // returns true so the caller can attempt to process the raw path —
    // worst case is a duplicate walk, not a missed scan.
    public static bool TryAddNormalized(HashSet<string> seen, string path)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(path).TrimEnd(
                System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar);
            return seen.Add(full);
        }
        catch
        {
            return true;
        }
    }
}
