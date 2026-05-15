using System.Text.Json;
using Godot;

namespace PratfallModFramework;

// Persistent trust policy for mods received over the wire. Stored at
//   user://modframework-trust.json
// The default policy is "open" (accept transferred mods), matching legacy behavior. Users
// who want strict control can flip the mode to "trusted-only", which routes incoming
// transfers into a quarantine folder until the user explicitly allowlists the hash.
public sealed class ModTrustConfig
{
    public const string ConfigPath = "user://modframework-trust.json";
    public const string ModeOpen = "open";
    public const string ModeTrustedOnly = "trusted-only";

    public string Mode { get; set; } = ModeOpen;
    public List<string> TrustedSha256 { get; set; } = new();
    public List<string> TrustedAuthors { get; set; } = new();

    public bool IsTrustedOnly => string.Equals(Mode, ModeTrustedOnly, StringComparison.OrdinalIgnoreCase);

    public bool IsHashTrusted(string sha256Hex)
    {
        if (string.IsNullOrWhiteSpace(sha256Hex)) return false;
        foreach (var trusted in TrustedSha256)
            if (string.Equals(trusted, sha256Hex, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public bool IsAuthorTrusted(string author)
    {
        if (string.IsNullOrWhiteSpace(author)) return false;
        foreach (var trusted in TrustedAuthors)
            if (string.Equals(trusted, author, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public static ModTrustConfig LoadOrDefault()
    {
        var path = ProjectSettings.GlobalizePath(ConfigPath);
        if (!File.Exists(path))
            return new ModTrustConfig();

        try
        {
            var json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<ModTrustConfig>(json, ModNetworkJson.Options);
            cfg ??= new ModTrustConfig();
            cfg.Mode = string.Equals(cfg.Mode, ModeTrustedOnly, StringComparison.OrdinalIgnoreCase) ? ModeTrustedOnly : ModeOpen;
            cfg.TrustedSha256 ??= new List<string>();
            cfg.TrustedAuthors ??= new List<string>();
            return cfg;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] Failed to read trust config at {path}: {ex.Message} (defaulting to open mode)");
            return new ModTrustConfig();
        }
    }

    public void Save()
    {
        var path = ProjectSettings.GlobalizePath(ConfigPath);
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] Failed to save trust config at {path}: {ex.Message}");
        }
    }
}
