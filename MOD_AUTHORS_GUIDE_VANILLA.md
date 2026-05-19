# Pratfall Mod Author Guide — Vanilla

This guide is for writing mods that target **just Pratfall and its official mod loader** (Tim's `ModManager`, shipped with the game in `1.1.0.R2943` and later; updated `1.1.0.R2973` on 2026-05-18 with Steam Workshop support, a "very simple mod loader (main menu)" UI, and assorted multiplayer bug fixes — see Tim's [Workshop & Bugfixes patch notes](https://store.steampowered.com/news/app/4244510/view/663861845817296708)). No third-party framework required.

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
9. [Recipe: Show a toast notification](#recipe-show-a-toast)
10. [Recipe: Play a sound](#recipe-play-a-sound)
11. [Recipe: Spawn an entity into the world](#recipe-spawn-an-entity)
12. [Recipe: React to a level loading](#recipe-react-to-level-load)
13. [Recipe: Multiplayer-aware patterns (host check, late-join)](#recipe-multiplayer-patterns)
14. [Recipe: Extend a random drop pool](#recipe-extend-a-drop-pool)
15. [Recipe: Custom Godot Node / Resource types](#recipe-custom-godot-types)
17. [Recipe: Unpack `Pratfall.pck` + repack your mod's PCK](#recipe-unpack--repack-pck-files)
17. [Decoded Pratfall surface inventory](#decoded-pratfall-surface-inventory)
    - [17.1 "How do I ...?" quick-reference](#how-do-i-)
    - [17.2 Singletons (73)](#singletons-73)
    - [17.3 Static helper classes (22)](#static-helper-classes-22)
    - [17.4 Configs & Settings (26)](#configs--settings-26)
    - [17.5 Events you can subscribe to (11)](#events-you-can-subscribe-to-11)
    - [17.6 `GameplayTags.*` (40)](#gameplaytags-40)
    - [17.7 `Constants.EventId*` (56)](#constantseventid-56)
    - [17.8 Entity hierarchy & `IEntity` (23 entities, 184 component-accessors)](#entity-hierarchy--ientity)
    - [17.9 `IComponent` implementors (184)](#icomponent-implementors-184)
    - [17.10 Public interfaces (13)](#public-interfaces-13)
    - [17.11 `res://` path conventions](#res-path-conventions)
    - [17.12 Save-coupled arrays — don't mutate](#save-coupled-arrays--dont-mutate)
17. [Debugging & dev iteration](#debugging--dev-iteration)
18. [Distribution conventions](#distribution-conventions)
19. [Godot 4 concepts mod authors should know](#godot-4-concepts)
20. [Pitfalls + things to know](#pitfalls)
21. [Resources](#resources)

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
    <!--
      Install target is the official loader's mods folder, NEXT TO Pratfall.exe.
      Cecil-verified from ModManager.CreateModDirectory: shipped Pratfall computes
      Path.GetDirectoryName(OS.GetExecutablePath()) + "/mods". The `<userData>/mods`
      path (under %APPDATA%\Pratfall\mods) is ONLY used when running from the Godot
      editor — shipped Pratfall ignores it. If your dev environment uses a non-
      Steam install, override GameDir on the dotnet build command line.
    -->
    <GameModsDir>$(GameDir)\mods</GameModsDir>
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
    <MakeDir Directories="$(GameModsDir)\$(ModId)" />
    <Copy SourceFiles="$(TargetPath)" DestinationFolder="$(GameModsDir)\$(ModId)" />
    <Copy SourceFiles="manifest.json" DestinationFolder="$(GameModsDir)\$(ModId)" />
  </Target>
</Project>
```

**Heads up on the install path.** Pratfall's official `ModManager` looks for mods under `<Pratfall install folder>\mods\` in shipped builds (verified against `ModManager.CreateModDirectory` IL — it calls `OS.GetExecutablePath()` and appends `mods`). The `<userData>/mods` path (under `%APPDATA%\Pratfall\mods`) is **only used when running from the Godot editor** — shipped Pratfall ignores it. If you've seen guides or framework helpers point at AppData, those are framework-specific conventions, not the vanilla loader.

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

### `enabled_mods.json`

Inside the official `<GameDir>\mods\` directory, the loader keeps an `enabled_mods.json` file — a JSON array of **mod folder names**, NOT display names:

```json
["Author.SomeMod", "Author.AnotherMod"]
```

Cecil-verified from `ModManager.IsModEnabled(manifest)` IL: each string is compared against `manifest.DirectoryName` (the folder name). So if your mod folder is `Author.MyMod`, the entry in `enabled_mods.json` must be exactly `Author.MyMod` — the `Name` field from your manifest is for display only and does NOT gate loading.

For manual testing, you can edit `enabled_mods.json` directly with a text editor, or use Pratfall's in-game Mods button which writes it for you on each toggle. If the file is absent or empty (`[]`), no mods are enabled at launch (unless they have `AutoLoad: true` in their manifest).

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

- Class name MUST be exactly `ModEntry` **in the global namespace** — top-level, no `namespace MyMod { ... }` wrapper. Pratfall calls `assembly.GetType("ModEntry")` which only finds a type whose full name is exactly `ModEntry`. If you put `class ModEntry` inside `namespace MyMod`, its full name becomes `MyMod.ModEntry` and the lookup returns `null`, ModInit never fires, and the loader silently does nothing. (Confirmed by Tim's `quad-head/pratfall-example-mod`, whose `ModEntry` is at the global namespace.)
- Methods MUST be `public static`, no parameters. Pratfall looks them up via `GetMethod(name, BindingFlags.Public | BindingFlags.Static)` and invokes with `null` target + `null` args.
- After `ModDestroy` is called, the loader calls `Unload()` on the AssemblyLoadContext your mod was loaded into, then forces three GC passes. The catch: Pratfall loads your mod **into the same AssemblyLoadContext that hosts Godot's `ScriptManagerBridge`** (verified in `ModManager.LoadAssembly` IL — `AssemblyLoadContext.GetLoadContext(typeof(ScriptManagerBridge).Assembly)`). It does NOT create a per-mod ALC. Whether the runtime can actually free your mod's assembly depends on whether that shared ALC is collectible AND whether anything else still references your code. Exceptions during unload are swallowed by an `ExceptionHandler.Block` toggle, so a failed unload is silent.

**Unload is cooperative, not forced.** Disabling a mod runs your `ModDestroy` and asks the runtime to unload the `AssemblyLoadContext`, but the runtime CANNOT actually free the assembly while anything still references it. Things that will silently keep your mod alive in memory after disable:

- **Static event subscriptions you didn't unsubscribe** — `GameEventBus.OnGameEventReceived += handler` without a matching `-=` (also `SavegameManager.OnGameWillSave`, `Network.Instance.EventManager.OnNetworkEventReceived`, etc.)
- **Background threads / `Task.Run` that's still running** — pinned to your assembly via captured `this`
- **Harmony patches you didn't `UnpatchAll`** — patched MethodInfo objects retain references to your patch methods
- **Cached `Type` / `MethodInfo` / `Delegate` references** held by game-side dictionaries (e.g. type caches in `Newtonsoft.Json` / `System.Text.Json`)
- **Native libraries loaded with `LoadFromUnmanagedDll`** — never unloaded by the runtime
- **Godot nodes you `AddChild`ed but never `QueueFree`d** — the scene tree still owns them
- **`MainThreadDispatcher.Instance.Enqueue` callbacks queued for after-unload** — captured `this` keeps your context alive

If you see your mod's log messages still firing after you toggled it off, one of the above is the culprit. The mod's assembly will stay in memory until the entire game process exits.

## CLI flags

Pratfall reads these from its command line at startup:

| Flag | Effect |
|---|---|
| `--qh-disable-mod-ui` | Hides the native Mod button on the main menu. (`ModManager.ShouldHideModLoaderUi` returns true.) |
| `--qh-skip-mods` | **Currently a no-op** despite the flag-reading code being present. `ModManager.ShouldLoadMods` returns `!HasFlag("--qh-skip-mods")` per Cecil (verified `1.1.0.R2973`), but no code path in Pratfall actually reads `ShouldLoadMods` — the getter is defined but unused. Probably intended for a future refactor; document here so debug users don't rely on it. |
| `--qh-mod-directory <path>` | Overrides the mods folder. Pratfall's loader normally computes the path from `OS.GetExecutablePath()`; this flag lets you point it at a different folder. Cecil-confirmed in `ModManager.CreateModDirectory`. **Useful for profile-based mod managers** (Thunderstore / r2modman) — see the [profile / mod-manager-compat note below](#profile--mod-manager-compat). |
| `--qh-skip-preload` | Skips resource preloading on launch. Auto-skipped already when the GPU vendor contains "Intel" (workaround for an Intel preload bug); this flag forces-skips on any GPU. Cecil-confirmed in `Preloader.SkipPreload`. |
| `--qh-disable-login` | Disables EOS (Epic Online Services) login at launch. Useful for dev iteration when you don't want Steam→EOS authentication to fire. |
| `--qh-skip-video-settings` | Skips the launch-time video-settings detect/apply pass. Useful when you've manually edited your settings file and don't want them overwritten on launch. |

### Profile / mod-manager compat

Pratfall is already compatible with profile-based mod managers (Thunderstore / r2modman style). The path is:

1. The mod manager creates per-profile mod folders, e.g. `<profile_root>/Pratfall/<profile_name>/mods/`
2. Drops the profile's enabled-mod-id list into `<that mods folder>/enabled_mods.json`
3. Launches Pratfall with `--qh-mod-directory <that mods folder>`

Pratfall's official loader reads `enabled_mods.json` from whatever folder `--qh-mod-directory` points at, so the same launch-arg controls both the mod set AND the enabled state — no separate "enabled state" flag is needed. Per `ModManager.CreateModDirectory` IL: `--qh-mod-directory` takes the highest precedence, beating both editor-mode-userdata fallback and the shipped-build `OS.GetExecutablePath() + "/mods"` default.

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
        // Defaults pulled from ThrowFlareComponent.ctor IL: MaxFlares=3,
        // FlareRecoverySeconds=3, ThrowStrength=10, TorqueStrength=0.1f.
        // A specific .tscn-equipped flare can override these in scene data;
        // restoring to the C# ctor defaults is "close enough" for most mods.
        var flare = Player.LocalPlayer?.ThrowFlareComponent;
        if (flare == null) return;
        flare.MaxFlares = 3;
        flare.FlareRecoverySeconds = 3.0f;
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

> **Current Pratfall release (verified `1.1.0.R2973`, 2026-05-18) gates this.** `LocalizationManager.LoadUserLocalizations` checks `Game.Config.AllowUserLocalization` first — and on the public release that flag is **false** (Cecil-verified: `GameConfig` constructor initializes it to `false`), so the loader silently skips every user-installed locale regardless of filename or content. Wait for the dev to flip the flag, or load translations via `TranslationServer.AddTranslation` directly (advanced; bypasses the manager's bookkeeping). The recipe below is the *intended* path; verify on your target build before shipping by checking `Game.Config.AllowUserLocalization`.

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

### Workaround when the gate is closed

On builds where `Game.Config.AllowUserLocalization` is **false** (including the current `1.1.0.R2973` release), the JSON-file path above is a no-op. To patch translations *into an existing locale* you can bypass `LocalizationManager` entirely and call Godot's `TranslationServer` directly:

```csharp
using Godot;

public static void ModInit()
{
    var t = new Translation();
    t.Locale = "en";  // patch English; pick any locale Godot knows
    t.AddMessage("MYMOD_HELLO", "Hello");
    t.AddMessage("MYMOD_BUTTON_LABEL", "Equip");
    TranslationServer.AddTranslation(t);
}
```

**Today (build `1.1.0.R2973`)** this works on every build regardless of the gate, but it has a hard limitation: you can only add or override translation keys inside locales the game already knows about. You can't add a brand-new *selectable* language because the in-game language selector reads from `LocalizationManager.AvailableLocales`, which is populated only by the JSON-file path above. As a workaround for getting NEW keys into the active language reliably, Henrique's [PratfallLocalizationMod](https://github.com/HenriqueCamillo/PratfallLocalizationMod) listens to `NotificationTranslationChanged` and re-applies its `AddTranslation` calls after every locale change — works today, but a little fiddly.

**Coming in the next Pratfall update** (per Tim in `#mod-dev`, 2026-05-18): the settings menu will rescan Godot's known languages every time it opens, and the launch-time language cache will be removed. After that ships, `TranslationServer.AddTranslation` becomes a first-class path for **adding new selectable languages too** — call it from `ModInit`, and the language shows up in the in-game selector. The JSON-file path will still work for authors who want their locale file alongside the mod folder; both paths will coexist. Re-check this section against the actual update notes when it lands.

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

Pratfall publishes events via `GameEventBus.SendEvent<T>(GameplayTag tag, T eventData)` where `T` implements `IGameEvent`. The bus doesn't filter — every subscriber sees every event. Filter inside your handler.

**Use the pre-defined `GameplayTags` static class for the tag reference** — Pratfall ships ~40 named `GameplayTag` constants (loaded from `res://data/gameplay_tags/*.tres` in the `GameplayTags` static cctor). `GameplayTag.Equals` compares by `.Tag` string, so an Equals check against `GameplayTags.X` always works.

```csharp
using Godot;

public static class ModEntry
{
    private static GameEventReceived? _sub;

    public static void ModInit()
    {
        _sub = (tag, ev) =>
        {
            // Reference Pratfall's pre-defined GameplayTag instead of a made-up
            // string. `GameplayTag.Equals` is value-equality on the underlying
            // `Tag` property, so comparing against the static constant works
            // even though the runtime instance comes from a different code path.
            if (tag == null || !tag.Equals(GameplayTags.Stats_Gameplay_Player_Death)) return;
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

All 40 available tags are listed in [GameplayTags.* (40)](#gameplaytags-40) below, grouped by category (stats, win conditions, status effects, materials, harvestables).

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

## Recipe: Show a toast

Pop a transient notification on the HUD ("Item picked up", "Mod loaded", etc.). `ToastUIController.Instance` is a HUD-attached singleton — null on the main menu, null between levels.

```csharp
using Godot;

public static class ModEntry
{
    public static void ModInit()
    {
        var toaster = ToastUIController.Instance;
        if (toaster == null)
        {
            GD.Print("[MyMod] no HUD yet — toast deferred to next level load");
            return;
        }
        // Real signature: Show(string message, double durationSeconds, bool playSound)
        toaster.Show("MyMod loaded!", 3.0, playSound: true);
    }
}
```

For a toast that all players in a multiplayer lobby see, wrap the call in a `Network.Instance.EventManager.SendEvent(...)` broadcast on `Constants.EventIdShowToast = 138` instead — the game already handles that event-id and pops the toast on every receiver.

Gotchas:
- Queued internally — calling `Show` rapidly queues messages; they play in order, not simultaneously.
- `Show(..., playSound: false)` skips the audio cue; use it for low-priority chatter.
- If you want this to fire only once per level, gate on a `_hasShownThisLevel` flag and reset in your scene-load hook (see [React to level loading](#recipe-react-to-level-load)).

## Recipe: Play a sound

`AudioManager` exposes two flavors: 3D-positional (`PlaySound`) and 2D/UI (`PlaySound1D`). Both take a `Godot.AudioStream` you load from a packaged asset.

```csharp
using Godot;

public static class ModEntry
{
    private static AudioStream? _ding;

    public static void ModInit()
    {
        // Asset lives at res://<YourModFolder>/sounds/ding.ogg — shipped via your .pck.
        _ding = GD.Load<AudioStream>("res://MyMod/sounds/ding.ogg");
    }

    public static void PlayAt(Vector3 worldPosition)
    {
        if (_ding == null) return;
        var audio = AudioManager.Instance;
        if (audio == null) return;
        // 3D-positional — falls off with distance, attenuated by AudioManagerPlayOptions defaults.
        audio.PlaySound(_ding, worldPosition);
    }

    public static void PlayUiBeep()
    {
        if (_ding == null) return;
        AudioManager.Instance?.PlaySound1D(_ding, new AudioManagerPlayOptions());
    }
}
```

Gotchas:
- `AudioManager.Instance` is non-null during gameplay; it may be null in the very-early boot window.
- For 3D sound, the player only hears it if their listener (camera) is in range — `AudioManagerPlayOptions` lets you override volume / pitch / bus.
- This plays the sound **locally only**. Other players won't hear it. For multiplayer-replicated audio, send a network event and have all clients call `PlaySound` on receive.
- Audio files inside your `.pck` need to be imported as Godot AudioStream resources (`.ogg` and `.wav` work out of the box; see [Setup](#setup) for PCK packaging).

## Recipe: Spawn an entity

Drop a new `PackedScene` instance into the world. Two paths depending on whether you want the spawn replicated to other players.

```csharp
using Godot;

public static class ModEntry
{
    private static PackedScene? _propScene;
    private static Node? _spawned;

    public static void ModInit()
    {
        _propScene = GD.Load<PackedScene>("res://MyMod/MyProp.tscn");
    }

    public static void SpawnLocal()
    {
        if (_propScene == null) return;

        // Local-only spawn (only this player sees it). Returns a Godot.Node.
        // ScenePoolManager pools the result if the scene's root implements IPooledObject;
        // otherwise it does a regular Instantiate + AddChild under the parent you pass.
        _spawned = ScenePoolManager.Instance?.Instantiate(_propScene, Game.RootNode);
    }

    public static void SpawnReplicated()
    {
        if (_propScene == null) return;

        // Replicated spawn — every player in the lobby sees it spawn.
        // The prefab MUST be registered in NetworkPrefabsConfig first; if it isn't,
        // SpawnNetworkPrefab returns a failure result. Pratfall doesn't currently
        // expose a mod-friendly NetworkPrefabsConfig registration API — content
        // mods that need replicated spawn need framework-helper support.
        var componentMgr = Network.Instance?.ComponentManager;
        if (componentMgr == null) return;
        var result = componentMgr.SpawnNetworkPrefab(_propScene, Game.RootNode);
        // result.Node is the spawned root, result.Status indicates success/failure.
    }

    public static void ModDestroy()
    {
        // Free what you spawned if it should not outlive your mod.
        _spawned?.QueueFree();
        _spawned = null;
    }
}
```

Gotchas:
- **Replicated spawn requires prefab registration.** `NetworkPrefabsConfig` is loaded from game data and not mod-author-extensible from vanilla today — practically you can only replicate prefabs the game already knows about. Local-only spawn has no such restriction.
- `Game.RootNode` is the right parent for "persists across the session"; for "lives for one level", parent under a scene-specific node (e.g. via `SceneManager.Instance.GetLoadedScenes()`).
- `ScenePoolManager.Instance` is null very early in boot; defer spawning until at least one scene has loaded.
- `Game.RootNode` is null in the very-early bootstrap window too — your `ModInit` runs after it's set, but be aware.

## Recipe: React to level load

Pratfall has no public `OnSceneLoaded` event on `SceneManager`. The way to react to "a level just finished loading" from vanilla is to subscribe to `Network.Instance.EventManager.OnNetworkEventReceived` and filter for the loaded-level event id.

```csharp
using Godot;

public static class ModEntry
{
    private static NetworkEventReceived? _sub;

    public static void ModInit()
    {
        var mgr = Network.Instance?.EventManager;
        if (mgr == null)
        {
            GD.PrintErr("[MyMod] Network.EventManager not ready");
            return;
        }

        _sub = (ushort eventId, NetworkFrameEvent ev) =>
        {
            // Constants.EventIdLoadedLevel is a `const ushort` (value 119) — reference it directly.
            if (eventId != Constants.EventIdLoadedLevel) return;
            // A level just finished loading. HUD-attached singletons are now safe to query:
            //   ButtonPrompBarController.Instance, ToastUIController.Instance,
            //   DebugMappingManager.Instance.DropPools, etc.
            ToastUIController.Instance?.Show("MyMod is active in this level", 2.0, false);
        };
        mgr.OnNetworkEventReceived += _sub;
    }

    public static void ModDestroy()
    {
        var mgr = Network.Instance?.EventManager;
        if (_sub != null && mgr != null)
            mgr.OnNetworkEventReceived -= _sub;
        _sub = null;
    }
}
```

Gotchas:
- Other loaded-level ids you might care about: `EventIdRequestLevelLoad = 103`, `EventIdUnloadLevel = 120`, `EventIdSetLevelActive = 148`. See the full [`Constants.EventId*`](#constantseventid-56) table.
- `Network.Instance.EventManager` is **the** subscription target — not a static `NetworkEventManager` class. The events fire whether you're host, client, or singleplayer.
- The `NetworkFrameEvent` payload exposes `EventId`, `TargetId`, and `Data` (the raw bytes). For loaded-level you don't need the payload; for other events, call `ev.GetEvent<YourEventType>()` to deserialize.

## Recipe: Multiplayer patterns

> **These are basic patterns, not a complete sync protocol.** A host check and a late-join hook do NOT by themselves make a mod multiplayer-safe. Pratfall has a two-layer network stack (low-level frame messages + high-level tagged events) and the safer path for any mod that changes gameplay rules, saved state, inventory, drops, or authority is to build explicit per-mod sync via `Network.Instance.EventManager.SendEvent` with a custom event id and sender identity embedded in the payload. If you haven't built that, treat your mod as **"all players need this mod installed and enabled"** and say so in your README — don't rely on host-only logic working invisibly for clients. The decoded protocol map (local research notes) covers the full stack; the recipes below cover only the entry-level patterns.

Vanilla Pratfall doesn't have a single `IsHost` shortcut — host/client identity lives on `Network.Instance.LobbyManager`. The patterns below cover the four things multiplayer mods always need to do.

```csharp
using Godot;

public static class ModEntry
{
    public static void ModInit()
    {
        var lobby = Network.Instance?.LobbyManager;
        if (lobby == null)
        {
            // Singleplayer or pre-lobby — Network isn't up. No-op.
            return;
        }

        // Subscribe to join/leave for late-join handling.
        lobby.OnMemberJoined += OnMemberJoined;
        lobby.OnMemberLeft   += OnMemberLeft;
    }

    private static bool IsHost()
        => Network.Instance?.LobbyManager?.IsLobbyOwner ?? false;

    private static bool IsSingleplayer()
        => Network.Instance?.LobbyManager?.IsSingleplayerLobby ?? true;

    private static void OnMemberJoined(INetworkLobbyMember member)
    {
        if (!IsHost()) return;  // only the host replays state to new joiners
        // Send mod state to the joiner via Network.Instance.EventManager.SendEvent
        // with a custom eventId outside 100–153 and 230–231 to avoid collisions
        // (see the Constants.EventId* table for the used range).
        // const ushort MyModStateSyncId = 50000;
        // Network.Instance.EventManager.SendEvent(MyModStateSyncId, mySnapshot,
        //     NetworkMessageSendOption.Reliable, "MyMod.StateSync");
        GD.Print($"[MyMod] new member joined (index={member.Index}); replaying state");
    }

    private static void OnMemberLeft(INetworkLobbyMember member)
    {
        GD.Print($"[MyMod] member left (index={member.Index}); cleaning up per-member state");
    }

    public static void ModDestroy()
    {
        var lobby = Network.Instance?.LobbyManager;
        if (lobby == null) return;
        lobby.OnMemberJoined -= OnMemberJoined;
        lobby.OnMemberLeft   -= OnMemberLeft;
    }
}
```

Key facts:
- **Host check:** `Network.Instance.LobbyManager.IsLobbyOwner` (bool property on `NetworkLobbyManagerBase`). There is **no** `Network.IsHost` shortcut — that's invented and doesn't exist.
- **Singleplayer check:** `Network.Instance.LobbyManager.IsSingleplayerLobby`. Always true for offline play even though `Network` itself is still up.
- **Local member identity:** `Network.Instance.LobbyManager.LocalLobbyMember` (`INetworkLobbyMember` — exposes `Index`, `IsLocal`, `IsServer`, `GetUserId()`).
- **All members:** `Network.Instance.LobbyManager.LobbyMembers` (List).
- **Joiner notifications:** subscribe on `NetworkLobbyManagerBase.OnMemberJoined` / `OnMemberLeft` (instance `Action<INetworkLobbyMember>` fields). `LateJoinManager` is *not* the right hook — it has no public events; it's the manager that the *game* uses, not what mods subscribe to.
- **Custom network event ids:** pick anything outside `100–153` (gameplay events) and `230–231` (stats events) to avoid future-Pratfall collisions. Document your ids in your README so two mods don't pick the same one.
- **README compatibility tag:** mod authors in comparable communities (Risk of Rain 2, Lethal Company, REPO) self-tag mods as one of:
  - **Client-side only** — visual / UI only; the host doesn't need your mod, lobby members with or without it are compatible
  - **Host-only** — only the host runs the logic; clients are unaffected
  - **All players need this** — protocol-level changes; mismatched lobbies break in subtle ways

  Pratfall's mod framework can negotiate this automatically when both sides have it, but vanilla mods should at least *declare* it in their README so players know what to expect.

## Recipe: Extend a drop pool

Add an entry to a `RandomWeightedDropPool` resource — Robert's recommended pattern for content mods. The pool resources are wired up in scene files (`.tscn`/`.tres`), not loaded by code at known paths, so the practical access pattern is to iterate `DebugMappingManager.Instance.DropPools` at runtime and identify the pool you want by its `ResourceName` (Godot's resource-identifier inherited from `Godot.Resource`).

```csharp
using Godot;
using System.Linq;

public static class ModEntry
{
    private static RandomWeightedDropPool? _pool;
    private static RandomWeightedScene? _entry;

    public static void ModInit()
    {
        // DebugMappingManager.Instance.DropPools is the array Pratfall populates
        // from the active scene's drop-pool wiring. Iterate to find yours.
        // Identifying by ResourceName works when the .tres-author set one;
        // otherwise fall back to ResourcePath or index.
        var pools = DebugMappingManager.Instance?.DropPools;
        if (pools == null || pools.Length == 0)
        {
            GD.PrintErr("[MyMod] no DropPools array on DebugMappingManager — wrong scene context?");
            return;
        }
        _pool = pools.FirstOrDefault(p => p?.ResourceName == "FoodDropPool");
        if (_pool == null)
        {
            // List what IS available to help mod authors find the right name.
            GD.PrintErr($"[MyMod] FoodDropPool not found. Available: {string.Join(", ", pools.Where(p => p != null).Select(p => p.ResourceName))}");
            return;
        }

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

Gotchas:
- `DebugMappingManager.Instance` is null until the level/scene that wires it up has loaded — register your drop-pool extension from a scene-load hook, not at framework init / main-menu time, or guard with a null-check.
- `ResourceName` is set by the .tres / scene author. If `FoodDropPool` isn't the exact identifier in your target scene, the error branch above logs what IS available so you can iterate. Pratfall has zero `res://...DropPool*` paths in its IL (Cecil-confirmed) — pool identity is scene-data, not code-data.

## Recipe: Custom Godot types

Mods that ship `.tscn` files or instantiate custom Godot-derived types (`class MyComponent : Node3D`, `class MyResource : Resource`, `[GlobalClass]` attributes) need their assembly registered with Godot's script bridge. Pratfall's loader does this automatically when your manifest has `AddAssemblyToGodot: true` (the default).

So: **don't set `AddAssemblyToGodot: false`** unless you have a specific reason. No code recipe is needed — the registration is handled by the loader, not your mod.

Under the hood, Pratfall calls `Godot.Bridge.ScriptManagerBridge.LookupScriptsInAssembly(yourAssembly)` after loading your DLL. If you ever need to call it manually (e.g. for a runtime-loaded sub-assembly), you can:

```csharp
Godot.Bridge.ScriptManagerBridge.LookupScriptsInAssembly(myAssembly);
```

## Recipe: Unpack + repack PCK files

Pratfall ships its assets as a single Godot PCK (`Pratfall.pck` next to the executable). Mods that need to **inspect** the game's assets (to find `res://` paths, override existing scenes, or reference a built-in texture) or that need to **ship their own assets** (custom scenes, textures, audio) work with PCK files directly.

### Unpacking `Pratfall.pck` to see what's inside

Use [**gdsdecomp**](https://github.com/bruvzg/gdsdecomp) — community-maintained Godot 4 PCK extractor + decompiler. Point it at `<Pratfall install>\Pratfall.pck` and it dumps the whole tree:

- Scenes (`.tscn`, `.scn`) — open these in Godot to see node structure
- Resources (`.tres`, `.res`) — configs, materials, animations
- Imported assets (textures, audio, fonts) with their `.import` side files
- GDScript files **if any** — Pratfall is pure C#, so this is usually empty

What's NOT in the PCK:

- **C# scripts and types** — those live in `Pratfall.dll`. Use a .NET disassembler ([ILSpy](https://github.com/icsharpcode/ILSpy), [dnSpy](https://github.com/dnSpy/dnSpy), or `Mono.Cecil` programmatically) to inspect those.
- **Native libraries** — `.dll` / `.so` / `.dylib` files sit next to the executable, not inside the PCK.

Typical use cases for unpacking:

- Find the `res://` path of a vanilla scene or texture you want to swap out via `ResourceLoader.LoadOverride(...)` or by mounting your own PCK at the same path.
- Read a Pratfall `.tres` config to understand its structure before extending it (e.g., `DropPoolConfig`, `LevelConfig`).
- Locate the right `AudioStream` / `SpriteFrames` resource path to reference in your mod's code.

### Packing your mod's assets into a `.pck`

Your mod project is a Godot project. Asset layout matters because **Pratfall mounts your PCK at `res://<YourModFolderName>/`** — the folder name from your mod's install directory becomes the namespace for everything inside.

1. **Create the mod project** in Godot:

   ```
   YourModProject/
   ├── YourModProject.godot
   ├── YourModFolderName/        ← MUST match your install folder name exactly
   │   ├── scenes/
   │   │   └── MyScene.tscn
   │   ├── textures/
   │   │   ├── icon.png
   │   │   └── icon.png.import    ← auto-generated; MUST be included in PCK
   │   └── audio/
   │       ├── ding.ogg
   │       └── ding.ogg.import    ← auto-generated; MUST be included in PCK
   └── ...
   ```

2. **Import assets in the Godot editor** before packing — Godot generates a `.import` side file for every audio/texture/font/etc. Without these, the runtime can't actually load the resource even if the raw file is in the PCK. Open the project once, let the editor scan, then save.

3. **Export the PCK** — Godot editor:

   - `Project → Export → Add...`
   - Choose any preset (PCK doesn't actually need platform-specific binaries; Windows Desktop is fine)
   - Click **`Export PCK/Zip...`** (NOT "Export Project" — that builds an EXE you don't want)
   - Save as `YourMod.pck` next to your mod's DLL

4. **Reference the PCK in your mod's manifest**:

   ```json
   { "Name": "YourMod", "Assembly": "YourMod.dll", "PackageName": "YourMod.pck" }
   ```

   Or framework schema:

   ```json
   { "id": "YourMod", "assemblyFile": "YourMod.dll", "pckFile": "YourMod.pck" }
   ```

5. **Reference assets from your DLL** using the mounted path:

   ```csharp
   var scene = GD.Load<PackedScene>("res://YourModFolderName/scenes/MyScene.tscn");
   var icon = GD.Load<Texture2D>("res://YourModFolderName/textures/icon.png");
   var ding = GD.Load<AudioStream>("res://YourModFolderName/audio/ding.ogg");
   ```

**PCK packaging gotchas:**

- **Folder name = mount path.** If your install folder is `Author.MyMod` but your PCK has assets under `res://MyMod/`, the resource loader won't find them. The folder name in your Godot project's filesystem MUST match the mod's install directory name.
- **`.import` side files are mandatory.** They contain texture flags, audio import settings, etc. Forgetting them produces silent runtime load failures ("Could not load resource…").
- **PCKs cannot be unmounted in Godot 4.** Disabling a mod removes its DLL from the load context but the PCK's assets stay reachable via `res://` until the next game restart. The framework surfaces a "may not fully apply until next launch" notice for mods with a `pckFile`.
- **One mod, one PCK.** Two mods with assets at the same `res://<DirName>/...` path silently overwrite each other based on PCK mount order — another reason folder names must be unique across all installed mods.

## Decoded Pratfall surface inventory

Audit of `Pratfall.dll` (2026-05-17 — Pratfall `1.1.0.R2943`) — 822 game types analyzed (skipping Epic / NAudio / SixLabors / ImGuiNET / K4os / MemoryPack / System / Steamworks namespaces). All numbers below are Cecil-verified.

**Spot-check follow-up for `1.1.0.R2973` (2026-05-18 Workshop update):** the modding subsystem was substantially restructured (ModManager got `LoadAllModManifests`, `LoadedMods`, `OnModsLoaded`, `ModsDirectory`, `Setup`, new Workshop-loading methods; `GetModManifest` renamed `GetModManifestFromDirectory` AND privatized; `ModManifest` gained `IsSteamWorkshopMod` / `SteamWorkshopManifest` / `SteamWorkshopItem` properties). The 822-type total isn't materially different; specific ModManager API renames are flagged inline in the [ModManager](#modmanager-pratfalls-native-mod-loader) section. The non-modding inventory (singletons, events, configs, components, GameplayTags, EventIds) was not re-audited in full and may have small drift — re-Cecil before relying on a specific signature.

This section is a **reference map**, not a tutorial. The goal: when you're mid-mod and you need to know "is there a manager for X?" or "what events fire when a player dies?", you should be able to find the answer here instead of disassembling `Pratfall.dll` yourself.

### "How do I ...?"

| Goal | Look at |
|---|---|
| Play a sound | `AudioManager.Instance` (SFX), `MusicManager.Instance` (music), `UISoundManager.Instance` (UI clicks) |
| Read which player is me | `Player.LocalPlayer` (static field on `Player`) |
| Find the local player's components | `Player.LocalPlayer.<ComponentName>Component` — every component on `IEntity` is a property (see [Entity hierarchy](#entity-hierarchy--ientity)) |
| React to a save | `SavegameManager.OnGameWillSave` / `OnGameDidSave` ([recipe](#recipe-persist-mod-data)) |
| React to a player dying | Subscribe `GameEventBus.OnGameEventReceived` and compare `tag.Equals(GameplayTags.Stats_Gameplay_Player_Death)` ([recipe](#recipe-listen-to-game-events)) |
| Send a custom network message | `Network.Instance.EventManager.SendEvent(ushort eventId, T evt, NetworkMessageSendOption opt, string name)` — pick an ID that doesn't collide with [`Constants.EventId*`](#constantseventid-56). `Network.Instance.EventManager` is an instance, not a static class |
| Add a HUD prompt ("Press [A]") | `ButtonPrompBarController.Instance.AddButtonPrompt(...)` ([recipe](#recipe-show-hud-button-hints)) |
| Add an in-game language | Drop a JSON in `<userData>/localization/` ([recipe](#recipe-add-a-language)) |
| Add a possible item drop | Mutate `DebugMappingManager.Instance.DropPools[i].Pool` ([recipe](#recipe-extend-a-drop-pool)) |
| Add a custom `Node` / `Resource` type | Set `AddAssemblyToGodot: true` in manifest ([recipe](#recipe-custom-godot-types)) |
| Change a game mode / level / color | **DON'T** — those arrays are save-coupled by index. See [Save-coupled arrays](#save-coupled-arrays--dont-mutate) |
| Spawn an entity from code | `ScenePoolManager.Instance.Instantiate(packedScene, parent)` for local-only, `Network.Instance.ComponentManager.SpawnNetworkPrefab(prefab, parent)` for replicated ([recipe](#recipe-spawn-an-entity)) |
| Hook game ticks | Override `_Process` / `_PhysicsProcess` on a `Node` you parent under `Game.RootNode`, or use `MainThreadDispatcher.Instance.Enqueue(Action)` for one-shot off-thread → main-thread dispatch |
| Get the user save folder | `Game.Platform.GetUserDataPath()` then `ProjectSettings.GlobalizePath(...)` for a real filesystem path |
| Know which config is "the game settings" | `Game.Config` — but it's a struct with `init`-only setters, you can read but not mutate |

### Singletons (73)

A *singleton* here is a public class with a static `Instance` field or static-getter property. Access via `<Name>.Instance.<Member>`. Many are HUD/UI controllers that are **null on the main menu** — they only exist while a gameplay scene is loaded.

**Game state & flow**
- `GameController` — top-level game state, level loading orchestration
- `GameModeManager` — game-mode list + active mode (`Modes` array is save-coupled, see [pitfalls](#save-coupled-arrays--dont-mutate))
- `CustomGameManager` — custom-game preset state
- `LevelManager` — level prefab list (`LevelPrefabs` array is save-coupled)
- `LifecycleManager` — drives `_Ready` / `_Process` / `_PhysicsProcess` ordering for `ILifecycleHandler`s
- `SceneManager` — scene transition queue
- `Loader` / `Preloader` / `LoadingScreenManager` — resource + scene loading pipeline
- `ScenePoolManager` — pooled scene instances (for `IPooledObject` reuse)
- `DebugMappingManager` — game's drop-pool registry (`DropPools` array). Populated by the active scene, not by code

**Audio**
- `AudioManager` — SFX
- `MusicManager` — music tracks
- `UISoundManager` — UI clicks
- `WaterAudioManager` — water-surface ambience
- `CollisionSoundManager` — physics-impact audio
- `MainMenuAmbienceAudio` — main-menu loop

**Visual / rendering**
- `DynamicParticleManager` — particle pool
- `DynamicResolutionManager` — auto-resolution scaling
- `ExplosionManager` — explosion pooling
- `InstanceDrawManager` — instanced-mesh draw batching
- `FreeFlyCamera` — debug-camera (toggled by F-key)

**Input**
- `InputManager` — cursor + input source
- `InputButtonMappingManager` — keybind/gamepad-mapping registry

**Network**
- `Network` — multiplayer root
- `NetworkGroupManager` — replicated-group registry
- `LateJoinManager` — mid-game-join state sync

**Players**
- `PlayerManager` — connected-player registry
- `PlayerSpawnManager` — respawn logic
- `PlayerHudController` — HUD root for the local player
- `PlayerCompassHudController` — off-screen player markers
- `PlayerEmoteUIController` — emote wheel
- `ChaosTricksManager` — random-event ("chaos trick") scheduler

**UI controllers** (most are null until the relevant screen is open)
- `ButtonPrompBarController` — HUD prompt bar (null on main menu)
- `PauseMenuUIViewController`
- `InventoryUIController`
- `GameOverUIController` / `GameOverGifCaptureUIController`
- `DemoEndScreenUIController`
- `DepthMeterUIController`
- `DialogUIViewController`
- `HudMarkerUIController`
- `StoryPanelUIController`
- `ToastUIController` — popup notifications
- `UIViewControllerManager` — view-controller stack
- `CharacterEditorUIController`
- `GameCustomizerUIController`
- `CustomGameModeUIController`
- `ListenInputUIViewController` — keybind-capture overlay
- `AchievementSummaryUIController`
- `MenuDogAnimationsComponent` — main-menu dog (cosmetic)
- `PerformanceMonitorUIController` — fps overlay
- `SpeedrunUIController`

**Localization & saves**
- `LocalizationManager` — language + user-locale loader ([recipe](#recipe-add-a-language))

**Events**
- `GameEventBus` — game-wide tagged pub/sub ([recipe](#recipe-listen-to-game-events))

**Speedrun / instrumentation**
- `SpeedrunManager` — splits + PB tracking
- `LiveSplitManager` — LiveSplit integration
- `TestRunner` — internal test harness

**Performance / threading**
- `BudgetWorker` — frame-budgeted background work
- `JobManager` — job-system root
- `MainThreadDispatcher` — `Enqueue(System.Action)` queues a one-shot delegate for execution on the main thread next `_Process`
- `DeferredManager` — end-of-frame callbacks
- `GcManager` — `ListenForGcEvents()` etc. for GC instrumentation

**Steam / DLC**
- `SteamUpdater` — Steam SDK tick
- `SteamVoiceSettingsHelper` — voice-chat settings shim

**Misc / world**
- `WorldEntity` — root world entity
- `WorldTextManager` — floating-text labels
- `YarnBallEntity` — main-menu yarn ball (cosmetic)
- `ProceduralCaveComponent` — procedural cave generation singleton (also referenced via save-coupled `BiomeGenerationConfigs`)
- `NodeInstanceRegistry` — scene-node lookup by ID
- `NodeCounter<T>` — debug-only generic node counter
- `ImGuiGodot.ImGuiController` — Dear ImGui integration (debug builds)

### Static helper classes (22)

C# `static class` (no `Instance` — call methods directly via `<Name>.<Member>`). The line between "helper" and "manager" is fuzzy in Pratfall; what these all share is no instance state.

- `BuildHelper` — build-info constants
- `DialogHelper` — modal-dialog helpers (`ShowDialog(...)`, `ShowConfirm(...)`)
- `DlcHelper` — DLC ownership checks
- `EcsHelper` — entity/component helpers (`Spawn`, `Despawn`, `GetComponentRef<T>`, etc.)
- `EosHelper` / `EosManager` / `EosP2PManager` — Epic Online Services wrappers
- `FileHelper` — JSON + binary file IO conveniences
- `GodotHelper` — `Node` / `Resource` helpers
- `Helper` — math/string grab-bag
- `InputSettingsHelper` — keybind/gamepad-mapping IO
- `LeafGrowerHelper` — tree-leaf placement helpers (procedural)
- `LifecycleHelper` — lifecycle-handler registration helpers
- **`ModManager`** — Pratfall's native mod loader (substantially expanded in the 2026-05-18 `1.1.0.R2973` Workshop update). Public surface in R2973: `Setup()`, `LoadAllModManifests(Action onComplete)`, `LoadedMods` (List<ModManifest>), `OnModsLoaded` (Action callback fired after `LoadAllModManifests` completes — useful if you want to react to "mods are ready"), `ModsDirectory` (string, active mod folder — changes with `--qh-mod-directory`), `IsInitialized`, `EnabledModCount`, `EnableMod(ModManifest)`, `DisableMod(ModManifest)`, `IsModEnabled(ModManifest)`, `ShouldLoadMods` getter (defined but currently unused per Cecil), `ShouldHideModLoaderUi` getter. **Note**: `GetModManifest(string)` was renamed `GetModManifestFromDirectory(string)` AND made private — if you used the old name in pre-R2973 builds, you'll need to switch to iterating `LoadedMods` directly. ([lifecycle recipe](#lifecycle))
- `NetworkHelper` — common multiplayer helpers
- `PerformanceHelper` — perf-counter conveniences
- `SaveDataManager` — low-level read/write of save blobs (the file-IO half)
- `SavegameManager` — save lifecycle + events (the orchestration half — see [recipe](#recipe-persist-mod-data))
- `SentryHelper` — Sentry crash-reporter integration
- `SettingsManager` — settings load/save (read `GeneralSettings`, `AudioSettings`, `VideoSettings`, `InputSettings`)
- `SteamLeaderboardHelper` — Steam leaderboard wrappers
- `TimeFormatHelper` — duration formatting

### Configs & Settings (26)

`*Config` and `*Settings` types — game-tuning data. Most are read via `Manager.Instance.Config` or `Game.Config`. **Don't mutate at runtime** — they're either struct-by-value (changes don't stick) or save-coupled (mutating breaks other players' saves).

| Type | Where you read it | Notes |
|---|---|---|
| `GameConfig` | `Game.Config` | Top-level — `AllowUserLocalization`, `BuildId`, … `init`-only setters, struct semantics |
| `NetworkConfig` | game internals | network-tuning |
| `NetworkPrefabsConfig` | `NetworkComponentManager` | networked-prefab registry |
| `GameModeBaseConfig` + `GameModeCustomConfig` / `GameModeSpeedrunConfig` / `GameModeStoryConfig` | `GameModeManager.Modes[i]` | per-mode config — save-coupled |
| `AudioStreamsPreloadConfig` | `AudioManager` | audio preload list |
| `BiomeConfig` + `BiomeGenerationConfig` | `ProceduralCaveComponent` | biome tuning — multiplayer-deterministic, don't mutate |
| `MaterialConfig` | physics + audio | per-material physics/sound rules |
| `PlayerColorsConfig` | `Player.SetupNetwork` | color list — save-coupled by index, don't mutate |
| `PotGenerationConfig` | pot spawning | item-pot tuning |
| `StatsConfig` | stat tracking | which stats are tracked |
| `AvatarCosmeticConfig` + `CosmeticConfig` | character editor | unlockable cosmetics |
| `DlcConfig` | DLC manifest | per-DLC content map |
| `EosConfig` / `SteamConfig` | platform helpers | platform credentials |
| `AnalyticsConfig` | analytics | event-pipeline config |
| `SceneLoadSettings` | `SceneManager` | scene-load defaults |
| `CustomGameSettings` | `CustomGameManager` | custom-game rule set (also used by `RandomWeightedScene.SettingsType`) |
| `GeneralSettings` / `AudioSettings` / `VideoSettings` / `InputSettings` | `SettingsManager` | user-tweakable settings — these DO get mutated by the in-game settings menu |

### Events you can subscribe to (11)

Mod-relevant public events (filtered to public `add_*` methods on Pratfall's own types):

| Event | Where | Delegate | Notes |
|---|---|---|---|
| `OnGameWillSave` | `SavegameManager` (static) | `SaveDataCallback ()` | Fires before save — flush your mod state here |
| `OnGameDidSave` | `SavegameManager` (static) | `SaveDataCallback ()` | Fires after save |
| `OnLocalChanged` | `LocalizationManager` (static) | `LocaleChanged (string locale)` | Active language changed — refresh any cached translated strings |
| `OnGameEventReceived` | `GameEventBus` (static) | `GameEventReceived (GameplayTag, IGameEvent)` | Game-wide pub/sub ([recipe](#recipe-listen-to-game-events)) |
| `OnNetworkEventReceived` | `Network.Instance.EventManager` (instance) | `NetworkEventReceived (...)` | Low-level network event — `Constants.EventId*` IDs. `Network.Instance` is null until the Network singleton is `_Ready`; gate subscription on a non-null check |
| `OnGetNetworkSpawnParent` | `NetworkComponentManager` | `NetworkSpawnParentCallback` | Override the parent node for spawned networked objects |
| `OnGcTiming` | `GcTimingListener` | `Action<GcTiming>` | GC-pause measurements (perf instrumentation) |
| `OnValueChanged` / `OnRemoteValueChanged` | `NetworkVar<T>` / `NetworkVarNode<T>` | `Action<T>` | Per-instance — fires when a replicated value changes |

There is **no `OnGameDidLoad`**. The game's `Setup(...)` accepts an `onGameDidLoad` callback that only the game itself subscribes to. For mods, load your state in `ModInit` by reading your file directly.

### `GameplayTags.*` (40)

`GameplayTag` resources pre-loaded from `res://data/gameplay_tags/*.tres` by the `GameplayTags` static class. Use these for `GameEventBus` filtering — compare with `incomingTag.Equals(GameplayTags.X)` (value equality on `.Tag` string, so the static-vs-runtime instance gotcha doesn't bite you).

**Stats / gameplay events** (fired by the game when these things happen — subscribe to track player actions)
- `Stats_Gameplay_Player_Death`, `Stats_Gameplay_Player_Damage`, `Stats_Gameplay_Fall_Damage`, `Stats_Gameplay_Heal`
- `Stats_Gameplay_Caught_Player`, `Stats_Gameplay_Threw_Flare`, `Stats_Gameplay_Bat_Hit`, `Stats_Gameplay_Worm_Hit`
- `Stats_Gameplay_Open_Package`, `Stats_Gameplay_Ball_For_Dog`, `Stats_Gameplay_Unconscious`
- `Stats_Gameplay_Ate`, `Stats_Gameplay_Ate_Freeze_Pop`, `Stats_Gameplay_Ate_Grape_Juice`
- `Stats_Gameplay_Stick_Chameleon_Grenade`, `Stats_Gameplay_Stuck_Sticky_Bomb`
- `Stats_Gameplay_Depth_Reached`, `Stats_Gameplay_New_Depth`, `Stats_Gameplay_Finish_Game`, `Stats_Gameplay_Win`
- `Stats_Gameplay_Revived_Player_Direct`, `Stats_Gameplay_Revived_Player_Statue`
- `Stats_Gameplay_Died_By_Explosion`
- `Stats_Unlocked`

**Win conditions / game state**
- `Challenge_Win`, `Demo_Win`, `Game_Restart`

**Status effects & debug**
- `Curse_Lollypop`, `Debug_Godmode`, `Collision_Ignore_Player`

**Surface materials** (used by physics/audio for impact rules)
- `Material_Wood`, `Material_Stone`, `Material_Metal`, `Material_Glass`, `Material_Organic`, `Material_Sand`, `Material_None`

**Harvestables** (ground-resource categories)
- `Harvestable_Wood`, `Harvestable_Stone`, `Harvestable_Revive`

### `Constants.EventId*` (56)

`ushort` (System.UInt16) constants holding numeric event IDs (`Constants.EventIdJump = 129`). Used by `Network.Instance.EventManager.SendEvent(UInt16 eventId, T evt, NetworkMessageSendOption opt, string eventIdName)` for **low-level network messages** — the `eventIdName` parameter is a separate human-readable debug-name string, NOT the event id itself. Different system from `GameEventBus` / `GameplayTags` — don't mix them.

Sorted by numeric ID:

| ID | EventId | ID | EventId |
|---|---|---|---|
| 100 | `EventIdInteraction` | 128 | `EventIdContactDamage` |
| 101 | `EventIdEmote` | 129 | `EventIdJump` |
| 102 | `EventIdCameraShake` | 130 | `EventIdBootApply` |
| 103 | `EventIdRequestLevelLoad` | 131 | `EventIdPlayHungrySound` |
| 104 | `EventIdStartMission` | 132 | `EventIdDigPlayerFree` |
| 105 | `EventIdEndMission` | 133 | `EventIdChangeMaterialAt` |
| 106 | `EventIdDebugShowItemTray` | 134 | `EventIdChangeFloorAt` |
| 107 | `EventIdTakeDamage` | 135 | `EventIdSetUnconscious` |
| 108 | `EventIdApplyImpulse` | 136 | `EventIdDropInventory` |
| 109 | `EventIdShootCannon` | 137 | `EventIdBootApplyStart` |
| 110 | `EventIdShovelPosition` | 138 | `EventIdShowToast` |
| 111 | `EventIdRequestRevive` | 139 | `EventIdPlayEmote` |
| 112 | `EventIdTookDamage` | 140 | `EventIdBatEat` |
| 113 | `EventIdExplode` | 141 | `EventIdTriggerContact` |
| 114 | `EventIdNetworkGroupUnregistered` | 142 | `EventIdTeleport` |
| 115 | `EventIdGameOver` | 143 | `EventIdKnockBat` |
| 116 | `EventIdCloseGameOverUI` | 144 | `EventIdRequestStartTeleportEffect` |
| 117 | `EventIdGameRestart` | 145 | `EventIdTriggerGameEnd` |
| 118 | `EventIdCaughtPlayer` | 146 | `EventIdTriggerExtractor` |
| 119 | `EventIdLoadedLevel` | 147 | `EventIdQuickRestart` |
| 120 | `EventIdUnloadLevel` | 148 | `EventIdSetLevelActive` |
| 121 | `EventIdUnloadLevelAck` | 149 | `EventIdNotifyFlareStick` |
| 122 | `EventIdEquipCosmetic` | 150 | `EventIdBatHitWithObject` |
| 123 | `EventIdHonk` | 151 | `EventIdUpdateCustomGameSettings` |
| 124 | `EventIdLaserBeamSpawn` | 152 | `EventIdResetRagdoll` |
| 125 | `EventIdPickaxeAction` | 153 | `EventIdRequestMarkLateJoin` |
| 126 | `EventIdEnemySpit` | 230 | `EventIdGameModeChanged` |
| 127 | `EventIdGenerateBranch` | 231 | `EventIdSubmitSpeedrunTime` |

Used range: 100–153 contiguous, plus 230–231 for stats events. If you ship a custom network event, pick an ID outside those ranges to avoid collisions with future Pratfall releases.

### Entity hierarchy & `IEntity`

Pratfall's game objects extend Godot nodes but also implement `IEntity` (and often `ILifecycleHandler` for ordered `_Process` ticks). The hierarchy looks like:

```
Godot.Node                          Godot.Node3D                  Godot.RigidBody3D / StaticBody3D
   |                                    |                              |
NodeEntity : IEntity              Node3DEntity : IEntity        RigidBody3DEntity : IEntity   StaticBody3DEntity : IEntity
   |                                    |                              |                              |
managers (LevelManager,           managers (WorldEntity,        Player                        YarnBallEntity
GameModeManager, etc.)            DynamicParticleManager,
                                  CharacterEditorCamera, etc.)
```

Concrete entities (23 total, Cecil-counted):
- `NodeEntity`, `Node3DEntity`, `RigidBody3DEntity`, `StaticBody3DEntity` — base classes
- `Player` (extends `RigidBody3DEntity`) — **the main thing mods care about**
- `WorldEntity`, `YarnBallEntity` — world-root entities
- Managers that are also entities: `CollisionSoundManager`, `CustomGameManager`, `DebugMappingManager`, `DynamicParticleManager`, `ExplosionManager`, `FreeFlyCamera`, `GameModeManager`, `HangDebuggerNode`, `InstanceDrawManager`, `LevelManager`, `NetworkGroupManager`, `ScenePoolManager`, `SpeedrunManager`, `StoryPanelManager`, `WorldTextManager`
- Cameras: `CharacterEditorCamera`, `SpectatorCamera`

**`IEntity` exposes 185 properties** — 184 component-accessors (one per `IComponent` subclass) plus `Components: Dictionary<int, IComponent>` for dynamic access. This is the killer feature for mods:

```csharp
// Instead of GetComponent<PlayerHealthComponent>() everywhere, you just write:
var hp = Player.LocalPlayer?.PlayerHealthComponent;
// PlayerHealthComponent is actually Pratfall's FOOD/HUNGER component despite
// the "Health" name. Real fields per Cecil dump: FoodValue (UInt16 current),
// MaxFoodValue (UInt16 cap), FoodNormalized / HungerNormalized (0-1 floats),
// FoodConsumptionPerSecond, HungrySoundThreshold, HungrySound. There is NO
// CurrentHealth / MaxHealth field — those are on Pratfall's other body
// (HitPointsComponent etc.). For "fill me up to max food":
if (hp != null) hp.FoodValue = hp.MaxFoodValue;

// Same pattern for any component on any entity:
var flare = Player.LocalPlayer?.ThrowFlareComponent;
var inv   = Player.LocalPlayer?.InventoryComponent;
var cam   = Player.LocalPlayer?.PlayerCameraComponent;
```

**Each property returns `null` when the entity doesn't have that component.** This is the IEntity contract — accessing `ThrowFlareComponent` doesn't throw, it just returns null if no flare-throwing instance is attached. **Always null-check the return.**

Lower-level access if you need the dictionary lookup:

```csharp
// EcsHelper.GetComponentRef has a ref-out signature (Cecil:
// GetComponentRef<T>(ref T component, Node node, ComponentType componentType)).
// Most mod code should NOT call this — use the IEntity property accessor above.
// If you really need it:
PlayerHealthComponent? hp = null;
EcsHelper.GetComponentRef(ref hp, playerNode, ComponentType.PlayerHealthComponent);

// The Components dictionary is keyed by component-type ID (int), NOT typeof(...):
//   PROP Dictionary<int, IComponent> Components
// Each IComponent has a numeric type id; the IEntity property accessors are the
// readable wrapper. Don't write your own TryGetValue against this dict — you'd
// have to know the type ids by hand. Use `player.PlayerHealthComponent` instead.
```

### `IComponent` implementors (184)

The components you might want to read/mutate on a `Player` or other entity. Categorized by name prefix:

**Player components (33)** — extension points for player behavior. `Player.LocalPlayer.<Name>` accessor is available for each via `IEntity`.

`PlayerAdvancedModeComponent`, `PlayerAmbientParticleComponent`, `PlayerAnimationComponent`, `PlayerCameraComponent`, `PlayerCatchComponent`, `PlayerCheckpointComponent`, `PlayerCollisionComponent`, `PlayerContactDamageComponent`, `PlayerCosmeticsComponent`, `PlayerCrownComponent`, `PlayerDamageAreaComponent`, `PlayerDropAdvantageComponent`, `PlayerEmoteComponent`, `PlayerFallDamageComponent`, `PlayerHandSlotComponent`, **`PlayerHealthComponent`**, `PlayerHealthDrainComponent`, `PlayerHonkComponent`, `PlayerJourneyRecordComponent`, `PlayerLateJoinComponent`, `PlayerMaterialBootComponent`, `PlayerMeshComponent`, `PlayerMonitorComponent`, `PlayerMovementSoundComponent`, `PlayerPickaxeComponent`, `PlayerPingingComponent`, `PlayerReviveComponent`, `PlayerSkeletonComponent`, `PlayerSlideEffectComponent`, `PlayerSpectateOnDeathComponent`, `PlayerTeleportEffectComponent`, `PlayerToastComponent`, `PlayerUnconsciousComponent`.

**Interactable / item components (43)** — things the player can pick up or interact with. The `Interactable*` prefix is consistent.

`InteractableActivateFreezeBootComponent`, `InteractableAudioPlayerComponent`, `InteractableBatteryComponent`, `InteractableBounceComponent`, `InteractableCameraComponent`, `InteractableChameleonBranchComponent`, `InteractableColliderTrackingComponent`, **`InteractableComponent`** (base), `InteractableCrownComponent`, `InteractableDrillerComponent`, `InteractableDrillLauncherComponent`, `InteractableEmissionEnergyCurveComponent`, `InteractableExplosionComponent`, `InteractableExtractorComponent`, `InteractableFeedFoodToPlayer`, `InteractableFlareComponent`, `InteractableFoodVoiceModifierComponent`, `InteractableGravityComponent`, `InteractableGravityModifierComponent`, `InteractableGrenadeComponent`, `InteractableGunComponent`, `InteractableHealthPotionComponent`, `InteractableHolderComponent`, `InteractableLaserGunComponent`, `InteractableLoadLevelComponent`, `InteractableMegaphoneComponent`, `InteractableNodeVisibilityComponent`, `InteractableParticleComponent`, `InteractablePickupItemComponent`, `InteractablePlaySoundComponent`, `InteractableReviveAllComponent`, `InteractableScaleCurveComponent`, `InteractableSetUnconsciousComponent`, `InteractableShowCharacterEditorComponent`, `InteractableShowGameCustomizerComponent`, `InteractableShowInviteOverlayComponent`, `InteractableSpawnerComponent`, `InteractableSpinComponent`, `InteractableStartDrillerComponent`, `InteractableTeleporterComponent`, `InteractableTreasureChestComponent`, `InteractableUnlockDogBallAchievementComponent`, `InteractableWinComponent`, `InteractableZiplineLauncherComponent`.

**Network components (7)** — replicated state for multiplayer.

`NetworkComponent` (base), `NetworkContactComponent`, `NetworkEntitySpawnComponent`, `NetworkGroupComponent`, `NetworkTransformComponent`, `NetworkVoicePlayerComponent`, plus `StatsRuntimeComponent` (stat replication).

**Bat enemy (4)**: `BatEatComponent`, `BatExplodeComponent`, `BatFlyingMovementComponent`, `BatKickComponent`.

**Goblin enemy (2)**: `GoblinMovementComponent`, `GoblinSpawnComponent`.

**Worm enemy (2)**: `WormMovementComponent`, `WormSpawnComponent`.

**Spitting enemy (1)**: `SpittingEnemyComponent`.

**Camera (3)**: `CameraShakeComponent`, `CameraShakeReceiverComponent`, `FirstPersonMovementComponent`.

**Physics / damage (7)**: `ContactListenerComponent`, `DeathComponent`, `DestroyEntityBelowYComponent`, `DestroyOnDeathComponent`, `KnockbackOnHitComponent`, `LifetimeComponent`, `RagdollComponent`.

**Explosion / projectile (5)**: `ExplosionComponent`, `ExplosionInstanceComponent`, `ExplosionReceiverComponent`, `ProjectileRagdollComponent`, `SpawnGrenadesOnExplosionComponent`.

**Voxel / chunk (5)**: `ChunkEntityComponent`, `ChunkLoaderComponent`, `ChunkPhysicsObjectComponent`, `VoxelFieldComponent`, `VoxelFieldInstance`.

**Procedural / world (10)**: `BiomeFlagPoleComponent`, `ChangeLightOverTimeComponent`, `ChangeMaterialContinuouslyComponent`, `CloudRotationComponent`, `EmissiveLightComponent`, `FlickerLightSizeComponent`, `LightBlinkComponent`, `ProceduralCaveComponent`, `WorldEnvironmentBlendComponent`, `WorldEnvironmentSettingsComponent`.

**Animation / mesh (8)**: `AnimationTreePhysicsTickComponent`, `AlignWithVelocityComponent`, `HatFallPropellerComponent`, `MeshRandomizerComponent`, `NodeRandomizerComponent`, `RotateComponent`, `RotateHolderComponent`, `TalkAnimationBlendComponent`.

**Audio (2)**: `AudioPlayerComponent`, `CollisionSoundComponent`.

**Lifecycle / spawn (10)**: `BossHealthComponent`, `BuriedMineComponent`, `CheckpointStatueComponent`, `DespawnWhenTooFarAwayComponent`, `EnableInteractableOnDugOut`, `EnemySpawnerComponent`, `FlagPoleItemSpawnComponent`, `PickupItemOnDeathComponent`, `SpawnEntityOnDeathComponent`, `SpawnMeshOnBounceComponent`.

**Misc / debug (41)** — everything that didn't fit a tighter category: `CoreDamageCrackComponent`, `CoreDeathComponent`, `CrownFloatComponent`, `DepthScoreComponent`, `DiggingMineComponent`, `DogBarkComponent`, `EmoteComponent`, `GamemodeSignComponent`, `GameOverOnAllPlayersDeadComponent`, `GameplayTagComponent`, `GenerateBlobOnContactComponent`, `GenerateBridgeTrajectoryComponent`, `GenerateSpikeOnBounceComponent`, `HealOnKillComponent`, `HitPointsComponent`, `HudMarkerComponent`, `IconComponent`, `ImposterReplaceComponent`, `InteractionComponent`, `InventoryComponent`, `InverseExplosionSnakeComponent`, `LineComponent`, `LoadLevelVolumeComponent`, `NoFallDamageAreaComponent`, `ParticleComponent`, `PickaxeHittableComponent`, `PuppyColorComponent`, `RandomBarkComponent`, `RandomScaleSeedPositionComponent`, `ReviveOnKillComponent`, `ScreenshotComponent`, `ShakeComponent`, `StickyComponent`, `StunAreaComponent`, `TestComponent`, **`ThrowFlareComponent`**, `TrackerHatNumberComponent`, `WaterBubbleComponent`, `WinGameOnDeathComponent`, `ZiplineComponent`, `ZiplineUserComponent`.

### Public interfaces (13)

- `IComponent` — every component implements this. 184 implementors (see above).
- `IEntity` — every entity implements this. 23 implementors (see [Entity hierarchy](#entity-hierarchy--ientity)). Exposes 184 component-accessor properties (one per `IComponent` subclass) plus a `Components` dictionary.
- `IGameEvent` — the payload type for `GameEventBus.SendEvent<T>(GameplayTag, T)`. Concrete event data lives in `GameEvent<T1>` … `GameEvent<T1,T2,T3,T4,T5,T6>` generic carrier types (just `(Value1, Value2, ...)` tuples) — Pratfall doesn't ship named per-event POCOs.
- `INetworkEvent` — payload type for `Network.Instance.EventManager.SendEvent`. Implementors are the per-event records (e.g. `CustomGameManager.CustomGameSettingsNetworkEvent`).
- `INetworkMessage` — base for `INetworkEvent`.
- `INetworkLobby` / `INetworkLobbyMember` — multiplayer-lobby abstractions (Steam vs EOS hide behind these).
- `INetworkVoicePlayer` — voice-chat abstraction.
- `ILifecycleHandler` — opt into `LifecycleManager`-ordered `_Process` / `_PhysicsProcess` ticks. Most managers implement this.
- `IPersistentId` — entities that persist across save/load.
- `IPooledObject` — entities that participate in `ScenePoolManager`.
- `IPreBuildCallback` — `_Ready`-time pre-build hook.
- `ISerializationCallbackReceiver` — pre/post-serialize callbacks.

### `res://` path conventions

Cecil-scanned distinct top-level folders that appear as `res://X/...` string literals in Pratfall.dll:

| Pattern | Contents |
|---|---|
| `res://assets/...` | Art, audio, models, textures — most game content |
| `res://data/...` | Data resources: `gameplay_tags/*.tres`, configs, level metadata |
| `res://data/gameplay_tags/*.tres` | The 40 `GameplayTag` resources loaded by `GameplayTags` static cctor |
| `res://scenes/...` | `.tscn` scene files (rooms, prefabs) |
| `res://materials/...` | Godot material resources |
| `res://addons/...` | Godot editor addons (not loaded at runtime) |
| `res://tests/...` | Internal test scenes (not loaded by `TestRunner` in retail) |
| `res://...` (no folder) | A few root-level resources |

**Mod assets get mounted at `res://<YourModFolderName>/...`** — Pratfall's loader mounts each mod's `.pck` under its folder name. So if your mod folder is `MyMod`, your mod's `scene.tscn` lives at `res://MyMod/scene.tscn`. This is why folder names must be unique across all installed mods.

### Save-coupled arrays — don't mutate

These arrays are referenced by save-game data via **index**. Adding entries shifts every existing player's saved choice and silently corrupts their data. Don't touch:

- `PlayerColorsConfig.Colors: Color[]` — saved color index
- `GameModeManager.Modes: GameModeBaseConfig[]` — saved game-mode index
- `LevelManager.LevelPrefabs: PackedScene[]` — saved level-prefab index
- `ProceduralCaveComponent.BiomeGenerationConfigs` — also affects procedural determinism → multiplayer would diverge
- `OptionsUIViewController.TabBarItems: OptionsContentUIViewBase[]` — milder, but still shifts tab indices

If you need to add a game mode / level / color, your mod has to ship a fresh save profile (advanced — Pratfall doesn't have first-class support for this yet). Stick with extension points that are array-of-records-by-content (like `RandomWeightedDropPool.Pool` — see [drop pool recipe](#recipe-extend-a-drop-pool)) rather than array-of-references-by-index.

## Debugging & dev iteration

### Where logs go

Pratfall is built on Godot 4.6. Anything you write with `GD.Print(...)` or `GD.PrintErr(...)` from your mod ends up in the game's `godot.log`. The file lives under whatever `Game.Platform.GetUserDataPath()` resolves to, in the `logs/` subfolder. To find the exact path on your machine, run from your mod:

```csharp
GD.Print(ProjectSettings.GlobalizePath("user://logs/"));
```

On a typical Steam install this is `%APPDATA%\Godot\app_userdata\Pratfall\logs\` — but don't hard-code it; resolve at runtime. Up to 5 historical log files are kept (rotated), with `godot.log` being the most recent.

### Useful Godot CLI flags

Pass these via Steam → right-click Pratfall → Properties → Launch options, or launch the executable directly from a terminal:

| Flag | Effect |
|---|---|
| `--verbose` | Enables Godot's verbose engine logging in addition to your `GD.Print` lines. |
| `--log-file <path>` | Redirects engine output to a specific file (useful for diff-based debugging across runs). |
| `--qh-skip-mods` | Pratfall flag — skips all mod loading. Use to bisect "is this bug from my mod or vanilla?". |
| `--qh-disable-mod-ui` | Pratfall flag — hides the Mods button. Useful when running with a framework that injects its own UI. |

There is no `--console` flag on Windows for Godot 4 to attach a live stdout console. To see live output, launch the executable from a terminal: `Pratfall.exe > out.log 2>&1` from cmd/PowerShell, then tail `out.log`. Steam's launch-options can take stdout redirection but it's finicky — direct-launch from the install dir is the reliable path.

### Iteration loop

The fastest edit-build-test cycle:

1. **Launch Pratfall directly**, not through Steam. Steam's restart-after-quit is the slow part — running `Pratfall.exe` from the install dir means a kill-and-relaunch is sub-second.
2. **Build directly into the mod folder.** The `InstallMod` MSBuild target in the [Setup csproj template](#setup) copies the DLL into `$(GameDir)\mods\$(ModId)` after each build. Iteration is `dotnet build` → kill game → launch.
3. **Skip the user-confirmation gate on subsequent enables.** Once a mod has been approved, the framework remembers your decision; mid-session edit-build-test doesn't re-prompt. (Vanilla loader doesn't have a gate at all.)
4. **`ModInit` runs once per enable.** To re-test a code path without restarting the game, toggle your mod off → on from the in-game Mods button. `ModDestroy` runs on disable, `ModInit` runs again on enable. **Both must be reentrant** — see [Pitfalls](#pitfalls).

### Attaching a debugger

VS Code with the C# extension can attach to a running game by process name. Pratfall's process is `Pratfall.exe`. Set breakpoints in your mod's source, launch the game, attach via "Run and Debug" → ".NET Core Attach", pick `Pratfall.exe`. Step-through and watch work; **hot-edit does not** — modifying a mod DLL requires a game restart (see [Godot concepts](#godot-4-concepts)).

### Bisecting a multi-mod conflict

If your mod works alone but breaks alongside Mod X:

1. Note your enabled mod list.
2. Restart with `--qh-skip-mods` to confirm Pratfall is healthy without any mods.
3. Re-enable mods one at a time via the in-game Mods button; the conflict surfaces on the offender.
4. With both mods enabled, look at `godot.log` for `[ModId]` prefixed lines from each — the one that throws first usually points at the conflict.

### Smoke test before sharing

Before posting a mod for others:
- **Use Tim's [`quad-head/pratfall-example-mod`](https://github.com/quad-head/pratfall-example-mod) as the known-good baseline**, not your own first attempt. If the example mod loads cleanly on the same Pratfall build and install path but yours doesn't, assume the problem is in your mod first, not the loader. (Mods from this repo's `sample-mods/` folder work for framework development but should NOT be the only proof that the vanilla loader path works — they share too much surface with the framework codebase. Game version, install path, launch flags, and enabled state still matter; rule those out before blaming the loader.)
- When the Pratfall community has enough public mods, test alongside the **3 most-used mods** available for the same Pratfall build. Conflicts you don't expect show up in 3 minutes of play.
- Test on **both Steam-installed paths** if you have a friend who installs Pratfall to `D:\` instead of `C:\Program Files (x86)\Steam`. Hard-coded paths are a classic break.
- Test in a **2-player lobby** if your mod has any multiplayer behavior. Singleplayer doesn't exercise `Network.Instance.LobbyManager` properly — and per the [multiplayer-patterns disclaimer](#recipe-multiplayer-patterns), if you don't have explicit per-mod state sync, the lobby is your only way to know whether host-vs-client divergence ships.

## Distribution conventions

Pratfall's vanilla loader doesn't enforce these — they're community conventions imported from comparable games (Webfishing, REPO, Lethal Company, PEAK) so authors moving between scenes recognize the layout.

### Mod ID format

`AuthorName.ModName` (PascalCase, dot-separated). For example: `Unexpear.BiggerDropPool`. Use this as your mod folder name AND as the `ModId` in your csproj's `<ModId>` property. Uniqueness matters because the folder name is the asset namespace — see [Setup](#setup).

### Folder contents

Alongside your DLL:
- `manifest.json` (required by Pratfall)
- `README.md` — what the mod does, dependencies, multiplayer compatibility, known issues
- `CHANGELOG.md` — version history
- `icon.png` — **256×256** PNG. The current Pratfall loader doesn't surface it yet, but Thunderstore-style mod managers and future framework UI will.
- `LICENSE` — pick one. Default in this community is MIT (the [pratfall-example-mod](https://github.com/quad-head/pratfall-example-mod) is MIT). Without a LICENSE file, default is "All Rights Reserved" and other mod authors legally cannot fork or redistribute your work.

### Version format

Use [Semantic Versioning](https://semver.org/): `MAJOR.MINOR.PATCH`. Bump MAJOR for breaking config / API changes; MINOR for new features; PATCH for bug-fix-only releases. Set the same string in `manifest.json`'s `Version` field and your `.csproj` `<Version>`.

### Multiplayer-compatibility tag in README

Lead your README with one of:
- **Client-side only** — cosmetic / UI only; lobby members with or without your mod can play together.
- **Host-only** — only the host runs the logic; clients are unaffected.
- **All players need this** — protocol changes; mismatched lobbies break in subtle ways. Friends need to install it together.

The Pratfall Mod Framework can detect mismatches and prompt to transfer the mod, but vanilla mods have no such negotiation — players have to coordinate manually. Saying it up front in the README saves the support back-and-forth.

### What NOT to include in your package

- **Other mods' DLLs.** Declare them as dependencies in your README (and in the manifest's `Dependencies` field once that's standardized). Bundling causes duplicate-load conflicts and version skew.
- **Source-game DLLs** (`Pratfall.dll`, `GodotSharp.dll`). These resolve from the game install. Your csproj should reference them with `<Private>false</Private>` (see [Setup](#setup)).
- **Debug builds of your own DLL.** Build with `dotnet build -c Release` and ship the `bin/Release` output, not `bin/Debug`.
- **`*.pdb` files** unless you explicitly want users to be able to get source-line stack traces. They roughly double your DLL footprint.

### Where to publish (as of 2026-05-18)

There's no single official Pratfall mod host yet. Current state, per the dev team in `#mod-dev`:

| Platform | Status | Notes |
|---|---|---|
| **Steam Workshop** | **Shipped 2026-05-18.** First-party path. | Auto-update + re-install across devices. Pratfall's native loader handles subscribe / install via `Steamworks.SteamUGC`, and `ModManifest` gained `IsSteamWorkshopMod` + `SteamWorkshopManifest` + `SteamWorkshopItem` properties so mod code can detect Workshop sourcing. **Caveat: Chinese players may not have Workshop access** (Robert) — consider this if your mod targets that audience. |
| **Nexus Mods** | De facto current host; works today | Manual install only — users download a zip and drop the mod folder into `<GameDir>\mods\`. No auto-update. |
| **Thunderstore** | Community exists; rep (Ebkr) is engaged with the Pratfall team | Standard format for BepInEx-style games (Risk of Rain 2, Lethal Company, REPO, Content Warning). Pratfall is on Godot+C# which is uncommon for the platform, so existing tooling (r2modman) doesn't natively understand the loader yet. |
| **GitHub release / direct download** | Universal fallback | Works for any platform Pratfall runs on. Reasonable for early development; not a great long-term distribution channel. |

**Fragmentation matters.** Ebkr (Thunderstore) flagged the risk of mods splintering across platforms — if your players use one platform and your dependencies are on another, the install path breaks. With Steam Workshop live as of 2026-05-18 it's the natural first-party choice for most mods; supplement with Nexus or direct download for players who can't access Workshop. If you publish on multiple, link cross-platform so users can find the same mod from anywhere.

**Pratfall's uncommon stack matters too.** Tim noted that Godot + C# is rare among modded games, so tooling assumptions made for Unity+BepInEx don't always transfer. If you write a Thunderstore-format manifest, expect to also explain manual install for users whose mod manager doesn't auto-handle Pratfall yet.

## Godot 4 concepts

A few things mod authors hit if they're new to Godot. None of this is Pratfall-specific.

### Node lifecycle

Godot nodes go through:

```
constructor → _EnterTree → _Ready → _Process (every frame) / _PhysicsProcess (fixed tick) → _ExitTree → destructor
```

- `_EnterTree` fires when the node is added to the scene tree.
- `_Ready` fires AFTER all children are ready — safe place to do "find children by name" / setup work.
- `_Process(double delta)` runs every visual frame. **Don't allocate here** — it's a hot path.
- `_PhysicsProcess(double delta)` runs at fixed physics rate (60 Hz default).
- `_ExitTree` fires when removed from the tree.

If you override these on a class shipped in your mod, mark them `public override void` — Godot calls through reflection.

### `PackedScene.Instantiate()` returns a detached node

```csharp
var scene = GD.Load<PackedScene>("res://MyMod/MyProp.tscn");
var node = scene.Instantiate();    // detached — NOT in the tree yet
Game.RootNode.AddChild(node);      // now it's live
```

Forgetting the `AddChild` is the #1 newcomer bug — your code runs, no error, but nothing appears. The node exists in memory but isn't in the scene tree.

### `Resource` is shared by reference

Godot resources (`PackedScene`, `Texture2D`, `RandomWeightedDropPool`, etc.) are reference-counted shared objects. Two `GD.Load<T>` calls for the same path return the **same instance**. If you mutate one, every holder sees the change.

This is *why* the drop-pool recipe works (mutation sticks) but also why you have to undo it carefully on `ModDestroy`. To make a private copy, call `resource.Duplicate(subresources: true)`.

### `user://` vs `res://`

Both are Godot URIs, not filesystem paths:
- `res://...` — read-only path inside the game's mounted PCKs (and your mod's PCK if loaded). Use for assets your mod ships.
- `user://...` — read-write path under the platform's user-data folder. Use for save data, logs, config.

To get a real filesystem path that `System.IO` understands, pass either through `ProjectSettings.GlobalizePath(...)`. Godot's own `DirAccess` / `FileAccess` understand the URIs directly without globalization.

### C# hot-reload doesn't work for mods

GDScript supports hot-reload; C# does not, especially for code loaded via `AssemblyLoadContext`. Modifying your mod's source means: rebuild → game restart → re-test. Steps to make this fast are in [Debugging & dev iteration](#debugging--dev-iteration).

### `GD.Print` vs `Console.WriteLine`

Use `GD.Print(...)` for log output. `Console.WriteLine` works but goes to wherever Godot's stdout is wired (often nowhere visible on Windows builds). `GD.Print` always ends up in `user://logs/godot.log`. For errors use `GD.PrintErr(...)` so they're tagged red in the in-engine console.

## Pitfalls

- **Folder names must be unique across mods.** Pratfall mounts each mod's PCK at `res://<DirectoryName>/...`. Two mods sharing a folder name silently overwrite each other's assets. (Confirmed by Tim in #mod-dev, 2026-05-17.)
- **Filesystem URIs vs paths.** `Game.Platform.GetUserDataPath()` returns a Godot `user://` URI on Steam. Pass it through `ProjectSettings.GlobalizePath(...)` before any `System.IO` call. Godot's own `DirAccess` understands the URI, so game-side code paths work without it — but System.IO does not.
- **Don't mutate save-coupled arrays.** `PlayerColorsConfig.Colors`, `GameModeManager.Modes`, `LevelManager.LevelPrefabs` are all indexed by save-game data — mutating them invalidates existing player saves.
- **`ByteBufferWriter` has a 32 KB string cap.** Affects any custom network protocol built on top of `Network.Instance.EventManager.SendEvent`. Keep payloads under 32 KB after JSON serialization.
- **`Game.Config` is a value-type struct with `init`-only setters.** Mods cannot mutate config flags at runtime — not even via reflection (the `modreq(IsExternalInit)` modifier enforces this at the C# language level, and even reflection-based hacks would write to a copy because `Game.Config` returns the struct by value). If `Game.Config.AllowUserLocalization == false` on the shipped build, your mod can't flip it; either ship a JSON-only locale that side-loads via `TranslationServer.AddTranslation` directly, or wait for the dev to enable the flag.
- **`GameplayTag` vs `Constants.EventId*` are different systems.** `GameplayTags.X` references are for `GameEventBus.SendEvent` / `OnGameEventReceived` (the high-level pub/sub — `GameEventBus` IS a singleton with `Instance`, but the event itself is static). `Constants.EventId*` are `const ushort` numeric IDs (values like `129`, `115`) for `Network.Instance.EventManager.SendEvent(UInt16 eventId, ...)` (the low-level network event channel — `NetworkEventManager` is an *instance* accessed through the `Network` singleton, NOT a static class). Don't mix them — subscribing to a `GameEventBus` handler hoping a numeric event id will match would match nothing.
- **`ModEntry` class name is exact.** Pratfall uses `assembly.GetType("ModEntry")` — case-sensitive, no namespace.
- **`ModInit` / `ModDestroy` reentrance.** Mods can be enabled → disabled → enabled multiple times per session. Make both methods idempotent: every subscription paired with an unsubscribe, every array growth paired with a shrink.
- **`AssemblyLoadContext.Unload()` is called on disable.** Don't hold long-lived references to game types in static fields outside the mod's `ModEntry` — the GC needs to collect your assembly's load context.
- **HUD-attached singletons are null on the main menu.** `ButtonPrompBarController.Instance` and similar HUD pieces are only present during gameplay. Null-check before use.
- **Don't allocate in `_Process` / `_PhysicsProcess` hot paths.** These run every frame / every physics tick. Allocating boxes / temp lists / closures generates GC pressure that Pratfall already instruments (`GcTimingListener.OnGcTiming` event for measuring; `GcManager.Instance` for blocking GC during sensitive sections — note `GcManager` is a *blocker*, not an event source, despite its name). Pool what you can, cache lookup results, and prefer `for` over LINQ on per-frame code.
- **Don't touch Godot objects from a background thread.** Godot 4's C# API is main-thread-only — calling `node.Position = ...` or `resource.Duplicate()` from a `Task.Run` / timer thread crashes silently or corrupts state. If you have off-thread work, marshal back via `MainThreadDispatcher.Instance.Enqueue(() => { /* main-thread code */ })`.
- **Don't `[GlobalClass]`-collide with a game type name.** Godot 4 has a global class registry shared between the game and your mod's `AddAssemblyToGodot: true` types. If your `[GlobalClass] class Player : Node3D { }` collides with Pratfall's own `Player`, registration loses silently and your scenes won't instantiate it. Namespace your `[GlobalClass]` types or use unambiguous names.
- **Don't open mod `.tscn` files outside Pratfall's decompiled Godot project.** Opening a `.tscn` that references `Pratfall.dll` types in a fresh Godot editor instance will offer to "fix missing dependencies" and silently strip references to game types. You'll save the file and your scene will be missing every Pratfall-specific node. Either work inside a Godot project that has Pratfall's types available, or edit `.tscn` files as text only.
- **Don't ship other mods' DLLs inside your mod folder.** Two copies of the same assembly in two different `AssemblyLoadContext`s create type-identity confusion (`typeof(X)` from one ALC isn't equal to `typeof(X)` from the other). Declare your dependencies in your README; let users install them separately.

## Resources

- **Tim's example mod** — [`quad-head/pratfall-example-mod`](https://github.com/quad-head/pratfall-example-mod) — the canonical reference.
- **Discord** — `#mod-dev` channel of the Pratfall dev server (Tim, Robert, and active modders coordinate there).
- **The Pratfall Mod Framework** — [MOD_AUTHORS_GUIDE_FRAMEWORK.md](MOD_AUTHORS_GUIDE_FRAMEWORK.md) — adds a safety gate, IL scanner, multiplayer sync, and helpers that wrap the patterns in this guide.
