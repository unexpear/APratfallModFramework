using Godot;

namespace PratfallModFramework;

/// <summary>
/// Stretches explicit CustomGameSettings-style mod state to all peers
/// without transferring a DLL package.
/// </summary>
public static class ModNetworkStretch
{
    public static bool CanStretch(ModManifest manifest)
    {
        return manifest.GetEffectiveNetworkMode() == ModNetworkModes.Stretch;
    }

    public static void ApplyStretch(ModManifest manifest)
    {
        if (!CanStretch(manifest))
        {
            GD.Print($"[ModFramework] Mod {manifest.Id} cannot stretch because its effective mode is {manifest.GetEffectiveNetworkMode()}");
            return;
        }

        var manager = CustomGameManager.Instance;
        if (!GodotObject.IsInstanceValid(manager))
        {
            GD.Print($"[ModFramework] Stretch mod {manifest.Id} could not apply because CustomGameManager is not available");
            return;
        }

        manager.Settings ??= new System.Collections.Generic.Dictionary<CustomGameSettings, int>();

        var settingsChanged = false;
        var fixedSeedChanged = false;
        var unknownSettings = new List<string>();
        var unappliedSettings = new List<string>();

        foreach (var settingOverride in manifest.Effects.SettingOverrides)
        {
            if (!TryParseSetting(settingOverride.Name, out var setting))
            {
                unknownSettings.Add(settingOverride.Name);
                continue;
            }

            if (settingOverride.Clear)
            {
                if (manager.Settings.ContainsKey(setting))
                {
                    manager.ClearSetting(setting);
                    settingsChanged = true;
                }

                continue;
            }

            if (settingOverride.Value is int value)
            {
                if (!manager.Settings.TryGetValue(setting, out var existingValue) || existingValue != value)
                {
                    manager.SetSetting(setting, value, notify: false);
                    settingsChanged = true;
                }

                continue;
            }

            unappliedSettings.Add(settingOverride.Name);
        }

        var fixedSeedString = manifest.Effects.FixedSeedString;
        if (!string.IsNullOrWhiteSpace(fixedSeedString) &&
            !string.Equals(manager.FixedSeedString ?? string.Empty, fixedSeedString, StringComparison.Ordinal))
        {
            manager.FixedSeedString = fixedSeedString;
            fixedSeedChanged = true;
        }

        if (unknownSettings.Count > 0)
        {
            GD.Print($"[ModFramework] Stretch mod {manifest.Id} ignored unknown settings: {string.Join(", ", unknownSettings)}");
        }

        if (unappliedSettings.Count > 0)
        {
            GD.Print($"[ModFramework] Stretch mod {manifest.Id} ignored settings without explicit value/clear: {string.Join(", ", unappliedSettings)}");
        }

        if (!settingsChanged && !fixedSeedChanged)
        {
            if (manifest.Effects.Settings.Count > 0 || !string.IsNullOrWhiteSpace(fixedSeedString))
            {
                GD.Print($"[ModFramework] Stretch mod {manifest.Id} did not change local CustomGameSettings");
            }
            else
            {
                GD.Print($"[ModFramework] Stretch mod {manifest.Id} has no concrete stretch payload");
            }

            return;
        }

        manager.OnSettingsChanged?.Invoke();

        GD.Print($"[ModFramework] Applied stretch mod {manifest.Id} via CustomGameManager");
    }

    private static bool TryParseSetting(string name, out CustomGameSettings setting)
    {
        return Enum.TryParse(name, ignoreCase: true, out setting);
    }
}
