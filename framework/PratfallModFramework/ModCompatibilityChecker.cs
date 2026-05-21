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
        var enabledManifests = installed.Where(mod => enabledSet.Contains(mod.Id)).ToList();
        var seenPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in enabledManifests)
        {
            foreach (var conflictTargetId in mod.Multiplayer.ConflictsWith)
            {
                if (!enabledSet.Contains(conflictTargetId)) continue;
                var pair = string.CompareOrdinal(mod.Id, conflictTargetId) < 0
                    ? $"{mod.Id}|{conflictTargetId}"
                    : $"{conflictTargetId}|{mod.Id}";
                if (!seenPairs.Add(pair)) continue;
                report.Conflicts.Add(new Conflict
                {
                    ModA = mod.Id,
                    ModB = conflictTargetId,
                    Reason = "declared incompatible by manifest (conflictsWith)",
                });
            }
        }

        // (4) Missing required dependencies.
        foreach (var mod in enabledManifests)
        {
            foreach (var dependencyId in mod.Multiplayer.Requires)
            {
                if (!enabledSet.Contains(dependencyId))
                    report.MissingDependencies.Add(new MissingDependency { ModId = mod.Id, MissingDependencyId = dependencyId });
            }
        }

        // (5) Harmony patch overlaps — only if loaded assemblies were provided.
        // Two-step: collect a target→patches map, then emit warnings from groups of 2+.
        // Extracted into helpers so Check reads as a flat sequence rather than burying
        // a 3-level reflection loop in the middle.
        var patchedTargets = CollectPatchedTargets(loadedAssembliesById, enabledSet);
        EmitPatchOverlapWarnings(patchedTargets, report);

        return report;
    }

    // Walks every loaded mod assembly's [ModPatch] attributes and indexes them by
    // "TargetType::MethodName". Skips assemblies whose mod id isn't currently enabled.
    // Reflection partial-failure tolerance lives in ReflectionHelper.GetTypesSafe —
    // if an assembly can't fully reflect, we use whatever types DID load rather
    // than abandoning the whole assembly.
    private static Dictionary<string, List<(string ModId, PatchType Type)>> CollectPatchedTargets(
        IReadOnlyDictionary<string, Assembly>? loadedAssembliesById,
        HashSet<string> enabledModIds)
    {
        var patchedTargets = new Dictionary<string, List<(string ModId, PatchType Type)>>();
        if (loadedAssembliesById == null || loadedAssembliesById.Count == 0)
            return patchedTargets;

        foreach (var (modId, assembly) in loadedAssembliesById)
        {
            if (!enabledModIds.Contains(modId)) continue;
            foreach (var type in ReflectionHelper.GetTypesSafe(assembly))
            {
                foreach (var attr in type.GetCustomAttributes<ModPatchAttribute>())
                {
                    var key = $"{attr.TargetType.FullName}::{attr.MethodName}";
                    if (!patchedTargets.TryGetValue(key, out var list))
                        patchedTargets[key] = list = new();
                    list.Add((modId, attr.Type));
                }
            }
        }
        return patchedTargets;
    }

    // Emits one Warning per target that 2+ enabled mods patch. Transpiler-involved
    // overlaps get a stronger wording — Harmony transpilers rewrite IL and their
    // ordering can break the other patches' assumptions.
    private static void EmitPatchOverlapWarnings(
        Dictionary<string, List<(string ModId, PatchType Type)>> patchedTargets,
        Report report)
    {
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
        foreach (var conflict in report.Conflicts) warn($"[ModFramework]   {conflict}");
        foreach (var warning in report.Warnings) warn($"[ModFramework]   {warning}");
        foreach (var dependency in report.MissingDependencies) warn($"[ModFramework]   {dependency}");
    }
}
