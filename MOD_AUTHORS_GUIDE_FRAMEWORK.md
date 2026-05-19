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
10. [Recipe: Settings (`ModConfig`)](#recipe-settings-modconfig)
11. [Recipe: Per-mod logs + crash reports (`ModLogger` / `ModCrashReporter`)](#recipe-logging--crash-reports)
12. [Multiplayer manifest fields](#multiplayer-fields)
13. [Pitfalls + framework-specific things to know](#pitfalls)
14. [Resources](#resources)

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

Fields the framework reads but treats differently from Pratfall's loader:
- `AutoLoad` — Pratfall's native loader auto-enables on launch when true. **The framework deliberately ignores this field** — every mod starts disabled in the Mods dialog and requires explicit user enable + 🔍 approval. See [The user-check gate](#the-user-check-gate) for why.
- `AddAssemblyToGodot` — register types with Godot's script bridge. Both loaders default `true`. (Set to `false` only if you have a specific reason to skip script registration.)

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

**No auto-load** — even mods whose manifest declares `"AutoLoad": true` (Pratfall's native schema) are NOT auto-enabled by the framework. The user-check gate is the framework's only defense against unreviewed code execution, and auto-load would bypass it. Every mod stays toggled OFF in the Mods dialog until the user explicitly enables it AND clicks 🔍 to approve the fingerprint. If you author a mod that expects to "just work" on first launch, document that users need to enable it once after install.

### Workshop mods (live discovery)

The framework discovers Steam Workshop mods two ways:

1. **At startup** — `ManifestManager.ScanWorkshopMods` walks every Steam library folder (registry + `libraryfolders.vdf`) and scans `steamapps\workshop\content\4244510\<workshopid>\` for manifests. Subscriptions present at launch appear in the Mods dialog immediately.

2. **Live, mid-session** — `WorkshopSubscriber` hooks `Steamworks.SteamUGC.OnItemInstalled` (via a Harmony postfix on `Steam.SetupWorkshopCallbacks` so it runs after Steam is fully initialized). When the user subscribes to a Workshop mod from inside Pratfall (or accepts an update download), Steam fires the event → framework re-scans → the new mod appears in the Mods dialog **without a restart**. The user still has to click 🔍 to approve it before it loads — live discovery does not auto-trust.

Workshop mods are tagged with `manifest.IsSteamWorkshopMod = true` and `manifest.WorkshopId = <published-file-id>` so the inspector + mod cards can distinguish them from local mods. They get the full framework feature set (per-mod logger, config, crash reports, settings UI, network sync) identically to local mods.

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

> **Heads up on the current Pratfall release (verified `1.1.0.R2973`, 2026-05-18):** `LoadUserLocalizations` is gated by `Game.Config.AllowUserLocalization`, which is **false** on the shipped public build — Cecil-verified that `GameEntry._Ready` explicitly calls `set_AllowUserLocalization(false)` at startup (it's not just a forgotten default). The helper writes the file correctly and calls the loader, but the game silently refuses to actually load user locales until the dev flips the flag.
>
> **Opt-in unlock**: if you ship a locale-pack mod and want to bypass the gate today, call `ModLocalizationHelper.ForceEnableUserLocales()` once from your `OnLoad`. The framework Harmony-patches the getter to always return true, so subsequent `LoadUserLocalizations` calls actually load your file. Idempotent + process-lifetime. **Caveat**: Tim hasn't committed to the user-locale UX yet (file format, locale-name collision rules, mod-override priority) — by opting in you accept that behavior may shift when he flips the flag officially. The framework prints a clear log line when active so anyone debugging knows the patch is in play.

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

`ModGameEventHelper.Subscribe(GameplayTag, handler)`, `SubscribeToTag(string, handler)`, and `SubscribeAll(handler)` wrap `GameEventBus.OnGameEventReceived` with built-in tag filtering and exception isolation.

**Use the `Subscribe(GameplayTag, ...)` overload with Pratfall's pre-defined `GameplayTags.X` constants** — typos become compile errors instead of never-fires-handler:

```csharp
using PratfallModFramework;
using Godot;

public static class MyMod
{
    private static IDisposable? _sub;

    public static void OnLoad()
    {
        _sub = ModGameEventHelper.Subscribe(GameplayTags.Stats_Gameplay_Player_Death,
            (tag, ev) => GD.Print($"a player died: {ev}"));
    }

    public static void OnUnload() => _sub?.Dispose();
}
```

`SubscribeAll(handler)` fires for every event regardless of tag — use for logging, analytics, replay capture. `SubscribeToTag(tagString, handler)` is the string-keyed legacy overload — prefer the `Subscribe(GameplayTag, ...)` form unless you need to dynamically construct a tag string at runtime.

Pratfall ships ~40 named `GameplayTag` resources in `GameplayTags.*` — `Stats_Gameplay_Player_Death`, `Stats_Gameplay_Heal`, `Stats_Gameplay_Threw_Flare`, `Material_Wood`, `Challenge_Win`, `Demo_Win`, `Game_Restart`, etc. See the [vanilla guide's inventory](MOD_AUTHORS_GUIDE_VANILLA.md#tag-taxonomies-two-separate-systems--easy-to-confuse) for the full list. Don't confuse `GameplayTags.X` (high-level `GameEventBus` tags) with `Constants.EventId*` strings (low-level `NetworkEventManager` event IDs) — they're parallel systems.

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

`ModDropPoolHelper.Register(poolResPath, scene, weight, ...)` wraps the array-mutation pattern Robert recommended for `RandomWeightedDropPool` content mods. Returns an `IDisposable` that removes the specific entry you added (by reference, not by content — two mods can legitimately add the same scene at the same weight).

```csharp
using Godot;
using PratfallModFramework;

public static class MyMod
{
    private static IDisposable? _registration;

    public static void OnLoad()
    {
        // poolResPath is a res:// path to a .tres pool resource you've already
        // discovered (Pratfall doesn't expose pool .tres paths in code — they
        // live in scene files). For pool resources you haven't pre-identified,
        // see ModDropPoolHelper.RegisterIn below for the discovery alternative.
        _registration = ModDropPoolHelper.Register(
            poolResPath: "res://path/to/FoodDropPool.tres",
            scene: GD.Load<PackedScene>("res://my_mod/MyFood.tscn"),
            weight: 5);
    }

    public static void OnUnload() => _registration?.Dispose();
}
```

**For pools you can find at runtime via `DebugMappingManager.Instance.DropPools`** (the array Pratfall populates from the active scene), use `RegisterIn`:

```csharp
var pools = DebugMappingManager.Instance?.DropPools;
var pool = pools?.FirstOrDefault(p => p?.ResourceName == "FoodDropPool");
if (pool != null)
    _registration = ModDropPoolHelper.RegisterIn(pool, scene, weight: 5);
```

`RegisterIn` is also what the framework's own self-test uses against an in-memory pool — skips the `ResourceLoader.Load` step entirely.

The helper handles:
- Allocating a grown array, copying old entries, appending yours.
- Removing exactly your entry on dispose (by reference identity).
- Edge cases — empty pool, pool with the entry already removed by something else, etc.

## Recipe: Settings (ModConfig)

`ModConfig.For(modId)` returns a per-mod `ModConfigFile`. Call `Bind<T>(section, key, defaultValue, description?)` for each setting your mod exposes. The bound `ConfigEntry<T>` persists to `<userData>/modframework-config/<modid>.json`, fires `OnChange` events on mutation, and enforces optional `AcceptableValueRange<T>` / `AcceptableValueList<T>` constraints. Authors moving between framework ecosystems will recognize the API — it's modeled after BepInEx's `ConfigFile`.

```csharp
using PratfallModFramework;

public static class MyMod
{
    private static ConfigEntry<int> _maxFlares = null!;
    private static ConfigEntry<bool> _enabled = null!;

    public static void OnLoad()
    {
        var cfg = ModConfig.For("MyMod");

        _enabled = cfg.Bind("General", "Enabled", true, new ConfigDescription
        {
            Tooltip = "Master on/off switch for the mod"
        });

        _maxFlares = cfg.Bind("Combat", "MaxFlares", 3, new ConfigDescription
        {
            Tooltip = "How many flares the player can carry",
            Constraint = new AcceptableValueRange<int>(1, 100)
        });

        // React to changes (player edits the JSON file or, in a future release, the
        // in-game settings tab — both go through the same setter).
        _maxFlares.OnChange += newMax =>
            GD.Print($"[MyMod] MaxFlares changed to {newMax}");

        // Read the current value any time:
        if (_enabled.Value)
            ApplyMaxFlares(_maxFlares.Value);
    }

    public static void OnUnload() { /* no per-entry teardown needed */ }
}
```

### What's persistent vs in-memory

- **Persistent**: The current `Value` of every bound entry, written to disk immediately on every setter.
- **In-memory**: The `OnChange` handlers (re-subscribed in `OnLoad`), the `ConfigEntry<T>` references (rebound in `OnLoad` — same instance returned each time for the same `(section, key)`).

### Type support

`bool`, `int`, `long`, `float`, `double`, `string`, and any `enum`. Enums serialize as their string name (so `"Wood"` not `0`). Arrays / dictionaries / custom types are NOT supported in v1 — for that, use `ModSaveDataHelper`.

### Constraints

| Constraint | Used for | Behavior |
|---|---|---|
| `new AcceptableValueRange<int>(1, 100)` | Bounded numeric ranges | `.Value = 9999` throws `ArgumentOutOfRangeException` |
| `new AcceptableValueList<string>("low", "medium", "high")` | Discrete allowed values | Any value not in the list throws |
| _(no constraint)_ | Free-form values | No validation |

Constraints are validated on every `Value` setter. If the value passes, it's persisted and `OnChange` fires.

### File format

```json
{
  "_schema_version": 1,
  "General": {
    "Enabled": true
  },
  "Combat": {
    "MaxFlares": 50
  }
}
```

Users can hand-edit this file. If the JSON is malformed, the framework backs up the file as `<modid>.json.bad`, falls back to defaults, and logs the failure. If a key has the wrong type, the framework logs + uses the default for that one key.

### Reload from disk

If your mod has a "reload settings" button or you want to react to external edits:

```csharp
ModConfig.For("MyMod").Reload();
// Every ConfigEntry with a changed file value fires OnChange.
```

### Discovery (for the future in-game settings editor)

`ModConfig.GetAllEntries(modId)` returns the list of `IConfigEntry` instances bound for a mod. The framework reserves the right to use this for an in-game settings UI in a future release; mod authors generally won't need to call it.

### In-game settings editor

If a mod has called `ModConfig.Bind(...)` at least once, a **⚙ Settings** button appears on that mod's card in the Mods dialog (next to ℹ Info and 🔍 Scan). Clicking it opens a settings panel that auto-renders each bound entry with the right widget:

| Type | Constraint | Widget |
|---|---|---|
| `bool` | (none) | toggle switch (our `ToggleSwitch`) |
| `int` / `long` | `AcceptableValueRange<T>` | slider with current-value label |
| `int` / `long` | _(none)_ | spin box |
| `float` / `double` | `AcceptableValueRange<T>` | slider with 2-decimal current-value label |
| `float` / `double` | _(none)_ | spin box |
| `string` | `AcceptableValueList<T>` | dropdown |
| `string` | _(none)_ | text input |
| enum | _(implicit list from enum values)_ | dropdown |

Per-row "↺" button calls `entry.ResetToDefault()` and refreshes the widget. Entries are grouped by `Section`; sections with no name fall under "General". Tooltips from `ConfigDescription.Tooltip` show on the entry label hover. Mods that haven't bound anything don't get a ⚙ button — zero clutter for the rest of the list.

Bidirectional binding caveat: widget edits flow into the `ConfigEntry.Value` setter (which fires `OnChange`). Programmatic `Value` mutations from elsewhere while the panel is open are NOT reflected live — reopen the panel to see them. Rare enough in practice that the v1 trade-off is acceptable.

### Multiplayer sync (CSync)

Setting `Synced = true` on a `ConfigDescription` opts the entry into host-pushes-to-clients sync. The host's value wins; peers receive a snapshot when they join, and live updates when the host changes any synced value. Local edits on peers still apply locally (so their UI feels responsive), but the next snapshot from the host overwrites them.

```csharp
cfg.Bind("Gameplay", "MaxFlares", 3, new ConfigDescription
{
    Tooltip = "How many flares each player can carry.",
    Constraint = new AcceptableValueRange<int>(1, 10),
    Synced = true  // host's value wins for everyone in the lobby
});
```

Mechanics:

- The framework subscribes to all `Synced` entries automatically — your mod doesn't need to wire anything.
- On a peer joining the lobby (after manifest exchange completes), the host sends one `ModConfigSyncSnapshot` containing every Synced entry across every loaded mod.
- When the host's value changes (via `entry.Value = ...` or the in-game ⚙ panel), a snapshot with just that one entry broadcasts to all peers.
- Peers apply via `SetFromHost(value)`, which: applies the value, fires `OnChange`, but **does NOT** persist to the peer's config file and **does NOT** re-broadcast (so there's no cycle).
- Non-Synced entries are never sent — peers keep their own values for those.
- Values are serialized as `(TypeName, StringValue)` discriminator pairs. Supported types: `bool`, `int`, `long`, `float`, `double`, `string`, and any `enum`. Custom types are not supported — they stay local-only.
- Snapshots are capped at 32700 bytes (Pratfall's `ByteBufferWriter` limit); a typical mod has a handful of synced entries and won't approach this.

### What this does NOT do (yet)

- **Custom value types don't sync.** Only primitives, strings, and enums (listed above) round-trip through CSync. A `Synced = true` on, say, a `Vector3` entry won't be transmitted.
- **No conflict resolution UI.** If the host's snapshot rejects on a peer (constraint violation, type mismatch from a version skew), the peer logs and continues — there's no in-game notification.

## Recipe: Logging + crash reports

`ModLogger.For(modId)` returns a per-mod `IModLogger` that writes to `<userData>/modframework-logs/<modid>.log` AND tees to Godot's console (`GD.Print` / `GD.PrintErr` with a `[modid]` prefix). Replace `GD.Print` calls in your mod — you get structured per-mod logs without breaking the line-in-godot.log convention. The framework also keeps a ring buffer of your last ~200 entries in memory; when your mod throws, those entries are baked into the crash report so you can see what your mod was doing right before the failure.

```csharp
using PratfallModFramework;

public static class MyMod
{
    // Hold a single instance for the mod's lifetime. ModLogger.For returns the same
    // instance on repeat calls, so caching it is purely a readability choice.
    private static readonly IModLogger Log = ModLogger.For("MyMod");

    public static void OnLoad()
    {
        Log.Info("loading");
        try
        {
            DoTheRiskyThing();
            Log.Info("loaded");
        }
        catch (Exception ex)
        {
            // The framework already drops a crash report automatically when an
            // exception propagates out of OnLoad / OnUnload / EnableMod / patch
            // loading. Calling Log.Error here is optional — it'll show in
            // godot.log + your per-mod log file, but the crash report fires either way.
            Log.Error("DoTheRiskyThing failed", ex);
            throw; // re-throw so the framework's crash reporter writes the report
        }
    }

    public static void OnUnload() => Log.Info("unloaded");
}
```

### What automatic crash reports give you

When your `OnLoad` / `OnUnload` / `EnableMod` path / declared patch throws, the framework writes a file to `<userData>/modframework-crash-reports/<modid>_<utc-timestamp>.txt` containing:

- **Manifest snapshot** — id, name, version, author, type, multiplayer mode.
- **Exception chain** — type, message, full stack trace, walking `InnerException` to a depth of 8.
- **Recent log lines** — the last ~200 entries from your `ModLogger`'s in-memory ring buffer, so you can see what your mod logged right up to the failure.

The report path is logged to Godot's console (`[ModFramework] Crash report written: <path>`) so you can find it without remembering the folder. Users can send you the file when they hit a bug.

### Levels + tee behavior

| Method | Log file | Godot console |
|---|---|---|
| `Log.Debug(msg)` | `[DEBUG] msg` line | `GD.Print` (default Godot color) |
| `Log.Info(msg)` | `[INFO ] msg` line | `GD.Print` |
| `Log.Warn(msg)` | `[WARN ] msg` line | `GD.Print` |
| `Log.Error(msg)` | `[ERROR] msg` line | **`GD.PrintErr`** (red in console) |
| `Log.Error(msg, ex)` | `[ERROR] msg | ExceptionType: ExceptionMessage` line | `GD.PrintErr` |

Files are written UTF-8, append-mode. The framework does NOT rotate them automatically — they grow until you delete them. (For an actively-developed mod, you might want to wipe `<userData>/modframework-logs/<modid>.log` between debugging sessions.)

### What you don't need to write yourself

The framework already drops crash reports automatically for the four most common mod-side failure modes:

1. **`OnLoad` throws** — wired in `ModAssemblyLoader.InvokeLoadCallbacks`. Reflection's `TargetInvocationException` wrapper is unwrapped to the real exception.
2. **`OnUnload` throws** — wired in `ModAssemblyLoader.UnloadMod`.
3. **`EnableMod` fails** (any reason: hash mismatch, load failure, PCK mount failure) — wired in `ModManager.EnableMod`.
4. **Apply loop fails** (during `Load Enabled Mods` / pre-session apply) — wired in `ModManager.LoadAllEnabledMods`.

For your own try/catch sites inside the mod, call `ModCrashReporter.Report(modId, context, exception)` directly to drop a report with the same format.

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
- **The framework turns Pratfall's native ModManager off** and becomes the sole mod loader. (As of the 2026-05-18 Pratfall update — Tim explicitly invited custom loaders.) The framework Harmony-patches `ModManager.LoadAllModManifests` to skip the native discovery + auto-load pipeline; it does its own scan of `user://mods/`, `<game>/mods/`, Steam Workshop content folders, AND (when present) the `--qh-mod-directory` path passed by profile-based mod managers (Thunderstore / r2modman). Practical implications: (1) `AutoLoad: true` in your manifest is ignored — see Setup section; (2) your mod ID is the only key that matters — no separate "official" vs "framework" lanes; (3) the native ModButton + Mod UI in the main menu are hidden in favor of the framework's Mods dialog.
- **r2modman / Thunderstore compat:** if Pratfall is launched with `--qh-mod-directory <path>` (the launch-arg profile-based managers use), the framework treats that path as both an additional mod-scan root AND as the location for its own `modframework-state.json` — so each profile has independent enabled-mod + approved-fingerprint state. Also: any `.zip` files dropped into a scan root are auto-extracted on framework init (`ModZipDropInstaller`, zip-slip-safe via `ZipFile.ExtractToDirectory`). Mods you ship to Thunderstore that follow Pratfall's manifest schema work today; Thunderstore's snake_case manifest schema (`version_number`, `dependencies` as `team-package-version` strings) is not yet parsed by the framework — backlog item if listings actually appear.
- **Speedrun leaderboard submissions are blocked while your mod is enabled.** Pratfall's `SpeedrunManager.SubmitTimeToLeaderboard` refuses to submit when `ModManager.EnabledModCount != 0` (anti-cheat). The framework bridges its own enabled-mod count into that getter — so when any framework-loaded mod is enabled, completed runs in that session won't reach the public leaderboard. This is Pratfall's design, not the framework's. If you ship a cosmetic / UI mod where leaderboard-eligibility matters to players, mention in your description that "playing with this mod enabled disqualifies leaderboard submissions for the session."
- **`assemblySha256` pinning is opt-in.** If you set it and ship a build whose hash doesn't match, the framework refuses to load with a clear error. Useful for paranoid distribution; skip if your build pipeline doesn't produce deterministic SHAs.
- **`ByteBufferWriter` has a 32 KB string cap.** The framework's P2P transfer chunks at 14 KB raw to stay under, but if you publish custom events via `NetworkEventManager.SendEvent` directly, you're on your own for size.

## Resources

- **Sample mod** — [`sample-mods/HelloWorldMod/`](sample-mods/HelloWorldMod) — minimal Harmony-patch mod using `[ModPatch]`.
- **Framework helpers source** — [`framework/PratfallModFramework/Mod*Helper.cs`](framework/PratfallModFramework/) — read these to see exactly what each wrapper does. None use reflection or IL hacks; everything is public Pratfall API.
- **Vanilla path** — [MOD_AUTHORS_GUIDE_VANILLA.md](MOD_AUTHORS_GUIDE_VANILLA.md) — for mods that should work without the framework installed. Also has the full decoded Pratfall surface inventory (singletons, events, configs).
- **Tim's example mod** — [`quad-head/pratfall-example-mod`](https://github.com/quad-head/pratfall-example-mod) — the canonical official-loader reference (uses the `ModEntry` + `ModInit` / `ModDestroy` convention).
- **Discord** — `#mod-dev` channel of the Pratfall dev server (Tim, Robert, and active modders coordinate there).
