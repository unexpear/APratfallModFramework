using Godot;
using Godot.Collections;

namespace PratfallModFramework;

public class ModManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "patch";
    public string PackageName { get; set; } = "";
    public string AssemblyFile { get; set; } = "";
    // Optional SHA-256 of the DLL bytes, lowercase hex. When set, the framework refuses to
    // load a DLL whose actual hash differs (defends against tampering or stale files).
    // Omit to opt out of pinning (back-compat for unsigned mods).
    public string AssemblySha256 { get; set; } = "";
    // Optional .pck file to mount onto `res://` when the mod is enabled. Mounted PCKs
    // cannot be unmounted in Godot 4 — disabling such a mod takes effect on next restart.
    public string PckFile { get; set; } = "";
    // Whether to register the mod assembly with Godot's script bridge after loading.
    // True (default, matching the official loader's schema) lets mod-defined Node /
    // Resource types be instantiated from .tscn files or PackedScene.Instantiate.
    // Set to false only if you have a specific reason to skip script registration.
    public bool AddAssemblyToGodot { get; set; } = true;
    public string DirectoryPath { get; set; } = "";
    public string DirectoryName { get; set; } = "";
    public string LoadPolicy { get; set; } = ModLoadPolicies.Auto;
    // True when this manifest was discovered under a Steam Workshop content folder
    // (steamapps/workshop/content/4244510/<id>/). The framework scans Workshop
    // mods alongside local mods so per-mod features (logs, configs, settings UI,
    // crash reports) work identically.
    public bool IsSteamWorkshopMod { get; set; }
    // Steam Workshop published-file ID. 0 for non-Workshop mods. When set, the
    // framework can map back to the Steam item (e.g. to show a "browse on
    // Workshop" link in the Mods dialog, or to detect when the user unsubscribes).
    public ulong WorkshopId { get; set; }
    public ModEffects Effects { get; set; } = new();
    public ModMultiplayer Multiplayer { get; set; } = new();

    public static ModManifest FromJson(string json, string? directoryName = null, string? directoryPath = null)
    {
        var jsonObj = Json.ParseString(json);
        if (jsonObj.VariantType != Variant.Type.Dictionary) return new ModManifest();
        var dict = jsonObj.AsGodotDictionary();
        var hasFrameworkId = dict.ContainsKey("id");
        var hasFrameworkName = dict.ContainsKey("name");
        var hasOfficialName = dict.ContainsKey("Name");
        var hasOfficialAssembly = dict.ContainsKey("Assembly");
        var hasOfficialPackage = dict.ContainsKey("PackageName");
        var hasFrameworkFields = hasFrameworkId || hasFrameworkName || dict.ContainsKey("type") || dict.ContainsKey("effects") || dict.ContainsKey("multiplayer");
        var hasOfficialFields = hasOfficialName || hasOfficialAssembly || hasOfficialPackage;

        var manifest = new ModManifest
        {
            Id = dict.GetValueOrDefault("id", "").AsString(),
            Name = dict.GetValueOrDefault("name", dict.GetValueOrDefault("Name", "")).AsString(),
            Version = dict.GetValueOrDefault("version", dict.GetValueOrDefault("Version", "1.0.0")).AsString(),
            Author = dict.GetValueOrDefault("author", dict.GetValueOrDefault("Author", "")).AsString(),
            Description = dict.GetValueOrDefault("description", dict.GetValueOrDefault("Description", "")).AsString(),
            Type = dict.GetValueOrDefault("type", hasOfficialFields && !hasFrameworkFields ? "official" : "patch").AsString(),
            PackageName = dict.GetValueOrDefault("PackageName", "").AsString(),
            AssemblyFile = dict.GetValueOrDefault("Assembly", "").AsString(),
            AssemblySha256 = dict.GetValueOrDefault("assemblySha256", dict.GetValueOrDefault("AssemblySha256", "")).AsString(),
            PckFile = dict.GetValueOrDefault("pckFile", dict.GetValueOrDefault("PckFile", "")).AsString(),
            AddAssemblyToGodot = dict.ContainsKey("addAssemblyToGodot")
                ? dict["addAssemblyToGodot"].AsBool()
                : (dict.ContainsKey("AddAssemblyToGodot") ? dict["AddAssemblyToGodot"].AsBool() : true),
            DirectoryPath = directoryPath ?? "",
            DirectoryName = directoryName ?? "",
            LoadPolicy = dict.GetValueOrDefault("loadPolicy", dict.GetValueOrDefault("LoadPolicy", hasOfficialFields && !hasFrameworkFields ? ModLoadPolicies.Official : ModLoadPolicies.Auto)).AsString(),
            Effects = ModEffects.FromJson(dict.GetValueOrDefault("effects", new Dictionary()).AsGodotDictionary()),
            Multiplayer = ModMultiplayer.FromJson(dict.GetValueOrDefault("multiplayer", new Dictionary()).AsGodotDictionary())
        };

        // Backward-compatible aliases for earlier manifest drafts.
        if (dict.ContainsKey("networkMode"))
            manifest.Multiplayer.Mode = dict["networkMode"].AsString();
        if (dict.ContainsKey("requires"))
            manifest.Multiplayer.Requires = ModManifestJson.ReadStringList(dict["requires"]);
        if (dict.ContainsKey("conflictsWith"))
            manifest.Multiplayer.ConflictsWith = ModManifestJson.ReadStringList(dict["conflictsWith"]);

        manifest.Normalize();
        return manifest;
    }

    public void Normalize()
    {
        Effects ??= new ModEffects();
        Multiplayer ??= new ModMultiplayer();
        Id = Id.Trim();
        PackageName = PackageName.Trim();
        AssemblyFile = AssemblyFile.Trim();
        DirectoryPath = DirectoryPath.Trim();
        DirectoryName = DirectoryName.Trim();
        LoadPolicy = ModLoadPolicies.Normalize(LoadPolicy);
        if (string.IsNullOrWhiteSpace(Id))
            Id = ResolveImplicitId();
        Name = string.IsNullOrWhiteSpace(Name) ? Id : Name.Trim();
        Version = string.IsNullOrWhiteSpace(Version) ? "1.0.0" : Version.Trim();
        Author = Author.Trim();
        Description = Description.Trim();
        Type = ModManifestJson.NormalizeToken(Type, "patch");
        Effects.Normalize();
        Multiplayer.Normalize();
    }

    public string GetEffectiveNetworkMode()
    {
        if (Type == "official" &&
            string.IsNullOrWhiteSpace(Multiplayer.Mode) &&
            Effects.Settings.Count == 0 &&
            Effects.Patches.Count == 0 &&
            Effects.Assets.Count == 0 &&
            Effects.Nodes.Count == 0)
        {
            return ModNetworkModes.LocalOnly;
        }

        var declaredMode = ModNetworkModes.Normalize(Multiplayer.Mode);
        if (declaredMode != ModNetworkModes.Auto)
            return declaredMode;

        if (Type.Equals("framework", System.StringComparison.OrdinalIgnoreCase))
            return ModNetworkModes.LocalOnly;

        if (Effects.NeedsRestart || Effects.Nodes.Count > 0)
            return ModNetworkModes.RestartRequired;

        if (Effects.Patches.Count > 0 || Effects.Assets.Count > 0)
            return ModNetworkModes.Transfer;

        if (Effects.Settings.Count > 0 || !string.IsNullOrWhiteSpace(Effects.FixedSeedString))
            return ModNetworkModes.Stretch;

        return Type switch
        {
            "settings" => ModNetworkModes.Stretch,
            "patch" => ModNetworkModes.Transfer,
            "asset" => ModNetworkModes.Transfer,
            "content" => ModNetworkModes.Transfer,
            "local_only" => ModNetworkModes.LocalOnly,
            _ => ModNetworkModes.LocalOnly
        };
    }

    public bool DeclaresConflictWith(string modId)
    {
        return ModManifestJson.ContainsIdentifier(Multiplayer.ConflictsWith, modId);
    }

    public bool UsesOfficialLoader()
    {
        // Always false as of 2026-05-18: when Tim shipped Workshop + the
        // modding-fixes update, OfficialModBridge stopped bridging and started
        // turning the native ModManager OFF (it now no-ops LoadAllModManifests).
        // Every mod — including Workshop mods, which arrive with Pratfall's
        // schema and would previously default to LoadPolicy=Official — must
        // now load through OUR pipeline. Returning false here short-circuits
        // every `if (UsesOfficialLoader())` branch in ModManager.cs that used
        // to defer to the bridge; those branches are now dead code that will
        // get pruned in a follow-up cleanup. The LoadPolicy field itself is
        // preserved for back-compat (manifests still parse), but it no longer
        // gates anything at runtime.
        return false;
    }

    private string ResolveImplicitId()
    {
        if (!string.IsNullOrWhiteSpace(DirectoryName))
            return DirectoryName.Trim();

        if (!string.IsNullOrWhiteSpace(AssemblyFile))
            return Path.GetFileNameWithoutExtension(AssemblyFile).Trim();

        if (!string.IsNullOrWhiteSpace(PackageName))
            return Path.GetFileNameWithoutExtension(PackageName).Trim();

        return Name.Trim();
    }
}

public class ModEffects
{
    public List<string> Settings { get; set; } = new();
    public List<ModSettingOverride> SettingOverrides { get; set; } = new();
    public List<string> Patches { get; set; } = new();
    public List<string> Nodes { get; set; } = new();
    public List<string> Assets { get; set; } = new();
    public bool NeedsRestart { get; set; }
    public string FixedSeedString { get; set; } = "";

    public static ModEffects FromJson(Godot.Collections.Dictionary dict)
    {
        var effects = new ModEffects();
        if (dict.ContainsKey("settings"))
        {
            ReadSettings(dict["settings"], effects);
        }
        if (dict.ContainsKey("patches"))
        {
            var arr = dict["patches"].AsGodotArray();
            effects.Patches = arr.Select(v => v.AsString()).ToList();
        }
        if (dict.ContainsKey("nodes"))
        {
            var arr = dict["nodes"].AsGodotArray();
            effects.Nodes = arr.Select(v => v.AsString()).ToList();
            if (effects.Nodes.Count > 0)
                effects.NeedsRestart = true;
        }
        if (dict.ContainsKey("assets"))
        {
            var arr = dict["assets"].AsGodotArray();
            effects.Assets = arr.Select(v => v.AsString()).ToList();
        }
        if (dict.ContainsKey("needsRestart"))
            effects.NeedsRestart = dict["needsRestart"].AsBool();
        if (dict.ContainsKey("fixedSeedString"))
            effects.FixedSeedString = dict["fixedSeedString"].AsString();
        effects.Normalize();
        return effects;
    }

    public void Normalize()
    {
        Settings ??= new List<string>();
        SettingOverrides ??= new List<ModSettingOverride>();
        Patches ??= new List<string>();
        Nodes ??= new List<string>();
        Assets ??= new List<string>();
        Settings = ModManifestJson.NormalizeIdentifiers(Settings);
        SettingOverrides = ModManifestJson.NormalizeSettingOverrides(SettingOverrides);
        Patches = ModManifestJson.NormalizeIdentifiers(Patches);
        Nodes = ModManifestJson.NormalizeIdentifiers(Nodes);
        Assets = ModManifestJson.NormalizeIdentifiers(Assets);
        FixedSeedString = FixedSeedString.Trim();
        if (Nodes.Count > 0)
            NeedsRestart = true;
    }

    private static void ReadSettings(Variant variant, ModEffects effects)
    {
        if (variant.VariantType != Variant.Type.Array)
            return;

        foreach (var value in variant.AsGodotArray())
        {
            if (value.VariantType == Variant.Type.String)
            {
                effects.Settings.Add(value.AsString());
                continue;
            }

            if (value.VariantType != Variant.Type.Dictionary)
                continue;

            var dict = value.AsGodotDictionary();
            var name = dict.GetValueOrDefault("name", dict.GetValueOrDefault("setting", "")).AsString();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            effects.Settings.Add(name);
            var settingOverride = new ModSettingOverride
            {
                Name = name,
                Clear = dict.ContainsKey("clear") && dict["clear"].AsBool()
            };

            if (dict.ContainsKey("value") &&
                ModManifestJson.TryReadSettingValue(dict["value"], out var parsedValue))
            {
                settingOverride.Value = parsedValue;
            }

            effects.SettingOverrides.Add(settingOverride);
        }
    }
}

public class ModSettingOverride
{
    public string Name { get; set; } = "";
    public int? Value { get; set; }
    public bool Clear { get; set; }

    public void Normalize()
    {
        Name = Name.Trim();
    }
}

public class ModMultiplayer
{
    public string Mode { get; set; } = ModNetworkModes.Auto;
    public List<string> Requires { get; set; } = new();
    public List<string> ConflictsWith { get; set; } = new();

    public static ModMultiplayer FromJson(Godot.Collections.Dictionary dict)
    {
        var multiplayer = new ModMultiplayer();
        if (dict.Count == 0)
            return multiplayer;

        if (dict.ContainsKey("mode"))
            multiplayer.Mode = dict["mode"].AsString();
        if (dict.ContainsKey("requires"))
            multiplayer.Requires = ModManifestJson.ReadStringList(dict["requires"]);
        if (dict.ContainsKey("conflictsWith"))
            multiplayer.ConflictsWith = ModManifestJson.ReadStringList(dict["conflictsWith"]);

        multiplayer.Normalize();
        return multiplayer;
    }

    public void Normalize()
    {
        Requires ??= new List<string>();
        ConflictsWith ??= new List<string>();
        Mode = ModNetworkModes.Normalize(Mode);
        Requires = ModManifestJson.NormalizeIdentifiers(Requires);
        ConflictsWith = ModManifestJson.NormalizeIdentifiers(ConflictsWith);
    }
}

public static class ModNetworkModes
{
    public const string Auto = "auto";
    public const string LocalOnly = "local_only";
    public const string Stretch = "stretch";
    public const string Transfer = "transfer";
    public const string RestartRequired = "restart_required";

    public static string Normalize(string? mode)
    {
        var normalized = ModManifestJson.NormalizeToken(mode, Auto);
        return normalized switch
        {
            LocalOnly => LocalOnly,
            Stretch => Stretch,
            Transfer => Transfer,
            RestartRequired => RestartRequired,
            _ => Auto
        };
    }
}

public static class ModLoadPolicies
{
    public const string Auto = "auto";
    public const string Framework = "framework";
    public const string Official = "official";

    public static string Normalize(string? policy)
    {
        var normalized = ModManifestJson.NormalizeToken(policy, Auto);
        return normalized switch
        {
            Framework => Framework,
            Official => Official,
            _ => Auto
        };
    }
}

internal static class ModManifestJson
{
    public static List<string> ReadStringList(Variant variant)
    {
        if (variant.VariantType != Variant.Type.Array)
            return new List<string>();

        return variant.AsGodotArray()
            .Select(v => v.AsString())
            .ToList();
    }

    public static List<string> NormalizeIdentifiers(IEnumerable<string> values)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        foreach (var rawValue in values)
        {
            var value = rawValue?.Trim();
            if (string.IsNullOrWhiteSpace(value) || !seen.Add(value))
                continue;
            result.Add(value);
        }

        return result;
    }

    public static List<ModSettingOverride> NormalizeSettingOverrides(IEnumerable<ModSettingOverride> overrides)
    {
        var result = new List<ModSettingOverride>();
        var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        foreach (var settingOverride in overrides)
        {
            if (settingOverride == null)
                continue;

            settingOverride.Normalize();
            if (string.IsNullOrWhiteSpace(settingOverride.Name))
                continue;

            if (seen.Add(settingOverride.Name))
            {
                result.Add(settingOverride);
                continue;
            }

            var existingIndex = result.FindIndex(overrideEntry =>
                string.Equals(overrideEntry.Name, settingOverride.Name, System.StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
                result[existingIndex] = settingOverride;
        }

        return result;
    }

    public static bool TryReadSettingValue(Variant variant, out int value)
    {
        switch (variant.VariantType)
        {
            case Variant.Type.Bool:
                value = variant.AsBool() ? 1 : 0;
                return true;

            case Variant.Type.Int:
                value = (int)variant.AsInt32();
                return true;

            case Variant.Type.Float:
                var floatValue = variant.AsDouble();
                var rounded = System.Math.Round(floatValue);
                if (System.Math.Abs(floatValue - rounded) <= 0.000001d)
                {
                    value = (int)rounded;
                    return true;
                }

                break;

            case Variant.Type.String:
                if (int.TryParse(variant.AsString(), out value))
                    return true;
                break;
        }

        value = default;
        return false;
    }

    public static string NormalizeToken(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim().ToLowerInvariant();
    }

    public static bool ContainsIdentifier(IEnumerable<string> identifiers, string value)
    {
        return identifiers.Contains(value, System.StringComparer.OrdinalIgnoreCase);
    }
}
