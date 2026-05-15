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
    }

    public sealed class ManifestSummary
    {
        public string Id = "", Name = "", Version = "", Author = "", Description = "";
        public string EffectiveMode = "";
        public string PinnedSha256 = "";
        public string PckFile = "";
        public IReadOnlyList<string> Requires = Array.Empty<string>();
        public IReadOnlyList<string> ConflictsWith = Array.Empty<string>();
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
            },
        };

        if (!string.IsNullOrWhiteSpace(manifest.DirectoryPath) && Directory.Exists(manifest.DirectoryPath))
        {
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
                }
                catch (Exception ex)
                {
                    Godot.GD.PrintErr($"[ModFramework] ModInspector: failed to hash {file}: {ex.Message}");
                }
            }
        }

        if (loadedAssembly != null)
        {
            report.PatchesAreFromLoadedAssembly = true;
            try
            {
                Type[] types;
                try { types = loadedAssembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

                foreach (var type in types)
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
        else
        {
            report.LoadStateNote = "Mod is not currently loaded — patches not inspected. Enable the mod to load it (this runs its OnLoad), then re-inspect.";
        }

        return report;
    }
}
