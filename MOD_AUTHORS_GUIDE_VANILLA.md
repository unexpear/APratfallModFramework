# Pratfall Mod Author Guide — Vanilla

This guide is for writing mods that target **just Pratfall and its official mod loader** (Tim's `ModManager`, shipped with the game in `1.1.0.R2943` and later). No third-party framework required.

If you want the safety gate / IL scanner / multiplayer-vote / per-mod helpers added by the Pratfall Mod Framework, see [MOD_AUTHORS_GUIDE_FRAMEWORK.md](MOD_AUTHORS_GUIDE_FRAMEWORK.md) instead. The two paths are interoperable — your mod can target the vanilla loader and still run on a player's machine that has the framework installed.

## Contents

1. [Setup — csproj, manifest, folder layout](#setup)
2. [Lifecycle — `ModEntry.ModInit` / `ModDestroy`](#lifecycle)
3. [CLI flags Pratfall accepts](#cli-flags)
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
└── ModEntry.cs
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
  </ItemGroup>

  <Target Name="InstallMod" AfterTargets="Build">
    <MakeDir Directories="$(UserModsDir)\$(ModId)" />
    <Copy SourceFiles="$(TargetPath)" DestinationFolder="$(UserModsDir)\$(ModId)" />
    <Copy SourceFiles="manifest.json" DestinationFolder="$(UserModsDir)\$(ModId)" />
  </Target>
</Project>
```

**`manifest.json`** — Pratfall's loader expects PascalCase keys:

```json
{
  "Name": "My Cool Mod",
  "Version": "1.0.0",
  "Author": "you",
  "Description": "Does something cool.",
  "Assembly": "MyMod.dll",
  "PackageName": "",
  "AutoLoad": false,
  "AddAssemblyToGodot": true
}
```

Key fields:
- `Assembly` — DLL filename in the mod folder. Pratfall's loader will resolve `<mod folder>/<Assembly>` and `LoadFromAssemblyPath` it.
- `PackageName` — optional `.pck` filename. If set, the loader mounts the package and tries to instantiate `res://<DirectoryName>/root.tscn` under `Game.RootNode`.
- `AutoLoad` (default `false`) — when `true`, the loader auto-enables the mod at launch even if previously disabled. Useful for mods that ship runtime infrastructure.
- `AddAssemblyToGodot` (default `true`) — registers your mod's types with Godot's script bridge so custom `Node` / `Resource` subclasses work from `.tscn` and `PackedScene.Instantiate`. Don't set false unless you have a specific reason.

The mod folder name must be **unique** across all installed mods — it's the namespace for any assets in your `.pck` (Pratfall mounts them at `res://<DirectoryName>/...`).

## Lifecycle

Pratfall's `ModManager.LoadAssembly` reflects for a type literally named **`ModEntry`** in your loaded assembly, then calls a static **`ModInit`** method:

```csharp
public static class ModEntry
{
    public static void ModInit()
    {
        // Mod was enabled. Subscribe to events, register helpers, mount resources.
        // Pair every Register / Subscribe / += with the corresponding undo in ModDestroy.
    }

    public static void ModDestroy()
    {
        // Mod was disabled. Tear down everything ModInit set up.
        // Mods can be enabled + disabled multiple times per session — be reentrant.
    }
}
```

- Class name MUST be exactly `ModEntry` (any namespace).
- Methods MUST be `public static`, no parameters.
- After `ModDestroy` is called, the loader calls `AssemblyLoadContext.Unload()` and forces a GC — don't leak references to your assembly from static fields outside the mod.

## CLI flags

Pratfall reads these from its command line at startup:

| Flag | Effect |
|---|---|
| `--qh-disable-mod-ui` | Hides the native Mod button on the main menu. (`ModManager.ShouldHideModLoaderUi` returns true.) |
| `--qh-skip-mods` | Skips loading any mods at launch. (`ModManager.ShouldLoadMods` returns false.) Debug / recovery use. |

To pass these via Steam: right-click Pratfall → Properties → Launch options → add the flag.

## Recipe: Harmony patches

The most common mod pattern. Patch an existing Pratfall method to add or change behavior. Pratfall ships HarmonyLib alongside the game, so you can reference it directly.

```csharp
using HarmonyLib;
using Godot;

public static class ModEntry
{
    private static readonly Harmony _harmony = new("com.you.mymod");

    public static void ModInit()
    {
        var target = AccessTools.Method(typeof(PlayerPickaxeComponent), "TriggerPrimaryAction");
        var postfix = new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(OnPickaxe),
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic));
        _harmony.Patch(target, postfix: postfix);
    }

    public static void ModDestroy()
    {
        _harmony.UnpatchAll(_harmony.Id);
    }

    private static void OnPickaxe(PlayerPickaxeComponent __instance)
    {
        GD.Print("swung!");
    }
}
```

Add HarmonyLib to your csproj if not already referenced via Pratfall:

```xml
<PackageReference Include="Lib.Harmony" Version="2.3.3" />
```

## Recipe: Add a language

Pratfall's `LocalizationManager` has native support for user-installed locales. It scans `<userData>/localization/_*.json` and registers anything it finds in `AvailableLocales` — the same list the in-game language selector reads.

```csharp
using System.Text.Json;
using System.IO;
using Godot;

public static class ModEntry
{
    private static string LocalePath()
    {
        // GetUserDataPath() returns a Godot `user://...` URI on Steam. Pass it
        // through ProjectSettings.GlobalizePath to get a real filesystem path
        // that System.IO can read/write.
        var raw = Game.Platform?.GetUserDataPath();
        if (string.IsNullOrEmpty(raw)) return "";
        var folder = Path.Combine(ProjectSettings.GlobalizePath(raw), "localization");
        Directory.CreateDirectory(folder);
        // Filename MUST start with `_` and end with `.json` (loader's filter).
        return Path.Combine(folder, "_MyMod_es_419.json");
    }

    public static void ModInit()
    {
        var translations = new Dictionary<string, string>
        {
            { "MAIN_MENU_PLAY", "Jugar" },
            { "MAIN_MENU_OPTIONS", "Opciones" },
        };
        File.WriteAllText(LocalePath(), JsonSerializer.Serialize(translations));
        LocalizationManager.Instance?.LoadUserLocalizations();
    }

    public static void ModDestroy()
    {
        var path = LocalePath();
        if (File.Exists(path)) File.Delete(path);
        LocalizationManager.Instance?.LoadUserLocalizations();
    }
}
```

Gotchas:
- File MUST start with `_` and end with `.json` — Pratfall's `LoadJsonFiles` filters by this.
- The game gates on `GameConfig.AllowUserLocalization` — if a future build flips that flag off, `LoadUserLocalizations` becomes a no-op.
- Pratfall reads JSON, not CSV.

## Recipe: Persist mod data

`SavegameManager` fires `OnGameWillSave` whenever the player triggers a save. Subscribe to that and flush your data to a file alongside the game's save.

```csharp
using System.Text.Json;
using System.IO;
using Godot;

public static class ModEntry
{
    private static MyState _state = new();
    private static SavegameManager.SaveDataCallback? _saveHook;

    private static string SavePath()
    {
        var raw = Game.Platform?.GetUserDataPath();
        if (string.IsNullOrEmpty(raw)) return "";
        return Path.Combine(ProjectSettings.GlobalizePath(raw), "mymod-state.json");
    }

    public static void ModInit()
    {
        // Restore prior state.
        var path = SavePath();
        if (File.Exists(path))
            _state = JsonSerializer.Deserialize<MyState>(File.ReadAllText(path)) ?? new();

        // Flush on every save.
        _saveHook = () =>
        {
            var p = SavePath();
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, JsonSerializer.Serialize(_state));
        };
        SavegameManager.OnGameWillSave += _saveHook;
    }

    public static void ModDestroy()
    {
        if (_saveHook != null) SavegameManager.OnGameWillSave -= _saveHook;
        _saveHook = null;
    }

    public class MyState { public int Counter; }
}
```

`SavegameManager` exposes both `OnGameWillSave` (fires before save) and `OnGameDidSave` (fires after save). There's no `OnGameDidLoad` event — the game's `Setup(...)` accepts an `onGameDidLoad` callback that only the game itself subscribes to. Load your mod data at `ModInit` time by reading your file directly.

## Recipe: Listen to game events

Pratfall publishes events via `GameEventBus.SendEvent<T>(GameplayTag tag, T eventData)` where `T` implements `IGameEvent`. Subscribers can filter by tag string.

```csharp
using Godot;

public static class ModEntry
{
    private static GameEventReceived? _sub;

    public static void ModInit()
    {
        _sub = (tag, ev) =>
        {
            if (tag?.Tag != "Player.Death") return;   // filter manually
            GD.Print($"a player died: {ev}");
        };
        GameEventBus.OnGameEventReceived += _sub;
    }

    public static void ModDestroy()
    {
        if (_sub != null) GameEventBus.OnGameEventReceived -= _sub;
        _sub = null;
    }
}
```

The bus doesn't filter — every subscriber sees every event. Do the tag check inside your handler.

## Recipe: Show HUD button hints

Add a button-prompt entry to Pratfall's HUD bar (e.g. *"Press [A] to Equip"*).

```csharp
using Godot;

public static class ModEntry
{
    private const string Context = "MyMod_Inventory";

    public static void ModInit()
    {
        // ButtonPrompBarController is HUD-attached — Instance is null on the main menu.
        // Don't show prompts until you know the HUD is up (e.g. when entering a game).
        var bar = ButtonPrompBarController.Instance;
        if (bar == null) return;
        bar.AddButtonPrompt(new ButtonPromptOptions
        {
            ActionName = "ui_accept",
            Description = "Equip",
        }, Context);
    }

    public static void ModDestroy()
    {
        ButtonPrompBarController.Instance?.ClearButtonPrompts(Context);
    }
}
```

There's no per-prompt remove API — only `ClearButtonPrompts(context)` which clears every prompt registered under that context string. Pick a unique-per-mod context string so cleanup doesn't affect other mods' prompts.

## Recipe: Extend a drop pool

Add an entry to a `RandomWeightedDropPool` resource — Robert's recommended pattern for content mods.

```csharp
using Godot;

public static class ModEntry
{
    private static RandomWeightedDropPool? _pool;
    private static RandomWeightedScene? _entry;

    public static void ModInit()
    {
        _pool = ResourceLoader.Load<RandomWeightedDropPool>("res://path/to/FoodDropPool.tres");
        if (_pool == null) return;

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

    public static void ModDestroy()
    {
        if (_pool == null || _entry == null) return;
        var current = _pool.Pool ?? Array.Empty<RandomWeightedScene>();
        // Match by reference — never by content; two mods can legitimately add
        // the same scene at the same weight.
        var idx = Array.IndexOf(current, _entry);
        if (idx < 0) return;
        var shrunk = new RandomWeightedScene[current.Length - 1];
        Array.Copy(current, 0, shrunk, 0, idx);
        Array.Copy(current, idx + 1, shrunk, idx, current.Length - idx - 1);
        _pool.Pool = shrunk;
    }
}
```

## Recipe: Custom Godot types

Mods that ship `.tscn` files or instantiate custom Godot-derived types (`class MyComponent : Node3D`, `class MyResource : Resource`, `[GlobalClass]` attributes) need their assembly registered with Godot's script bridge. Pratfall's loader does this automatically when your manifest has `AddAssemblyToGodot: true` (the default).

So: **don't set `AddAssemblyToGodot: false`** unless you have a specific reason. No code recipe is needed — the registration is handled by the loader, not your mod.

Under the hood, Pratfall calls `Godot.Bridge.ScriptManagerBridge.LookupScriptsInAssembly(yourAssembly)` after loading your DLL. If you ever need to call it manually (e.g. for a runtime-loaded sub-assembly), you can:

```csharp
Godot.Bridge.ScriptManagerBridge.LookupScriptsInAssembly(myAssembly);
```

## Decoded Pratfall surface inventory

Audit of `Pratfall.dll` (2026-05-17) — 804 game types analyzed. The most mod-relevant ones:

### Singleton managers (99 total — key ones for modding)
- `AudioManager.Instance` — sound playback
- `MusicManager.Instance` — music tracks
- `InputManager.Instance` — cursor + input source
- `LocalizationManager.Instance` — language + locales (see recipe above)
- `SavegameManager` (static) — save lifecycle (see recipe above)
- `GameEventBus.Instance` — game-wide pub/sub (see recipe above)
- `ButtonPrompBarController.Instance` — HUD prompts (see recipe above)
- `GameController.Instance` — top-level game state, level loading
- `Network.Instance` — multiplayer root
- `GameModeManager.Instance` — game modes (Modes array is index-coupled to saves, see Pitfalls)
- `CustomGameManager.Instance` — custom game presets
- `LevelManager.Instance` — level prefab list (same indexing concern)
- `Loader.Instance` — resource loading
- `SettingsManager` (static) — settings load/save

### Events you can subscribe to
- `SavegameManager.OnGameWillSave / OnGameDidSave` (`SaveDataCallback`)
- `GameEventBus.OnGameEventReceived` (`GameEventReceived (GameplayTag, IGameEvent)`)
- `LocalizationManager.OnLocalChanged` (`LocaleChanged`)
- `NetworkEventManager.OnNetworkEventReceived`
- `NetworkComponentManager.OnGetNetworkSpawnParent`

### Native extension APIs (game already supports — use directly)
- `LocalizationManager.LoadUserLocalizations()` → drop a file in `<userData>/localization/_*.json`
- `ButtonPrompBarController.AddButtonPrompt(ButtonPromptOptions, string context)` + `ClearButtonPrompts(context)`
- `DebugMappingManager.DropPools: RandomWeightedDropPool[]` → mutate the array (see drop pool recipe)

### Arrays you might be tempted to mutate (but shouldn't)
These arrays are referenced by save-game data via **index**. Adding entries shifts every existing player's saved choice:
- `PlayerColorsConfig.Colors: Color[]`
- `GameModeManager.Modes: GameModeBaseConfig[]`
- `LevelManager.LevelPrefabs: PackedScene[]`
- `ProceduralCaveComponent.BiomeGenerationConfigs` — also affects procedural determinism → multiplayer would diverge
- `OptionsUIViewController.TabBarItems: OptionsContentUIViewBase[]` — milder, but still shifts indices

### Config types (read-only by convention)
26 `*Config` and `*Settings` types: `GameConfig`, `NetworkConfig`, `GameModeBaseConfig`, `AudioStreamsPreloadConfig`, `BiomeConfig`, `MaterialConfig`, etc. Read them via `Manager.Instance.Config` patterns; don't mutate.

## Pitfalls

- **Folder names must be unique across mods.** Pratfall mounts each mod's PCK at `res://<DirectoryName>/...`. Two mods sharing a folder name silently overwrite each other's assets. (Confirmed by Tim in #mod-dev, 2026-05-16.)
- **Filesystem URIs vs paths.** `Game.Platform.GetUserDataPath()` returns a Godot `user://` URI on Steam. Pass it through `ProjectSettings.GlobalizePath(...)` before any `System.IO` call. Godot's own `DirAccess` understands the URI, so game-side code paths work without it — but System.IO does not.
- **Don't mutate save-coupled arrays.** `PlayerColorsConfig.Colors`, `GameModeManager.Modes`, `LevelManager.LevelPrefabs` are all indexed by save-game data — mutating them invalidates existing player saves.
- **`ByteBufferWriter` has a 32 KB string cap.** Affects any custom network protocol built on top of `NetworkEventManager.SendEvent`. Keep payloads under 32 KB after JSON serialization.
- **`ModEntry` class name is exact.** Pratfall uses `assembly.GetType("ModEntry")` — case-sensitive, no namespace.
- **`ModInit` / `ModDestroy` reentrance.** Mods can be enabled → disabled → enabled multiple times per session. Make both methods idempotent: every subscription paired with an unsubscribe, every array growth paired with a shrink.
- **`AssemblyLoadContext.Unload()` is called on disable.** Don't hold long-lived references to game types in static fields outside the mod's `ModEntry` — the GC needs to collect your assembly's load context.
- **HUD-attached singletons are null on the main menu.** `ButtonPrompBarController.Instance` and similar HUD pieces are only present during gameplay. Null-check before use.

## Resources

- **Tim's example mod** — [`quad-head/pratfall-example-mod`](https://github.com/quad-head/pratfall-example-mod) — the canonical reference.
- **Discord** — `#mod-dev` channel of the Pratfall dev server (Tim, Robert, and active modders coordinate there).
- **The Pratfall Mod Framework** — [MOD_AUTHORS_GUIDE_FRAMEWORK.md](MOD_AUTHORS_GUIDE_FRAMEWORK.md) — adds a safety gate, IL scanner, multiplayer sync, and helpers that wrap the patterns in this guide.
