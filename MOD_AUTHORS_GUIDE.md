# Pratfall Mod Authors Guide

Practical guide for writing mods that target [Pratfall](https://store.steampowered.com/app/4244510/Pratfall/). Covers the official mod loader (Tim's, shipped with the game) AND the Pratfall Mod Framework (this repo). Each recipe shows the vanilla approach (works with just Pratfall) and the framework-helper approach (works only when this framework is installed) so you can pick what fits your distribution model.

> If you only ever want your mod to work for users who have the framework installed, prefer the helpers — they handle edge cases (filesystem URIs, cleanup, subscription accounting) you'd otherwise need to write yourself. If you want a mod that works for users on either loader with zero framework dependency, use the vanilla pattern.

## Contents

1. [Setup — csproj, manifest, folder layout](#setup)
2. [Lifecycle — OnLoad / OnUnload](#lifecycle)
3. [Two loaders, one mod](#two-loaders-one-mod)
4. [Recipe: Harmony patches](#recipe-harmony-patches)
5. [Recipe: Add a language to the in-game selector](#recipe-add-a-language)
6. [Recipe: Persist mod data alongside the save](#recipe-persist-mod-data)
7. [Recipe: Listen to game events](#recipe-listen-to-game-events)
8. [Recipe: Show HUD button hints](#recipe-show-hud-button-hints)
9. [Recipe: Extend a random drop pool](#recipe-extend-a-drop-pool)
10. [Recipe: Custom Godot Node / Resource types](#recipe-custom-godot-types)
11. [Decoded Pratfall surface inventory](#decoded-pratfall-surface-inventory)
12. [Pitfalls + things to know](#pitfalls)
13. [Resources](#resources)

---

## Setup

Minimal mod project shape:

```
MyMod/
├── MyMod.csproj
├── manifest.json
└── MyMod.cs
```

**`MyMod.csproj`** — `$(MSBuildProgramFiles32)` resolves to the right x86 Program Files path on non-English Windows. Override `GameDir` on the command line if your Steam library is on another drive:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>MyMod</AssemblyName>
    <RootNamespace>MyMod</RootNamespace>
    <ModId>MyMod</ModId>
    <GameDir Condition="'$(GameDir)' == ''">$(MSBuildProgramFiles32)\Steam\steamapps\common\Pratfall</GameDir>
    <UserModsDir>$(APPDATA)\Pratfall\mods</UserModsDir>
    <OutputPath>bin\$(Configuration)\</OutputPath>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
  </PropertyGroup>

  <ItemGroup>
    <Reference Include="GodotSharp">
      <HintPath>$(GameDir)\data_Pratfall_windows_x86_64\GodotSharp.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="Pratfall">
      <HintPath>$(GameDir)\data_Pratfall_windows_x86_64\Pratfall.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <!-- Optional: only when using the framework helpers -->
    <Reference Include="PratfallModFramework">
      <HintPath>$(GameDir)\data_Pratfall_windows_x86_64\PratfallModFramework.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>

  <Target Name="InstallMod" AfterTargets="Build">
    <MakeDir Directories="$(UserModsDir)\$(ModId)" />
    <Copy SourceFiles="$(TargetPath)" DestinationFolder="$(UserModsDir)\$(ModId)" />
    <Copy SourceFiles="manifest.json" DestinationFolder="$(UserModsDir)\$(ModId)" />
  </Target>
</Project>
```

**`manifest.json`** — schema is compatible with both Tim's loader AND the framework (dual-key parsing, camelCase + PascalCase both work):

```json
{
  "id": "MyMod",
  "name": "My Cool Mod",
  "version": "1.0.0",
  "author": "you",
  "description": "Does something cool.",
  "type": "patch",
  "Assembly": "MyMod.dll",
  "AutoLoad": false,
  "AddAssemblyToGodot": true,
  "effects": { "settings": [], "patches": [], "nodes": [], "assets": [], "needsRestart": false },
  "multiplayer": { "mode": "local_only", "requires": [], "conflictsWith": [] }
}
```

Key fields:
- `id` — must match the folder name (avoid collisions, see [Pitfalls](#pitfalls)).
- `Assembly` — DLL filename in the mod folder.
- `AutoLoad` (default `false`) — Tim's loader auto-enables on launch even if previously disabled. The framework respects this too.
- `AddAssemblyToGodot` (default `true`) — registers your mod's types with Godot's script bridge so custom `Node` / `Resource` subclasses work from `.tscn` and `PackedScene.Instantiate`. Don't set false unless you know why.
- `multiplayer.mode` — `local_only` if your mod doesn't affect networked state (single-player tweaks); `transfer` if peers need the actual DLL; `stretch` for settings-only mods.

## Lifecycle

Both loaders look for these on your mod's types (any static class with the right signatures):

```csharp
public static class MyMod
{
    public static void OnLoad()
    {
        // Mod was enabled. Subscribe to events, register helpers, mount resources.
        // Pair every Register / Subscribe / += with the corresponding undo in OnUnload.
    }

    public static void OnUnload()
    {
        // Mod was disabled. Tear down everything OnLoad set up.
        // Mods can be enabled + disabled multiple times per session — be reentrant.
    }
}
```

The framework calls these via reflection (no interface required). Tim's loader uses `[ModEntry]`/`ModInit`/`ModDestroy` instead — see [his example mod](https://github.com/quad-head/pratfall-example-mod) for that flavor. The framework also tolerates Tim-style entry points via the official-loader bubble.

## Two loaders, one mod

| | Pratfall's official loader (Tim) | This framework |
|---|---|---|
| Auto-loads `AutoLoad: true` mods at startup | ✓ | ✓ (defers to game's enabled state) |
| Honors `AddAssemblyToGodot` (script bridge) | ✓ | ✓ (matches the official behavior) |
| Multiplayer sync (vote / transfer / compat) | — | ✓ |
| User-check gate (mods stay off until reviewed) | — | ✓ |
| IL safety scanner (🔍) | — | ✓ |
| Per-peer Download / Stretch / Decline prompt | — | ✓ |
| Conflict resolution between mods | — | ✓ |
| Hides own UI via `--qh-disable-mod-ui` | ✓ (CLI flag) | ✓ (Harmony patches the getter, always-on) |

You don't have to pick — the framework runs alongside Tim's loader, not instead of it. If both are present, mods that target only the official-loader API still work (the framework "bubbles" around it).

## Recipe: Harmony patches

The most common mod pattern. Patch an existing Pratfall method to add/change behavior.

**Vanilla (just Pratfall + Harmony):**

```csharp
using HarmonyLib;
using Godot;

public static class MyMod
{
    private static readonly Harmony _harmony = new("com.you.mymod");

    public static void OnLoad()
    {
        var target = AccessTools.Method(typeof(PlayerPickaxeComponent), "TriggerPrimaryAction");
        var postfix = new HarmonyMethod(typeof(MyMod).GetMethod(nameof(OnPickaxe), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic));
        _harmony.Patch(target, postfix: postfix);
    }

    public static void OnUnload() => _harmony.UnpatchAll(_harmony.Id);

    private static void OnPickaxe(PlayerPickaxeComponent __instance) => GD.Print("swung!");
}
```

**Framework helper (`[ModPatch]` attribute):**

```csharp
using PratfallModFramework;
using Godot;

[ModPatch(typeof(PlayerPickaxeComponent), "TriggerPrimaryAction", PatchType.Postfix)]
public static class MyMod
{
    static void Postfix(PlayerPickaxeComponent __instance) => GD.Print("swung!");
}
```

Framework's `ModAssemblyLoader` scans every type with `[ModPatch]`, applies the patch on enable, and unpatches on disable. No Harmony imports in your mod source.

## Recipe: Add a language

Add a locale to the in-game language selector. Pratfall's `LocalizationManager` has native support — it scans `<userData>/localization/_*.json` and registers anything it finds.

**Vanilla:**

```csharp
using System.Text.Json;
using System.IO;
using Godot;

public static class MyLocalizationMod
{
    public static void OnLoad()
    {
        // Resolve the user-data localization folder. GetUserDataPath() returns a
        // Godot user:// URI — pass it through ProjectSettings.GlobalizePath to
        // get a real filesystem path System.IO can read.
        var raw = Game.Platform?.GetUserDataPath();
        if (string.IsNullOrEmpty(raw)) return;
        var folder = System.IO.Path.Combine(ProjectSettings.GlobalizePath(raw), "localization");
        Directory.CreateDirectory(folder);

        // Filename MUST start with `_` and end with `.json` (Pratfall's loader filter).
        var path = Path.Combine(folder, "_MyLocalizationMod_es_419.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, string>
        {
            { "MAIN_MENU_PLAY", "Jugar" },
            { "MAIN_MENU_OPTIONS", "Opciones" },
        }));

        LocalizationManager.Instance?.LoadUserLocalizations();
    }

    public static void OnUnload()
    {
        var raw = Game.Platform?.GetUserDataPath();
        if (string.IsNullOrEmpty(raw)) return;
        var path = Path.Combine(ProjectSettings.GlobalizePath(raw), "localization", "_MyLocalizationMod_es_419.json");
        if (File.Exists(path)) File.Delete(path);
        LocalizationManager.Instance?.LoadUserLocalizations();
    }
}
```

**Framework helper:**

```csharp
using PratfallModFramework;

public static class MyLocalizationMod
{
    private static IDisposable? _registration;

    public static void OnLoad()
    {
        _registration = ModLocalizationHelper.Register(
            modId: "MyLocalizationMod",
            localeCode: "es_419",
            translations: new Dictionary<string, string>
            {
                { "MAIN_MENU_PLAY", "Jugar" },
                { "MAIN_MENU_OPTIONS", "Opciones" },
            });
    }

    public static void OnUnload() => _registration?.Dispose();
}
```

**Why use the helper:** filesystem URI conversion is handled, filename convention is enforced (the leading `_` is mandatory), Dispose cleans up the file AND triggers a rescan so the locale disappears from the selector.

> **Note for csv users:** Pratfall's loader reads JSON, not CSV. If your existing tooling produces CSV, convert to JSON before writing.

## Recipe: Persist mod data

Save mod state alongside the game's save. Pratfall's `SavegameManager` fires `OnGameWillSave` whenever the game saves — subscribe to that to flush your data.

**Vanilla:**

```csharp
using System.Text.Json;
using System.IO;
using Godot;

public static class MyMod
{
    private static MyState _state = new();
    private static SavegameManager.SaveDataCallback? _saveHook;
    private static string SavePath => Path.Combine(
        ProjectSettings.GlobalizePath(Game.Platform!.GetUserDataPath()),
        "mymod-state.json");

    public static void OnLoad()
    {
        // Restore prior state.
        if (File.Exists(SavePath))
            _state = JsonSerializer.Deserialize<MyState>(File.ReadAllText(SavePath)) ?? new();

        // Flush on every save.
        _saveHook = () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SavePath)!);
            File.WriteAllText(SavePath, JsonSerializer.Serialize(_state));
        };
        SavegameManager.OnGameWillSave += _saveHook;
    }

    public static void OnUnload()
    {
        if (_saveHook != null) SavegameManager.OnGameWillSave -= _saveHook;
        _saveHook = null;
    }

    public class MyState { public int Counter = 0; }
}
```

**Framework helper:**

```csharp
using System.Text.Json;
using PratfallModFramework;

public static class MyMod
{
    private const string Id = "MyMod";
    private static MyState _state = new();
    private static IDisposable? _saveHook;

    public static void OnLoad()
    {
        var prior = ModSaveDataHelper.LoadIfPresent(Id);
        if (prior != null) _state = JsonSerializer.Deserialize<MyState>(prior) ?? new();

        _saveHook = ModSaveDataHelper.Register(Id,
            serialize: () => JsonSerializer.Serialize(_state));
    }

    public static void OnUnload() => _saveHook?.Dispose();

    public class MyState { public int Counter = 0; }
}
```

**Why use the helper:** file path resolution, directory creation, error handling around the serializer, and subscription/unsubscription bookkeeping are all done for you. Mod data lives at `<userData>/modframework-saves/<modId>.json` — predictable per-mod path that won't collide.

## Recipe: Listen to game events

Pratfall publishes events via `GameEventBus.SendEvent<T>(GameplayTag tag, T eventData)`. Subscribers can filter by tag.

**Vanilla:**

```csharp
using Godot;

public static class MyMod
{
    private static GameEventReceived? _sub;

    public static void OnLoad()
    {
        _sub = (tag, ev) =>
        {
            if (tag?.Tag != "Player.Death") return;   // filter manually
            GD.Print($"a player died: {ev}");
        };
        GameEventBus.OnGameEventReceived += _sub;
    }

    public static void OnUnload()
    {
        if (_sub != null) GameEventBus.OnGameEventReceived -= _sub;
        _sub = null;
    }
}
```

**Framework helper:**

```csharp
using PratfallModFramework;

public static class MyMod
{
    private static IDisposable? _sub;

    public static void OnLoad()
    {
        _sub = ModGameEventHelper.SubscribeToTag("Player.Death",
            (tag, ev) => Godot.GD.Print($"a player died: {ev}"));
    }

    public static void OnUnload() => _sub?.Dispose();
}
```

**Why use the helper:** tag filtering is built in, exceptions in your handler are caught + logged with mod context (so a bug in your handler doesn't take down other subscribers), and Dispose handles cleanup. Also offers `SubscribeAll` for catch-all use cases like logging/analytics.

## Recipe: Show HUD button hints

Add a button-prompt entry to Pratfall's HUD button-prompt bar (e.g. *"Press [A] to Equip"*).

**Vanilla:**

```csharp
using Godot;

public static class MyMod
{
    private const string Context = "MyMod_Inventory";

    public static void OnLoad()
    {
        var bar = ButtonPrompBarController.Instance;
        if (bar == null) return;
        bar.AddButtonPrompt(new ButtonPromptOptions
        {
            ActionName = "ui_accept",
            Description = "Equip",
        }, Context);
    }

    public static void OnUnload()
    {
        ButtonPrompBarController.Instance?.ClearButtonPrompts(Context);
    }
}
```

**Framework helper:**

```csharp
using PratfallModFramework;

public static class MyMod
{
    private const string Context = "MyMod_Inventory";

    public static void OnLoad() => ModButtonPromptHelper.Show("ui_accept", "Equip", Context);
    public static void OnUnload() => ModButtonPromptHelper.ClearContext(Context);
}
```

**Why use the helper:** tolerates null `Instance` on screens where the HUD isn't loaded (main menu), catches/logs exceptions from the underlying call so you don't crash other prompts.

## Recipe: Extend a drop pool

Add an entry to a `RandomWeightedDropPool` resource (Robert's recommended pattern — see decode notes).

**Vanilla:**

```csharp
using Godot;

public static class MyMod
{
    private static RandomWeightedDropPool? _pool;
    private static RandomWeightedScene? _entry;

    public static void OnLoad()
    {
        _pool = ResourceLoader.Load<RandomWeightedDropPool>("res://path/to/FoodDropPool.tres");
        _entry = new RandomWeightedScene
        {
            Scene = GD.Load<PackedScene>("res://my_mod/MyFood.tscn"),
            Weight = 5,
            CanDropSingleplayer = true,
        };
        var existing = _pool.Pool ?? Array.Empty<RandomWeightedScene>();
        var grown = new RandomWeightedScene[existing.Length + 1];
        Array.Copy(existing, grown, existing.Length);
        grown[existing.Length] = _entry;
        _pool.Pool = grown;
    }

    public static void OnUnload()
    {
        if (_pool == null || _entry == null) return;
        var current = _pool.Pool ?? Array.Empty<RandomWeightedScene>();
        var idx = Array.IndexOf(current, _entry);
        if (idx < 0) return;
        var shrunk = new RandomWeightedScene[current.Length - 1];
        Array.Copy(current, 0, shrunk, 0, idx);
        Array.Copy(current, idx + 1, shrunk, idx, current.Length - idx - 1);
        _pool.Pool = shrunk;
    }
}
```

**Framework helper:**

```csharp
using Godot;
using PratfallModFramework;

public static class MyMod
{
    private static IDisposable? _registration;

    public static void OnLoad()
    {
        _registration = ModDropPoolHelper.Register(
            poolResPath: "res://path/to/FoodDropPool.tres",
            scene: GD.Load<PackedScene>("res://my_mod/MyFood.tscn"),
            weight: 5);
    }

    public static void OnUnload() => _registration?.Dispose();
}
```

**Why use the helper:** removes the exact entry you added (never by content match — two mods can legitimately add the same scene at the same weight), handles edge cases like the pool being empty or the entry being missing on unload.

## Recipe: Custom Godot types

Mods that ship `.tscn` files or instantiate custom Godot-derived types (`class MyComponent : Node3D`, `class MyResource : Resource`, `[GlobalClass]` attributes) need their assembly registered with Godot's script bridge. Otherwise the type loads as plain C# but `PackedScene.Instantiate` and `.tscn` script references silently fail.

**Tim's loader does this automatically** when your manifest has `AddAssemblyToGodot: true` (the default).

**The framework does this automatically** when your manifest has `AddAssemblyToGodot: true` (same default — schema-compatible).

So: **don't set `AddAssemblyToGodot: false`** unless you know exactly why. There's no code recipe — the registration is handled by the loader, not your mod.

If you're curious what it does under the hood: the loader calls `Godot.Bridge.ScriptManagerBridge.LookupScriptsInAssembly(yourAssembly)` after loading your DLL.

## Decoded Pratfall surface inventory

Audit of `Pratfall.dll` (2026-05-17) — 804 game types analyzed. The most mod-relevant ones:

### Singleton managers (99 total, key ones for modding)
- `AudioManager.Instance` — sound playback
- `MusicManager.Instance` — music tracks
- `InputManager.Instance` — cursor + input source
- `LocalizationManager.Instance` — language + locales
- `SavegameManager` (static) — save lifecycle
- `GameEventBus.Instance` — game-wide pub/sub
- `ButtonPrompBarController.Instance` — HUD prompts
- `GameController.Instance` — top-level game state, level loading
- `Network.Instance` — multiplayer root
- `GameModeManager.Instance` — game modes
- `CustomGameManager.Instance` — custom presets
- `LevelManager.Instance` — level prefab list
- `Loader.Instance` — resource loading
- `SettingsManager` — settings load/save

### Events you can subscribe to
- `SavegameManager.OnGameWillSave / OnGameDidSave` (`SaveDataCallback`)
- `GameEventBus.OnGameEventReceived` (`GameEventReceived (GameplayTag, IGameEvent)`)
- `LocalizationManager.OnLocalChanged` (`LocaleChanged`)
- `NetworkEventManager.OnNetworkEventReceived`
- `NetworkComponentManager.OnGetNetworkSpawnParent`

### Native extension APIs (game already supports — wrap thinly or use directly)
- `LocalizationManager.LoadUserLocalizations()` → file in `<userData>/localization/_*.json`
- `ButtonPrompBarController.AddButtonPrompt(ButtonPromptOptions, string context)` + `ClearButtonPrompts(context)`
- `DebugMappingManager.DropPools: RandomWeightedDropPool[]` → mutate via `ModDropPoolHelper` (Robert's pattern)

### Arrays you might be tempted to mutate (but shouldn't — save-game indexing)
- `PlayerColorsConfig.Colors: Color[]` — adding a color shifts every saved player's choice
- `GameModeManager.Modes: GameModeBaseConfig[]` — saved preferred mode is by index
- `LevelManager.LevelPrefabs: PackedScene[]` — level progression / unlocks reference indices
- `ProceduralCaveComponent.BiomeGenerationConfigs` — affects procedural determinism → multiplayer would diverge
- `OptionsUIViewController.TabBarItems: OptionsContentUIViewBase[]` — milder, but still shifts indices

### Config types (read-only by convention)
- 26 `*Config` and `*Settings` types: `GameConfig`, `NetworkConfig`, `GameModeBaseConfig`, `AudioStreamsPreloadConfig`, `BiomeConfig`, etc.

### CLI flags (Tim's loader)
- `--qh-disable-mod-ui` — hides the native Mod button on the main menu (the framework patches this to always-on)
- `--qh-skip-mods` — skips loading any mods at launch (debug/recovery)

## Pitfalls

- **Folder names must be unique across mods.** Tim's loader mounts each mod's PCK to `res://<DirectoryName>/...`. Two mods sharing a folder name silently overwrite each other's assets. (Cecil-confirmed by Tim in #mod-dev, 2026-05-16.)
- **Filesystem URIs vs paths.** `Game.Platform.GetUserDataPath()` returns a Godot `user://` URI. Pass it through `ProjectSettings.GlobalizePath(...)` before any `System.IO` call. (The framework helpers do this for you; vanilla code must remember.)
- **Don't mutate save-coupled arrays.** `PlayerColorsConfig.Colors`, `GameModeManager.Modes`, `LevelManager.LevelPrefabs` are all indexed by save-game data — mutating them invalidates existing player saves.
- **ByteBufferWriter has a 32 KB string cap.** Affects any custom network protocol you build on top of `NetworkEventManager.SendEvent`. The framework's transfer chunks at 14 KB raw → ~20 KB JSON envelope to stay under.
- **Mod ID vs folder name.** Manifest `id` and folder name SHOULD match (Tim's loader keys off the folder for `res://` paths, ours uses both). Saving headaches: make them identical.
- **Workshop auto-updates change DLL bytes silently.** If your mod ships via Workshop, expect users to see "needs re-check" prompts in the framework after each update (framework gate behavior). Pure-Pratfall users won't notice — the mod just loads.
- **`UnloadAssembly` calls `AssemblyLoadContext.Unload()` + forces GC.** Don't hold long-lived references to game types in static fields if you want clean unload behavior.
- **OnLoad reentrance.** Mods can be enabled → disabled → enabled multiple times per session. Make your OnLoad / OnUnload idempotent: every subscription paired with an unsubscribe, every Register paired with a Dispose.

## Resources

- **Tim's example mod** — [`quad-head/pratfall-example-mod`](https://github.com/quad-head/pratfall-example-mod) — the canonical reference for the official loader's flavor (`[ModEntry]`, root.tscn pattern).
- **This framework's sample** — [`sample-mods/HelloWorldMod/`](sample-mods/HelloWorldMod) — minimal Harmony-patch mod using `[ModPatch]`.
- **Framework helpers source** — [`framework/PratfallModFramework/Mod*Helper.cs`](framework/PratfallModFramework/) — read these to see exactly what each wrapper does. None use reflection or IL hacks; everything is public Pratfall API.
- **Decoded loader behavior** — Tim's `ModManager.LoadAssembly` calls `LoadFromAssemblyPath` → optionally `ScriptManagerBridge.LookupScriptsInAssembly` (gated on `AddAssemblyToGodot`) → invokes your `ModEntry.ModInit()`. The framework's `ModAssemblyLoader.LoadMod` mirrors this exactly with `OnLoad` instead of `ModInit`.
- **Discord** — `#mod-dev` channel of the Pratfall dev server is where Tim, Robert, and active modders coordinate.
