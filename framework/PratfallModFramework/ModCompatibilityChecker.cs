using System.Reflection;

namespace PratfallModFramework;

// Local-set compatibility check across the user's INSTALLED mods. Complementary to
// ModCompatibilityResolver (which handles peer-vs-local mismatches over the network).
//
// Catches three classes of issue:
//   1. Static manifest declarations — `conflictsWith` and `requires`
//   2. Identity collisions — duplicate mod ids, duplicate assembly file names
//   3. Runtime collisions — multiple mods declaring [ModPatch] on the same target
//      method (Harmony will run them all, but ordering can break behavior)
//
// The checker is non-destructive — it produces a Report. The caller decides whether
// to log warnings, surface them in UI, or refuse to apply.
public static class ModCompatibilityChecker
{
    public sealed class Report
    {
        public List<Conflict> Conflicts { get; } = new();
        public List<Warning> Warnings { get; } = new();
        public List<MissingDependency> MissingDependencies { get; } = new();
        public bool HasIssues => Conflicts.Count + Warnings.Count + MissingDependencies.Count > 0;
        public int TotalIssues => Conflicts.Count + Warnings.Count + MissingDependencies.Count;

        public string Summarize() => HasIssues
            ? $"{Conflicts.Count} conflict(s), {Warnings.Count} warning(s), {MissingDependencies.Count} missing dep(s)"
            : "no issues";
    }

    // A hard incompatibility — these mods cannot run alongside each other reliably.
    public sealed class Conflict
    {
        public string ModA { get; init; } = "";
        public string ModB { get; init; } = "";
        public string Reason { get; init; } = "";
        public override string ToString() => $"CONFLICT: {ModA} vs {ModB} ({Reason})";
    }

    // A soft conflict — may work but worth knowing about (e.g. patch overlap).
    public sealed class Warning
    {
        public IReadOnlyList<string> InvolvedMods { get; init; } = Array.Empty<string>();
        public string Detail { get; init; } = "";
        public string Reason { get; init; } = "";
        public override string ToString() => $"WARN: {Detail} ({Reason}; mods: {string.Join(", ", InvolvedMods)})";
    }

    public sealed class MissingDependency
    {
        public string ModId { get; init; } = "";
        public string MissingDependencyId { get; init; } = "";
        public override string ToString() => $"MISSING_DEP: {ModId} requires {MissingDependencyId} (not enabled)";
    }

    public static Report Check(
        IReadOnlyList<ModManifest> installed,
        IReadOnlyCollection<string> enabledIds,
        IReadOnlyDictionary<string, Assembly>? loadedAssembliesById = null)
    {
        var report = new Report();
        var enabledSet = new HashSet<string>(enabledIds, StringComparer.OrdinalIgnoreCase);

        // (1) Duplicate ids across installed manifests.
        foreach (var grp in installed.GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase))
        {
            var list = grp.ToList();
            if (list.Count > 1)
            {
                report.Conflicts.Add(new Conflict
                {
                    ModA = list[0].Id,
                    ModB = list[1].Id,
                    Reason = $"duplicate mod id installed {list.Count} times — only one will load",
                });
            }
        }

        // (2) Duplicate assembly file names (collide on disk).
        foreach (var grp in installed
            .Where(m => !string.IsNullOrWhiteSpace(m.AssemblyFile))
            .GroupBy(m => m.AssemblyFile, StringComparer.OrdinalIgnoreCase))
        {
            var list = grp.ToList();
            if (list.Count > 1)
            {
                report.Conflicts.Add(new Conflict
                {
                    ModA = list[0].Id,
                    ModB = list[1].Id,
                    Reason = $"two mods share assembly file '{grp.Key}' (last to install wins)",
                });
            }
        }

        // (3) Manifest-declared `conflictsWith` between two enabled mods.
        var enabledManifests = installed.Where(m => enabledSet.Contains(m.Id)).ToList();
        var seenPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in enabledManifests)
        {
            foreach (var b in a.Multiplayer.ConflictsWith)
            {
                if (!enabledSet.Contains(b)) continue;
                var pair = string.CompareOrdinal(a.Id, b) < 0 ? $"{a.Id}|{b}" : $"{b}|{a.Id}";
                if (!seenPairs.Add(pair)) continue;
                report.Conflicts.Add(new Conflict
                {
                    ModA = a.Id,
                    ModB = b,
                    Reason = "declared incompatible by manifest (conflictsWith)",
                });
            }
        }

        // (4) Missing required dependencies.
        foreach (var m in enabledManifests)
        {
            foreach (var dep in m.Multiplayer.Requires)
            {
                if (!enabledSet.Contains(dep))
                    report.MissingDependencies.Add(new MissingDependency { ModId = m.Id, MissingDependencyId = dep });
            }
        }

        // (5) Harmony patch overlaps — only if loaded assemblies were provided.
        if (loadedAssembliesById != null && loadedAssembliesById.Count > 0)
        {
            // Map: "Namespace.Type::MethodName" -> list of (modId, patchType)
            var patchedTargets = new Dictionary<string, List<(string ModId, PatchType Type)>>();
            foreach (var (modId, asm) in loadedAssembliesById)
            {
                if (!enabledSet.Contains(modId)) continue;
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
                foreach (var t in types)
                {
                    foreach (var attr in t.GetCustomAttributes<ModPatchAttribute>())
                    {
                        var key = $"{attr.TargetType.FullName}::{attr.MethodName}";
                        if (!patchedTargets.TryGetValue(key, out var list))
                            patchedTargets[key] = list = new();
                        list.Add((modId, attr.Type));
                    }
                }
            }

            foreach (var (target, patches) in patchedTargets)
            {
                var distinctMods = patches.Select(p => p.ModId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (distinctMods.Count < 2) continue;
                var hasTranspiler = patches.Any(p => p.Type == PatchType.Transpiler);
                report.Warnings.Add(new Warning
                {
                    Detail = $"{distinctMods.Count} mods patch {target}",
                    InvolvedMods = distinctMods,
                    Reason = hasTranspiler
                        ? "multiple Harmony patches on same target — TRANSPILER involved, ordering may break behavior"
                        : "multiple Harmony patches on same target — usually safe but may interact",
                });
            }
        }

        return report;
    }

    public static void LogReport(Report report, Action<string>? info = null, Action<string>? warn = null)
    {
        info ??= s => Godot.GD.Print(s);
        warn ??= s => Godot.GD.PrintErr(s);
        if (!report.HasIssues)
        {
            info($"[ModFramework] Compatibility check: {report.Summarize()}");
            return;
        }
        warn($"[ModFramework] Compatibility check: {report.Summarize()}");
        foreach (var c in report.Conflicts) warn($"[ModFramework]   {c}");
        foreach (var w in report.Warnings) warn($"[ModFramework]   {w}");
        foreach (var d in report.MissingDependencies) warn($"[ModFramework]   {d}");
    }
}
