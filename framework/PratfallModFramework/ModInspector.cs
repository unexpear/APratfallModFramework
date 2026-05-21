using System.Reflection;
using System.Security.Cryptography;

namespace PratfallModFramework;

// User-directed scanner. Builds a structured report on a single mod for the user to
// inspect before trusting/enabling a community-sourced download.
//
// What it shows today (no IL/Cecil dependency):
//   - manifest summary (id, name, version, author, multiplayer mode, requires/conflicts)
//   - file listing (name, byte size, SHA-256) for everything in the mod folder
//   - for already-loaded mods: declared [ModPatch] targets via reflection
//
// What it deliberately does NOT do:
//   - IL inspection (would need Cecil deployed alongside the framework — v1.1 candidate)
//   - actual code execution (we never load the mod just to inspect it)
//   - judgement / verdicts ("this is malicious") — only surfaces facts, user decides
public static class ModInspector
{
    public sealed class Report
    {
        public string ModId = "";
        public string FolderPath = "";
        public ManifestSummary? Manifest;
        public List<FileEntry> Files = new();
        public List<DeclaredPatch> DeclaredPatches = new();
        public bool PatchesAreFromLoadedAssembly;
        public string? LoadStateNote;
        // Absolute path to the mod's Workshop preview image (Preview.png /
        // Preview.jpg / Preview.jpeg, case-insensitive) if one exists at the
        // top of the mod folder. Same convention SteamWorkshopUploader.exe
        // uses for thumbnail upload. Null if no preview is shipped.
        public string? PreviewImagePath;
    }

    public sealed class ManifestSummary
    {
        public string Id = "", Name = "", Version = "", Author = "", Description = "";
        public string EffectiveMode = "";
        public string PinnedSha256 = "";
        public string PckFile = "";
        public IReadOnlyList<string> Requires = Array.Empty<string>();
        public IReadOnlyList<string> ConflictsWith = Array.Empty<string>();
        // Source-of-install info — Workshop-discovered mods get a badge in the
        // mod inspector + can be linked back to their Steam Workshop item.
        public bool IsSteamWorkshopMod;
        public ulong WorkshopId;
    }

    public sealed class FileEntry
    {
        public string FileName = "";
        public long ByteSize;
        public string Sha256Hex = "";
    }

    public sealed class DeclaredPatch
    {
        public string TargetTypeFullName = "";
        public string TargetMethod = "";
        public string PatchType = ""; // Prefix / Postfix / Transpiler
        public string DeclaringTypeFullName = "";
    }

    public static Report Inspect(ModManifest manifest, Assembly? loadedAssembly = null)
    {
        var report = new Report
        {
            ModId = manifest.Id,
            FolderPath = manifest.DirectoryPath,
            Manifest = new ManifestSummary
            {
                Id = manifest.Id,
                Name = manifest.Name,
                Version = manifest.Version,
                Author = manifest.Author,
                Description = manifest.Description,
                EffectiveMode = manifest.GetEffectiveNetworkMode(),
                PinnedSha256 = manifest.AssemblySha256 ?? "",
                PckFile = manifest.PckFile ?? "",
                Requires = manifest.Multiplayer.Requires.ToList(),
                ConflictsWith = manifest.Multiplayer.ConflictsWith.ToList(),
                IsSteamWorkshopMod = manifest.IsSteamWorkshopMod,
                WorkshopId = manifest.WorkshopId,
            },
        };

        ScanFolderFiles(manifest, report);

        if (loadedAssembly != null)
        {
            CollectDeclaredPatches(loadedAssembly, report);
        }
        else
        {
            report.LoadStateNote = "Mod is not currently loaded — patches not inspected. Enable the mod to load it (this runs its OnLoad), then re-inspect.";
        }

        return report;
    }

    // Hashes every file at the mod folder's top level and records (name, size,
    // SHA-256) in the report. Also detects the Workshop preview image (first
    // Preview.png/.jpg/.jpeg found) for the inspector UI's thumbnail. Per-file
    // failures (read errors, locked files) log + continue rather than aborting
    // the whole scan — a single un-hashable file shouldn't blank the report.
    private static void ScanFolderFiles(ModManifest manifest, Report report)
    {
        if (string.IsNullOrWhiteSpace(manifest.DirectoryPath) || !Directory.Exists(manifest.DirectoryPath))
            return;

        foreach (var file in Directory.EnumerateFiles(manifest.DirectoryPath))
        {
            try
            {
                using var sha = SHA256.Create();
                using var fs = File.OpenRead(file);
                var hash = Convert.ToHexString(sha.ComputeHash(fs));
                report.Files.Add(new FileEntry
                {
                    FileName = Path.GetFileName(file),
                    ByteSize = new FileInfo(file).Length,
                    Sha256Hex = hash,
                });

                // Track the first Preview.png / .jpg / .jpeg we find as the
                // mod's Workshop thumbnail (same convention SteamWorkshopUploader.exe
                // uses). Case-insensitive so authors can use any capitalization.
                if (report.PreviewImagePath == null && IsPreviewImageFile(file))
                    report.PreviewImagePath = file;
            }
            catch (Exception ex)
            {
                Godot.GD.PrintErr($"[ModFramework] ModInspector: failed to hash {file}: {ex.Message}");
            }
        }
    }

    // Enumerates [ModPatch] attributes declared in the loaded mod assembly and
    // records (target type, target method, patch kind, declaring type) into
    // the report. Sets PatchesAreFromLoadedAssembly = true regardless of
    // outcome — the flag reflects "we tried", not "we succeeded". An
    // outer-try failure populates LoadStateNote with the error message.
    private static void CollectDeclaredPatches(Assembly loadedAssembly, Report report)
    {
        report.PatchesAreFromLoadedAssembly = true;
        try
        {
            foreach (var type in ReflectionHelper.GetTypesSafe(loadedAssembly))
            {
                foreach (var attr in type.GetCustomAttributes<ModPatchAttribute>())
                {
                    report.DeclaredPatches.Add(new DeclaredPatch
                    {
                        TargetTypeFullName = attr.TargetType.FullName ?? attr.TargetType.Name,
                        TargetMethod = attr.MethodName,
                        PatchType = attr.Type.ToString(),
                        DeclaringTypeFullName = type.FullName ?? type.Name,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            report.LoadStateNote = $"Reflected types but hit error: {ex.Message}";
        }
    }

    // Workshop-thumbnail convention per Tim's modding guide: filename matches
    // Preview.png / Preview.jpg / Preview.jpeg (case-insensitive). Caller has
    // already enumerated the file; we just need the name+extension test.
    private static bool IsPreviewImageFile(string fullPath)
    {
        var nameNoExt = Path.GetFileNameWithoutExtension(fullPath);
        if (!string.Equals(nameNoExt, "Preview", StringComparison.OrdinalIgnoreCase))
            return false;
        var ext = Path.GetExtension(fullPath);
        return string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase);
    }
}
