# Pratfall Mod Author Guide — Framework

This guide is for writing mods that target **the Pratfall Mod Framework** — this repo, which sits on top of Pratfall's official loader and adds a user-check gate, IL safety scanner, multiplayer sync, conflict resolution, and per-helper APIs that wrap the common patterns.

If you want a mod that works against just Pratfall + Tim's loader with no third-party dependency, see [MOD_AUTHORS_GUIDE_VANILLA.md](MOD_AUTHORS_GUIDE_VANILLA.md). The two paths are interoperable — a framework-targeted mod won't run on a vanilla install, but a vanilla-targeted mod runs fine on a framework install.

## When to use the framework

| You want… | Vanilla is fine | Framework helps |
|---|---|---|
| A single-player Harmony patch | ✓ | ✓ (slightly less boilerplate via `[ModPatch]`) |
| To ship via Steam Workshop and worry about auto-updates pushing malicious bytes | | ✓ (fingerprint gate re-locks on byte change) |
| Multiplayer sync — peers vote on enabling, transfer your DLL P2P | | ✓ |
| Per-mod inspector + IL safety scanner UX in the Mods dialog | | ✓ |
| Conflict resolution between mods that declare each other incompatible | | ✓ |
| `IDisposable` helpers that handle cleanup, filesystem URI conversion, and subscription accounting | | ✓ |
| To not care that another mod with the same folder name might overwrite your assets | | ✓ (compatibility checker warns) |

## Contents

1. [Setup — csproj, manifest, folder layout](#setup)
2. [Lifecycle — `OnLoad` / `OnUnload`](#lifecycle)
3. [The user-check gate (behavior you should understand)](#the-user-check-gate)
4. [Recipe: Harmony patches via `[ModPatch]`](#recipe-modpatch)
5. [Recipe: Add a language (`ModLocalizationHelper`)](#recipe-localization)
6. [Recipe: Persist mod data (`ModSaveDataHelper`)](#recipe-savedata)
7. [Recipe: Listen to game events (`ModGameEventHelper`)](#recipe-gameevents)
8. [Recipe: Show HUD button hints (`ModButtonPromptHelper`)](#recipe-buttonprompts)
9. [Recipe: Extend a random drop pool (`ModDropPoolHelper`)](#recipe-droppools)
10. [Multiplayer manifest fields](#multiplayer-fields)
11. [Pitfalls + framework-specific things to know](#pitfalls)
12. [Resources](#resources)

---

## Setup

Minimal mod project shape:

```
MyMod/
├── MyMod.csproj
├── manifest.json
└── MyMod.cs
```

**`MyMod.csproj`** — same as vanilla, plus a reference to `PratfallModFramework.dll`:

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

**`manifest.json`** — the framework parses both camelCase and PascalCase keys, so this file works with Pratfall's loader too. Multiplayer-relevant fields are framework-specific:

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
  "assemblySha256": "",
  "pckFile": "",
  "effects": {
    "settings": [],
    "patches": [],
    "nodes": [],
    "assets": [],
    "needsRestart": false
  },
  "multiplayer": {
    "mode": "local_only",
    "requires": [],
    "conflictsWith": []
  }
}
```

Framework-specific manifest fields:
- `assemblySha256` (optional) — SHA-256 of your DLL bytes in lowercase hex. When set, the framework refuses to load a DLL whose actual hash differs. Pin this to defend against tampered or stale files. Leave empty to opt out (back-compat).
- `pckFile` (optional) — name of a `.pck` side-file. The framework transfers both DLL and PCK during P2P sync and mounts the PCK on enable.
- `multiplayer.mode` — `local_only`, `stretch`, `transfer`, `restart_required`, or `auto` (default — framework infers from `type` + effects).
- `multiplayer.requires` — list of other mod IDs that must be installed on peers.
- `multiplayer.conflictsWith` — list of mod IDs your mod is incompatible with. Surfaces in the conflict-resolution prompt.

Fields the framework shares with Pratfall's loader (same default behavior):
- `AutoLoad` — auto-enable on launch. Both loaders honor it.
- `AddAssemblyToGodot` — register types with Godot's script bridge. Both loaders default `true`.

The mod folder name must be **unique** across all installed mods — both loaders mount each PCK at `res://<DirectoryName>/...`, and two mods sharing a folder name silently overwrite each other's assets. The framework's compatibility checker flags duplicate folder names with a ⚠ badge.

## Lifecycle

The framework reflects for two static methods on any type in your assembly (the class doesn't have to be named `ModEntry`):

```csharp
public static class MyMod
{
    public static void OnLoad()
    {
        // Mod was enabled. Subscribe to events, register helpers, mount resources.
        // Pair every Register / Subscribe / += with the corresponding Dispose / -= in OnUnload.
    }

    public static void OnUnload()
    {
        // Mod was disabled. Tear down everything OnLoad set up.
    }
}
```

Differences from Pratfall's `ModEntry.ModInit`/`ModDestroy`:
- Class name is flexible — anything containing static methods with the right names works.
- The framework's loader tolerates both naming conventions, so a mod with a `ModEntry` class + `ModInit`/`ModDestroy` will also work on a framework install via the official-loader bubble.

Mods can be enabled → disabled → enabled multiple times per session. Make both methods idempotent.

## The user-check gate

**The framework refuses to load a DLL whose current bytes haven't been user-approved.** This is the v1.1 security model — it prevents auto-installed mods (Workshop auto-update, fresh download) from silently executing code before the user has had a chance to inspect them.

How a mod gets approved:
1. The user toggles the mod ON in the Mods dialog.
2. The user clicks 🔍 (the IL safety scanner) on the mod's card.
3. The user accepts Download in a multiplayer acquisition prompt.

Each path computes the mod's fingerprint (`SHA256("dll:" + DllSha256 + "|pck:" + PckSha256)`) and adds it to the approved set in `<userData>/modframework-state.json`.

**Implication for mod authors:** updating your mod's DLL or PCK changes the fingerprint, which re-locks the gate. Users on the framework will see your toggle go OFF after an update and need to re-approve. This is by design — it's the only defense against a compromised distribution channel pushing malicious bytes. Document this in your update notes so users aren't surprised.

The gate doesn't apply to:
- Manifest-only mods (no DLL — nothing to gate).
- Official-loader mods bridged through `OfficialModBridge`. The framework defers to Pratfall's enabled-state file for those.

## Recipe: ModPatch

Replace raw Harmony with the framework's attribute-based pattern. The framework's `ModAssemblyLoader` scans every type with `[ModPatch]` and applies the patch on enable, unpatches on disable. Your mod doesn't import HarmonyLib.

```csharp
using PratfallModFramework;
using Godot;

[ModPatch(typeof(PlayerPickaxeComponent), "TriggerPrimaryAction", PatchType.Postfix)]
public static class PickaxePatch
{
    static void Postfix(PlayerPickaxeComponent __instance) => GD.Print("swung!");
}
```

The static method name must be `Prefix`, `Postfix`, or `Transpiler` (matching the `PatchType` you declared). Argument names follow Harmony conventions (`__instance`, `__result`, `__originalMethod`, `___privateField`, etc.).

Multiple patches per mod: declare multiple `[ModPatch]` types in your assembly. The loader patches all of them on enable.

## Recipe: Localization

> **Heads up on the current Pratfall release (1.1.0.R2943):** `LoadUserLocalizations` is gated by `Game.Config.AllowUserLocalization`, which is **false** on the shipped public build. The helper writes the file correctly and calls the loader, but the game silently refuses to actually load user locales until the dev flips the flag. The helper detects this case and prints a clear error to the console; your file is still on disk and will load automatically the moment the flag goes true.

`ModLocalizationHelper.Register` wraps `LocalizationManager.LoadUserLocalizations` — writes your translations to `<userData>/localization/<modId>_<locale>.json` with the right naming convention (Pratfall's loader skips files starting with `_` so the helper does NOT prefix one), triggers the rescan, and returns an `IDisposable` that cleans up the file on dispose.

```csharp
using PratfallModFramework;

public static class MyMod
{
    private static IDisposable? _registration;

    public static void OnLoad()
    {
        _registration = ModLocalizationHelper.Register(
            modId: "MyMod",
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

If your mod ships pre-built JSON content (e.g. a `.json` next to the DLL), use `RegisterRaw(modId, localeCode, jsonContent)` to skip the dictionary serialization step.

The helper handles:
- Filesystem URI conversion (`Game.Platform.GetUserDataPath()` returns a `user://` URI — `System.IO` can't write to that without `ProjectSettings.GlobalizePath` first).
- The filename rule (ends with `.json`, does NOT start with `_` — Pratfall's `LoadJsonFiles` skips leading-underscore files).
- Mod-id-based filename uniqueness so two mods don't collide.
- Rescan via `LocalizationManager.Instance.LoadUserLocalizations()` after write and after delete.

**The registered locale ID is `"zuser" + filename-without-extension`** — Pratfall namespaces user locales away from system locales. So `Register(modId: "MyMod", localeCode: "es_419", ...)` produces locale ID `"zuserMyMod_es_419"`. Call `ModLocalizationHelper.ComputeRegisteredLocaleId(modId, localeCode)` to get the exact string if you need to call `TranslationServer.SetLocale(...)` or `LocalizationManager.IsLocaleAvailable(...)` from your mod. The in-game language selector displays user locales by their filename basename.

## Recipe: SaveData

`ModSaveDataHelper.Register` subscribes to `SavegameManager.OnGameWillSave` and persists mod-provided JSON to a per-mod file at `<userData>/modframework-saves/<modId>.json`. `LoadIfPresent` restores prior state at startup.

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

    public class MyState { public int Counter; }
}
```

The helper handles:
- Filesystem URI conversion.
- Directory creation on first save.
- Exception isolation — if your serializer throws, the helper logs the error and the rest of the save flow continues. Other mods' serializers still run.
- Subscription accounting — Dispose unsubscribes cleanly so an enable → disable → enable cycle doesn't accumulate hooks.

Use `ModSaveDataHelper.Delete(modId)` to reset state explicitly (e.g. a "Reset Mod Data" menu in your mod). Use `ModSaveDataHelper.GetModSaveFilePath(modId)` if you need to read/write the file directly (for migrations, e.g.).

## Recipe: GameEvents

`ModGameEventHelper.SubscribeToTag` and `SubscribeAll` wrap `GameEventBus.OnGameEventReceived` with built-in tag filtering and exception isolation.

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

`SubscribeAll(handler)` fires for every event regardless of tag — use for logging, analytics, replay capture. `SubscribeToTag(tagString, handler)` only fires when the event's tag string matches (ordinal comparison, case-sensitive).

The helper handles:
- Tag-string comparison so your handler doesn't need to do it.
- Exception isolation — your handler's throw is logged with mod context; other subscribers still run.
- Dispose unsubscribes cleanly.

The underlying bus doesn't filter — every subscriber sees every published event. `SubscribeToTag` does the filter in the wrapper. The cost is one string compare per event per subscriber; negligible.

## Recipe: ButtonPrompts

`ModButtonPromptHelper.Show` wraps `ButtonPrompBarController.AddButtonPrompt` and tolerates null `Instance` (the HUD bar doesn't exist on the main menu).

```csharp
using PratfallModFramework;

public static class MyMod
{
    private const string Context = "MyMod_Inventory";

    public static void OnLoad() => ModButtonPromptHelper.Show("ui_accept", "Equip", Context);
    public static void OnUnload() => ModButtonPromptHelper.ClearContext(Context);
}
```

The helper handles:
- Null `Instance` on screens where the HUD isn't loaded — logs an error and returns instead of throwing.
- `ButtonPromptOptions` construction.
- Exception isolation around the underlying `AddButtonPrompt` call.

`ClearContext(context)` mirrors `ButtonPrompBarController.ClearButtonPrompts(context)` — there's no per-prompt removal API on the game side, only per-context. Pick a unique-per-mod context string so cleanup doesn't affect other mods' prompts.

## Recipe: DropPools

`ModDropPoolHelper.Register` wraps the array-mutation pattern Robert recommended for `RandomWeightedDropPool` content mods. Returns an `IDisposable` that removes the specific entry you added (by reference, not by content — two mods can legitimately add the same scene at the same weight).

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

For tests or in-memory pools, use `ModDropPoolHelper.RegisterIn(pool, scene, weight, ...)` to skip the resource-load step.

The helper handles:
- Allocating a grown array, copying old entries, appending yours.
- Removing exactly your entry on dispose (by reference identity).
- Edge cases — empty pool, pool with the entry already removed by something else, etc.

## Multiplayer fields

If you want your mod to sync in multiplayer lobbies, the `multiplayer` block in your manifest controls how:

```json
"multiplayer": {
  "mode": "transfer",
  "requires": ["SomeBaseMod"],
  "conflictsWith": ["RivalMod"]
}
```

`mode` (string, default `auto`):
- `local_only` — single-player-only. Other peers don't need to have it. Won't trigger votes.
- `stretch` — settings-only. The framework can apply the settings on other peers without transferring the DLL.
- `transfer` — peers need the actual DLL. When a vote passes, peers without the mod see a Download / Stretch / Decline prompt.
- `restart_required` — same as transfer, but warns the user the mod may need a game restart to fully apply (e.g. mods that mount PCKs).
- `auto` (default) — framework infers from `type` and `effects`:
  - `effects.needsRestart` or `effects.nodes` present → `restart_required`
  - `effects.patches` or `effects.assets` present → `transfer`
  - `effects.settings` or `fixedSeedString` present → `stretch`
  - Otherwise → `local_only`

`requires` — list of mod IDs that must be installed on all peers. The framework refuses to start a vote for your mod if any peer is missing a required dependency.

`conflictsWith` — list of mod IDs your mod is incompatible with. If two locally-enabled mods declare each other in `conflictsWith`, the conflict-resolution prompt fires asking the user which one to keep.

## Pitfalls

- **Updating your mod re-locks the gate.** Any byte change in your DLL or PCK invalidates the user's prior approval. Users will see your toggle OFF after an update and need to re-approve via 🔍 Scan or toggle ON. Document this in update notes.
- **Folder names must be unique across mods.** Both loaders mount each PCK at `res://<DirectoryName>/...`. Two mods sharing a folder name silently overwrite each other's assets. The framework's compatibility checker flags this with a ⚠ badge but can't prevent the collision.
- **Don't mutate save-coupled arrays.** `PlayerColorsConfig.Colors`, `GameModeManager.Modes`, `LevelManager.LevelPrefabs`, `ProceduralCaveComponent.BiomeGenerationConfigs` are all referenced by index in save-game data. Adding entries breaks existing saves. The framework doesn't ship helpers for these for that reason.
- **OnLoad reentrance.** The framework's enable / disable / re-enable can happen multiple times per session (toggle in the dialog, vote result, conflict resolution). Make both `OnLoad` and `OnUnload` idempotent — every subscription paired with an unsubscribe, every `Register` paired with a `Dispose`.
- **`AssemblyLoadContext.Unload()` is called on disable.** Don't hold long-lived references to game types in static fields outside the mod's entry types — the GC needs to collect your assembly's load context.
- **HUD-attached singletons are null on the main menu.** `ButtonPrompBarController.Instance` and similar HUD pieces are only present during gameplay. The framework helpers null-check for you; if you bypass the helpers, null-check yourself.
- **The framework runs alongside Tim's loader, not instead of it.** Your mod loaded via the framework appears in the framework's Mods dialog. If you ALSO ship your mod through Pratfall's `enabled_mods.json` for vanilla users, the framework's `OfficialModBridge` defers — you won't see two copies, but you'll see one Mods dialog row that says "(via official loader)".
- **`assemblySha256` pinning is opt-in.** If you set it and ship a build whose hash doesn't match, the framework refuses to load with a clear error. Useful for paranoid distribution; skip if your build pipeline doesn't produce deterministic SHAs.
- **`ByteBufferWriter` has a 32 KB string cap.** The framework's P2P transfer chunks at 14 KB raw to stay under, but if you publish custom events via `NetworkEventManager.SendEvent` directly, you're on your own for size.

## Resources

- **Sample mod** — [`sample-mods/HelloWorldMod/`](sample-mods/HelloWorldMod) — minimal Harmony-patch mod using `[ModPatch]`.
- **Framework helpers source** — [`framework/PratfallModFramework/Mod*Helper.cs`](framework/PratfallModFramework/) — read these to see exactly what each wrapper does. None use reflection or IL hacks; everything is public Pratfall API.
- **Vanilla path** — [MOD_AUTHORS_GUIDE_VANILLA.md](MOD_AUTHORS_GUIDE_VANILLA.md) — for mods that should work without the framework installed. Also has the full decoded Pratfall surface inventory (singletons, events, configs).
- **Tim's example mod** — [`quad-head/pratfall-example-mod`](https://github.com/quad-head/pratfall-example-mod) — the canonical official-loader reference (uses the `ModEntry` + `ModInit` / `ModDestroy` convention).
- **Discord** — `#mod-dev` channel of the Pratfall dev server (Tim, Robert, and active modders coordinate there).
