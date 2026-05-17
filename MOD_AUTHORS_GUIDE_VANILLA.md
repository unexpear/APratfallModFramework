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

**Heads up — Pratfall does not ship HarmonyLib.** Vanilla mods that want Harmony-style method patches have to bring their own. The two practical options:

1. **Ship `0Harmony.dll` alongside your mod's DLL.** Add `<PackageReference Include="Lib.Harmony" Version="2.3.3" />` to your csproj and copy `0Harmony.dll` into your mod folder at build time. Whether the runtime resolves it cleanly depends on AssemblyLoadContext probe order — works in most cases on .NET 8 but has been known to be fragile across game updates.

2. **Use direct property/field mutation** (no Harmony). For many mod ideas this is enough. Pattern shown by `123DMWM` in #mod-dev for an infinite-flare mod:

```csharp
using Godot;

public static class ModEntry
{
    public static void ModInit()
    {
        // Player.LocalPlayer is a public static field on the Player class.
        // ThrowFlareComponent is inherited from RigidBody3DEntity (which
        // implements IEntity, where every component type is a property).
        // Player.LocalPlayer.ThrowFlareComponent is null UNTIL the player
        // has picked up a flare item — IEntity's component-property accessors
        // return null when the entity has no instance of that component.
        // For a "global tweak on next spawn" effect you'd typically hook
        // into a spawn event and re-apply; null-check up front to be safe.
        var flare = Player.LocalPlayer?.ThrowFlareComponent;
        if (flare == null)
        {
            GD.Print("[MyMod] no ThrowFlareComponent yet — apply on next pickup");
            return;
        }
        flare.MaxFlares = 50;
        flare.FlareRecoverySeconds = 0.01f;
    }

    public static void ModDestroy()
    {
        // Defaults pulled from Pratfall.dll IL: MaxFlares=3, FlareRecoverySeconds
        // depends on the .tscn-author setting per scene; restore with your
        // best guess or skip restore if your mod is "load once, never undo".
        var flare = Player.LocalPlayer?.ThrowFlareComponent;
        if (flare == null) return;
        flare.MaxFlares = 3;
        flare.FlareRecoverySeconds = 5.0f;
    }
}
```

Caveats with the direct-mutation pattern:
- The IL safety scanner shipped by the Pratfall Mod Framework won't flag this (it's just `stfld` on a game type — not a dangerous API). That's intentional: cheat-style mods are out of scope for the malware scanner.
- You need to remember the original values yourself to restore them on `ModDestroy`. Pratfall doesn't expose "the defaults" as a snapshot.
- Mutations apply to whatever instance exists at the moment — instances created later (respawned players, new sessions) get the unmodified defaults unless you re-apply.
- Many "feels like a component on Player" things are actually `IEntity`-inherited properties that can be null when the specific component instance isn't present. Null-check before dereferencing.

If you genuinely need Harmony patches (transpilers, prefix-with-skip, advanced argument injection), the cleanest path on vanilla is option 1 above. Mods targeting the **Pratfall Mod Framework** instead get a `[ModPatch]` attribute that handles Harmony loading, attribute scanning, and unpatch-on-disable — see [MOD_AUTHORS_GUIDE_FRAMEWORK.md](MOD_AUTHORS_GUIDE_FRAMEWORK.md) for that pattern.

## Recipe: Add a language

> **Current Pratfall release (1.1.0.R2943) gates this.** `LocalizationManager.LoadUserLocalizations` checks `Game.Config.AllowUserLocalization` first — and on the public release that flag is **false**, so the loader silently skips every user-installed locale regardless of filename or content. Wait for the dev to flip the flag, or load translations via `TranslationServer.AddTranslation` directly (advanced; bypasses the manager's bookkeeping). The recipe below is the *intended* path; verify on your target build before shipping by checking `Game.Config.AllowUserLocalization`.

Pratfall's `LocalizationManager` has native support for user-installed locales. It scans `<userData>/localization/*.json` (skipping any file whose name starts with `_`) and registers anything it finds in `AvailableLocales` — the same list the in-game language selector reads.

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
        // Filename MUST end with `.json` AND MUST NOT start with `_` — leading-
        // underscore files are reserved/skipped by Pratfall's LoadJsonFiles filter.
        return Path.Combine(folder, "MyMod_es_419.json");
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
- File MUST end with `.json` AND MUST NOT start with `_`. Pratfall's `LoadJsonFiles` skips leading-underscore files (probably reserved for templates/disabled). Naming pattern that works: `<modId>_<localeCode>.json`.
- **The registered locale ID is `"zuser" + filename-without-extension`.** Pratfall namespaces user locales away from system locales ("en", "de", "fr", ...) so they can't collide. So a file `MyMod_es_419.json` registers as locale ID `"zuserMyMod_es_419"`, NOT `"es_419"`. If you want to programmatically switch to your locale via `TranslationServer.SetLocale(...)` or check `LocalizationManager.IsLocaleAvailable(...)`, use the prefixed ID.
- The in-game language selector displays user locales by their filename basename (`MyMod_es_419` in the example above). If you want a friendlier display name, pick a friendlier filename.
- The game gates on `GameConfig.AllowUserLocalization` — if a future build flips that flag off, `LoadUserLocalizations` becomes a no-op.
- Pratfall reads JSON, not CSV. Expected shape: a flat `Dictionary<string, string>` of translation key → translated string (Pratfall uses source-gen `JsonSerializer.Deserialize<Dictionary<string,string>>`).
- Verify it loaded by calling `LocalizationManager.Instance.IsLocaleAvailable("zuser<modId>_<localeCode>")` after `LoadUserLocalizations` — returns false if the file was silently skipped.

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
            // WeightAdvantage / WeightDisadvantage default to 0 — set them if your
            // entry should drop more or less often based on the player's situation.
            // SettingsType is also a field (CustomGameSettings); leave default for
            // entries that don't tie into the custom-game settings system.
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

Audit of `Pratfall.dll` (2026-05-17, re-verified post-helper-bugfix) — 822 game types analyzed (skipping Epic / NAudio / SixLabors / ImGuiNET / K4os / MemoryPack / System namespaces). The most mod-relevant ones:

### Singleton managers (74 total — key ones for modding)

Counted as a public type with a static `Instance` field or static-getter property. Some additional types have `*Manager` in their name but aren't strict singletons (state-only static classes like `SavegameManager` / `SettingsManager` are listed below with a `(static)` note).
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
