# Pratfall Mod Author Guide — Vanilla

This guide is for writing mods that target **just Pratfall and its official mod loader** (Tim's `ModManager`, shipped with the game in `1.1.0.R2943` and later; updated `1.1.0.R2973` on 2026-05-18 with Steam Workshop support, a "very simple mod loader (main menu)" UI, and assorted multiplayer bug fixes — see Tim's [Workshop & Bugfixes patch notes](https://store.steampowered.com/news/app/4244510/view/663861845817296708)). No third-party framework required.

If you want the safety gate / IL scanner / multiplayer-vote / per-mod helpers added by the Pratfall Mod Framework, see [MOD_AUTHORS_GUIDE_FRAMEWORK.md](MOD_AUTHORS_GUIDE_FRAMEWORK.md) instead. The two paths are interoperable — your mod can target the vanilla loader and still run on a player's machine that has the framework installed.

## Contents

1. [Setup — csproj, manifest, folder layout](#setup)
2. [Lifecycle — `ModEntry.ModInit` / `ModDestroy`](#lifecycle)
3. [CLI flags Pratfall accepts](#cli-flags)
4. [Godot 4 concepts mod authors should know](#godot-4-concepts)
    - [4.1 Node lifecycle](#node-lifecycle)
    - [4.2 Godot ref lifetime — don't trust C# null checks](#godot-ref-lifetime--dont-trust-c-null-checks)
    - [4.3 `PackedScene.Instantiate()` returns a detached node](#packedsceneinstantiate-returns-a-detached-node)
    - [4.4 `Resource` is shared by reference](#resource-is-shared-by-reference)
    - [4.5 `user://` vs `res://`](#user-vs-res)
    - [4.6 C# hot-reload doesn't work for mods](#c-hot-reload-doesnt-work-for-mods)
    - [4.7 `GD.Print` vs `Console.WriteLine`](#gdprint-vs-consolewriteline)
5. [Recipe: Harmony patches](#recipe-harmony-patches)
6. [Recipe: Add a language to the in-game selector](#recipe-add-a-language)
7. [Recipe: Persist mod data alongside the save](#recipe-persist-mod-data)
8. [Recipe: Listen to game events](#recipe-listen-to-game-events)
9. [Recipe: Show HUD button hints](#recipe-show-hud-button-hints)
10. [Recipe: Show a toast notification](#recipe-show-a-toast)
11. [Recipe: Play a sound](#recipe-play-a-sound)
12. [Recipe: Spawn an entity into the world](#recipe-spawn-an-entity)
13. [Recipe: React to a level loading](#recipe-react-to-level-load)
14. [Recipe: Multiplayer-aware patterns (host check, late-join)](#recipe-multiplayer-patterns)
15. [Recipe: Extend a random drop pool](#recipe-extend-a-drop-pool)
16. [Recipe: Custom Godot Node / Resource types](#recipe-custom-godot-types)
17. [Recipe: Gold, progression & compatibility-renderer (2026-06 update)](#recipe-gold-progression--compatibility-renderer-2026-06-update)
    - [17.1 Gold](#gold)
    - [17.2 Detecting compatibility-renderer (low-spec) mode](#detecting-compatibility-renderer-low-spec-mode)
    - [17.3 Progression / difficulty (read-only)](#progression--difficulty-read-only)
18. [Recipe: PCK assets — unpack, repack, and override game assets](#recipe-pck-assets--unpack-repack-and-override-game-assets)
    - [18.1 Unpacking `Pratfall.pck` to see what's inside](#unpacking-pratfallpck-to-see-whats-inside)
    - [18.2 Packing your mod's assets into a `.pck`](#packing-your-mods-assets-into-a-pck)
    - [18.3 Auto-instantiated root scene (`root.tscn`)](#auto-instantiated-root-scene-roottscn)
    - [18.4 Overriding Pratfall's own assets](#overriding-pratfalls-own-assets)
    - [18.5 PCK packaging gotchas](#pck-packaging-gotchas)
19. [Decoded Pratfall surface inventory](#decoded-pratfall-surface-inventory)
    - [19.1 "How do I ...?"](#how-do-i-)
    - [19.2 Singletons (78)](#singletons-78)
    - [19.3 Static helper classes (22)](#static-helper-classes-22)
    - [19.4 Configs & Settings (27)](#configs--settings-27)
    - [19.5 Events you can subscribe to (11)](#events-you-can-subscribe-to-11)
    - [19.6 `GameplayTags.*` (42)](#gameplaytags-42)
    - [19.7 `Constants.EventId*` (72)](#constantseventid-72)
    - [19.8 Entity hierarchy & `IEntity`](#entity-hierarchy--ientity)
    - [19.9 `IComponent` implementors (203)](#icomponent-implementors-203)
    - [19.10 Public interfaces (13)](#public-interfaces-13)
    - [19.11 `res://` path conventions](#res-path-conventions)
    - [19.12 Save-coupled arrays — don't mutate](#save-coupled-arrays--dont-mutate)
20. [Debugging & dev iteration](#debugging--dev-iteration)
    - [20.1 Where logs go](#where-logs-go)
    - [20.2 Useful Godot CLI flags](#useful-godot-cli-flags)
    - [20.3 Iteration loop](#iteration-loop)
    - [20.4 Attaching a debugger](#attaching-a-debugger)
    - [20.5 Bisecting a multi-mod conflict](#bisecting-a-multi-mod-conflict)
    - [20.6 Smoke test before sharing](#smoke-test-before-sharing)
21. [Distribution conventions](#distribution-conventions)
    - [21.1 Mod ID format](#mod-id-format)
    - [21.2 Folder contents](#folder-contents)
    - [21.3 Version format](#version-format)
    - [21.4 Multiplayer-compatibility tag in README](#multiplayer-compatibility-tag-in-readme)
    - [21.5 What NOT to include in your package](#what-not-to-include-in-your-package)
    - [21.6 Where to publish (as of 2026-05-18)](#where-to-publish-as-of-2026-05-18)
    - [21.7 Uploading to Steam Workshop](#uploading-to-steam-workshop)
    - [21.8 Steam Workshop preview image](#steam-workshop-preview-image)
22. [Pitfalls + things to know](#pitfalls)
23. [Resources](#resources)

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

**Alternate build path:** if you export via the Godot editor instead of `dotnet build`, the compiled DLL ends up under `<your Godot project>/.godot/mono/temp/bin/`; copy it into your mod folder manually. The csproj `InstallMod` target above is the equivalent for the dotnet-build flow.

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
  "AutoLoad": false
}
```

Key fields:
- `Assembly` — DLL filename in the mod folder. Pratfall's loader will resolve `<mod folder>/<Assembly>` and `LoadFromAssemblyPath` it.
- `PackageName` — optional `.pck` filename. If set, the loader mounts the package and tries to instantiate `res://<DirectoryName>/root.tscn` under `Game.RootNode`.
- `AutoLoad` (default `false`) — when `true`, the loader auto-enables the mod at launch even if previously disabled. Useful for mods that ship runtime infrastructure.

The mod folder name must be **unique** across all installed mods — it's the namespace for any assets in your `.pck` (Pratfall mounts them at `res://<DirectoryName>/...`).

### `enabled_mods.json`

Inside the official `<GameDir>\mods\` directory, the loader keeps an `enabled_mods.json` file — a JSON array of **full mod directory paths** (the absolute path the loader builds from `OS.GetExecutablePath()` + `/mods/<folder>`), NOT bare folder names and NOT display names:

```json
["D:\\SteamLibrary\\steamapps\\common\\Pratfall/mods/Author.SomeMod"]
```

Cecil-verified from `ModManager.IsModEnabled(manifest)` IL: each entry is compared (exact string match) against `manifest.Directory` — the mod's **full absolute path** — *not* `manifest.DirectoryName` (the bare folder) and *not* `Name` (display only). So a bare folder name like `Author.MyMod` will **not** match; the entry has to be the exact full path the loader computed (note the mixed separators it produces: `\` through the install root, then `/mods/<folder>`). The `Name` field never gates loading.

For manual testing, prefer Pratfall's in-game Mods button — it writes the correct full path on each toggle. Hand-editing works too, but you must use the exact absolute path or the mod won't be recognized. If the file is absent or empty (`[]`), no mods are enabled at launch (unless they have `AutoLoad: true` in their manifest).

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
- After `ModDestroy` is called, the loader calls `Unload()` on the AssemblyLoadContext your mod was loaded into, then forces a full GC cycle (`GC.Collect()` → `GC.WaitForPendingFinalizers()` → `GC.Collect()`). The catch: Pratfall loads your mod **into the same AssemblyLoadContext that hosts Godot's `ScriptManagerBridge`** (verified in `ModManager.LoadAssembly` IL — `AssemblyLoadContext.GetLoadContext(typeof(ScriptManagerBridge).Assembly)`). It does NOT create a per-mod ALC. Whether the runtime can actually free your mod's assembly depends on whether that shared ALC is collectible AND whether anything else still references your code. Exceptions during unload are swallowed via the `Log.BlockExceptionHandler` toggle, so a failed unload is silent.

**Unload is cooperative, not forced.** Disabling a mod runs your `ModDestroy` and asks the runtime to unload the `AssemblyLoadContext`, but the runtime CANNOT actually free the assembly while anything still references it. Things that will silently keep your mod alive in memory after disable:

- **Static event subscriptions you didn't unsubscribe** — `GameEventBus.OnGameEventReceived += handler` without a matching `-=` (also `SavegameManager.OnGameWillSave`, `Network.EventManager.OnNetworkEventReceived`, etc.)
- **Background threads / `Task.Run` that's still running** — pinned to your assembly via captured `this`
- **Harmony patches you didn't `UnpatchAll`** — patched MethodInfo objects retain references to your patch methods
- **Cached `Type` / `MethodInfo` / `Delegate` references** held by game-side dictionaries (e.g. type caches in `Newtonsoft.Json` / `System.Text.Json`)
- **Native libraries loaded with `LoadFromUnmanagedDll`** — never unloaded by the runtime
- **Godot nodes you `AddChild`ed but never `QueueFree`d** — the scene tree still owns them
- **`MainThreadDispatcher.Instance.Enqueue` callbacks queued for after-unload** — captured `this` keeps your context alive

If you see your mod's log messages still firing after you toggled it off, one of the above is the culprit. The mod's assembly will stay in memory until the entire game process exits.

### Two ways to run a node: scene (`root.tscn`) vs code (`new` + `AddChild`)

Most per-frame/visual mods come down to "get a `Node` into the tree so its `_Ready`/`_Process` run." There are two ways, and the choice decides whether you ship a `.pck` at all:

- **Scene** — put your node in `res://<DirectoryName>/root.tscn`; on enable the loader instantiates it under the game root for you (see [auto-instantiated root scene](#auto-instantiated-root-scene-roottscn)). No `ModEntry` needed — but you need a **Godot project + a `.pck`**, and the install-folder name must match the baked `res://` root (see [PCK packaging gotchas](#pck-packaging-gotchas)).
- **Code** — no `.pck`, no `res://`, no scene. In `ModEntry.ModInit`, `new` the node and `AddChild` it yourself:

```csharp
using Godot;

public partial class MyNode : Node            // partial + the Godot source generator,
{                                              // or _Process never gets called (see below)
    public override void _Process(double delta) { /* per-frame work */ }
}

public static class ModEntry                   // global namespace (see above)
{
    static MyNode _node;
    public static void ModInit()
    {
        if (Engine.GetMainLoop() is SceneTree tree)
            tree.Root.AddChild(_node = new MyNode());   // now _Ready/_Process run
    }
    public static void ModDestroy() => _node?.QueueFree();   // pair the AddChild
}
```

The `new` + `AddChild` is exactly what the scene route does for you automatically — which is why code-only feels "heavier", but it's a one-time ~3-line hook, not per-feature boilerplate. (Pratfall also exposes `Game.RootNode` as a shortcut for `tree.Root`.) Two things bite people:

- **Forget the `AddChild`** and your code runs with no error but nothing happens — the node exists in memory but isn't in the tree.
- **"Code-only" means *no scene/PCK*, not *no Godot tooling*.** The node class still has to be a real Godot C# script — `partial`, built with the Godot .NET source generator (it emits the `InvokeGodotClassMethod` bridge the engine calls) — or `_Ready`/`_Process` never fire. If your mod only Harmony-patches or subscribes to events, skip the node entirely and do the work straight in `ModInit`.

Both ship in the wild: Robert's Infinite Flare puts its `FlareModifier : Node` in `root.tscn` (scene); Rafi1017's Colored Flares `AddChild`s the same kind of node from `ModEntry.ModInit` (code). Both Cecil-verified.

### A third way to run code: `ILifecycleHandler` and `OnStart`

Both routes above give you plain Godot `_Ready` / `_Process`. If you want to plug into **Pratfall's own** lifecycle instead — most importantly an **`OnStart`** that runs once *after* Ready but *before* the first update (like Unity's `Start`), plus `_Process` / `_PhysicsProcess` ticks ordered through the game's `LifecycleManager` — make your node an **`ILifecycleHandler`**. Every game component (and most managers) is one. This recipe is compile-verified against the real game assemblies:

```csharp
using Godot;

// ILifecycleHandler / LifecycleHelper / LifecycleUpdateType are in the global namespace.
public partial class MyComponent : Node, ILifecycleHandler   // partial: Godot's source generator requires it on Node subclasses
{
    // Gate the per-frame hooks: None / Update / PhysicUpdate / All  (note the spelling: "Physic", no 's').
    public LifecycleUpdateType GetUpdateType() => LifecycleUpdateType.Update;

    // Required: Godot doesn't deliver _Notification to the script through interface
    // inheritance, so route it into the game's dispatcher yourself.
    public override void _Notification(int what) => LifecycleHelper.ProcessNotification(this, this, what);

    public void OnEnterTree() { }
    public void OnReady() { }
    public void OnStart() { }                   // once, after Ready, before the first OnUpdate
    public void OnUpdate(double delta) { }       // only if GetUpdateType() includes Update
    public void OnPhysicsUpdate(double delta) { }
    public void OnExitTree() { }
    public void OnDestroy() { }
}
```

What the one-liner buys you (Cecil-verified from `LifecycleHelper.ProcessNotification`): on `NotificationEnterTree` it registers the node as a component if it's an `IComponent`, then calls `OnEnterTree`; on Ready it registers the handler with `LifecycleManager.Instance` and calls `OnReady`; process / physics ticks call `OnUpdate` / `OnPhysicsUpdate`; on predelete it unregisters and calls `OnDestroy`. The whole switch is wrapped in a swallowing `try/catch` and short-circuits on `LifecycleHelper.BlockAllNotifications`.

- **`OnStart` is the reason to bother** — there's no plain-Godot equivalent. It fires between Ready and the first tick, so it's the place to read state another node set up in *its* Ready/EnterTree (e.g. spawn an object and set fields in `OnReady`, then have a dependent component read them already-initialized in `OnStart`). Dev-confirmed: Robert / Tim, #mod-dev 2026-06-10.
- **`GetUpdateType()` gates the per-frame hooks.** `None` gives you the one-shot hooks (`OnEnterTree`/`OnReady`/`OnStart`/`OnDestroy`) with **zero** per-frame cost; opt into `Update` / `PhysicUpdate` / `All` only when you need ticks.
- **It's direct dispatch, not reflection** (dev-confirmed) — `ProcessNotification` is a plain `switch` on the notification id, so it's cheap.
- **`partial` + the Godot source generator are still required** (same as the code route above), and the `_Notification` override is mandatory — Godot won't deliver notifications to an interface-inherited handler otherwise.
- **If you `AddChild` it from code, free it on teardown.** A code-instantiated `ILifecycleHandler` stays registered with `LifecycleManager` until the node is freed — so a leaked one keeps getting `OnUpdate` ticks *after* your mod is disabled, and pins your `AssemblyLoadContext` (blocking unload). `QueueFree` it in `OnUnload` / `ModDestroy`. Nodes shipped in `root.tscn` are freed for you on disable, so this only bites the code (`AddChild`) route.

Use plain `_Ready`/`_Process` for most mods; reach for `ILifecycleHandler` when you specifically want `OnStart`'s ordering or `LifecycleManager`-ordered ticks.

## CLI flags

Pratfall reads these from its command line at startup:

| Flag | Effect |
|---|---|
| `--qh-disable-mod-ui` | Hides the native Mod button on the main menu. (`ModManager.ShouldHideModLoaderUi` returns true.) |
| `--qh-skip-mods` | **Skips loading all mods.** `ModManager.ShouldLoadMods` returns `!HasFlag("--qh-skip-mods")`; `ModManager.Setup`'s `LoadAllModManifests` completion callback then does `if (ShouldLoadMods) { LoadEnabledMods(); LoadAutoLoadMods(); }`. So with the flag set, **neither enabled mods nor `AutoLoad: true` mods are loaded** — manifests are still scanned (the list is built), but nothing is loaded or enabled. Cecil-verified from `ModManager.Setup` + the `<Setup>b__21_1` callback IL (build `23570525`). *(Was genuinely a no-op in `1.1.0.R2973` — `ShouldLoadMods` had 0 callers then; the game wired it up since.)* |
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

**Heads-up on the launch-args confirmation dialog.** Pre-2026-05-18 builds popped a "Launch Game with custom arguments — Continue / Cancel" dialog on every `--qh-*` launch, which would have made profile-switching painful. **Tim fixed this** in the 2026-05-18 Workshop update; profile-based managers launching via Steam now work without per-launch friction. A separate "launching from the executable" issue Tim flagged is being fixed in a later patch but doesn't affect Steam launches (which is r2modman's path anyway, per Ebkr).

**If you're running Pratfall Mod Framework alongside r2modman**, the framework also honors `--qh-mod-directory`: scans that folder for mods AND writes its own state file (`modframework-state.json` — enabled mods + approved fingerprints) into that folder, so each r2modman profile has independent framework state. Mods dropped as `.zip` files into the profile folder are auto-extracted on framework startup (zip-slip-safe via .NET's `ZipFile.ExtractToDirectory`). One-time migration: on first launch under a new profile, the framework reads the default `user://modframework-state.json` once as a fallback so you don't lose your existing approvals.

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

### Godot ref lifetime — don't trust C# null checks

Godot's C# bindings expose `Node`, `Control`, `Button`, `CanvasLayer`, `Texture2D`, etc. as C# objects backed by underlying C++ objects. The two have **independent lifetimes**: the C++ object can be freed (via `QueueFree`, scene change, `Free`, parent's deletion) while the C# object lingers in memory until the next GC pass. A plain null check passes, but accessing any member throws `ObjectDisposedException` or "called method on already-freed object."

```csharp
// WRONG — passes the null check, crashes on the next line
if (cachedButton != null)
    cachedButton.Text = "Click me";

// RIGHT — IsInstanceValid checks the underlying C++ object
if (Godot.GodotObject.IsInstanceValid(cachedButton))
    cachedButton.Text = "Click me";
```

> Per Tim (#mod-dev, 2026-05-20): "the biggest tip I can give you is to not do null checks on objects instead check if its null with `IsInstanceValid()`. the c# object might still exist because it hasn't been garbage collected yet but the c++ object is already gone."

A tiny extension keeps the check from being verbose at every call site:

```csharp
public static class GodotRefExtensions
{
    public static bool IsAlive(this Godot.GodotObject? obj)
        => obj != null && Godot.GodotObject.IsInstanceValid(obj);
}

// Usage:
if (cachedButton.IsAlive())
    cachedButton.Text = "Click me";
```

**When to be paranoid**:
- Refs held in dictionaries, static fields, or any storage that outlives the current frame
- Refs captured in lambdas wired to `Pressed`, `Timeout`, `Toggled`, etc. — the event may fire after the node has been freed
- Refs passed to `CallDeferred` — the deferred call may run after the target is freed
- Refs returned by `FindChild` / `GetNode` once and cached
- Anything that crosses a `QueueFree`, scene-load, or dialog-close boundary

**When you can skip it**:
- Refs allocated and used within the same method with no async path between create and use
- Refs on objects you fully control and just created (nothing else could have freed them yet)

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

**Alternative — Node in `root.tscn`, no patching.** For simple "re-apply each frame" tweaks, ship a `[GlobalClass] partial class Foo : Node` inside your mod's `root.tscn` and do the work in `_Process`. Robert's [Infinite Flare mod](https://github.com/quad-head/pratfall-infinite-flare-mod) (`FlareModifier.cs`) takes this route — every `_Process` tick it re-sets `Player.LocalPlayer.ThrowFlareComponent.MaxFlares` and `FlareRecoverySeconds`. Relies on the [auto-instantiated `root.tscn`](#auto-instantiated-root-scene-roottscn); no Harmony required. Trade-offs: you redo the work every frame instead of once, and you still own null-checking the entity.

## Recipe: Add a language

> **Current Pratfall release (verified `1.1.0.R2973`, 2026-05-18) gates the JSON-file path.** `LocalizationManager.LoadUserLocalizations` checks `Game.Config.AllowUserLocalization` first — and on the public release that flag is **false** (Cecil-verified: `GameConfig` constructor initializes it to `false`), so the loader silently skips every user-installed locale `.json` file. Tim has said he plans to enable the flag (see `#mod-dev`, 2026-05-18). **`TranslationServer.AddTranslation` is NOT gated** and works today as a first-class path (see [below](#workaround-when-the-gate-is-closed)).

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

**`TranslationServer.AddTranslation` is a first-class path for adding new selectable languages** as of `1.1.0.R2973` — confirmed Cecil + by mod authors (Henrique reported he removed his earlier `NotificationTranslationChanged` workaround once the build shipped). The chain that makes this work:

1. Your mod calls `TranslationServer.AddTranslation(translation)` — adds to Godot's internal locale list.
2. `LocalizationManager.UpdateAvailableLocales()` reads `TranslationServer.GetLoadedLocales()` and assigns it to `LocalizationManager.AvailableLocales`.
3. `GeneralOptionsContentUIView.UpdateText` reads `AvailableLocales` to render the in-game language picker — your new language appears.

So calling `AddTranslation` from your `ModInit` is enough; the picker refreshes when the player opens settings (no manual `NotificationTranslationChanged` listener required anymore). Henrique's [PratfallLocalizationMod](https://github.com/HenriqueCamillo/PratfallLocalizationMod) is the canonical reference and now uses this simpler path.

**JSON-file path coexists.** When `Game.Config.AllowUserLocalization` flips to `true` (Tim has said he plans to enable it), `LoadUserLocalizations` will also feed `TranslationServer.AddTranslation` internally — so the JSON-file convention and the direct-call path both end up populating the same underlying server. Pick whichever fits your distribution model.

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

**You don't need `GameEventBus` for your mod's own logic.** It exists mainly so the game's stats / achievements can react to gameplay without hard coupling (per Robert, #mod-dev). Use it to *react to events the game fires* — like the player-death example below — but for your mod's own internal control flow, just call your own method directly rather than routing through the bus.

**Use the pre-defined `GameplayTags` static class for the tag reference** — Pratfall ships ~42 named `GameplayTag` constants (loaded from `res://data/gameplay_tags/*.tres` in the `GameplayTags` static cctor). `GameplayTag.Equals` compares by `.Tag` string, so an Equals check against `GameplayTags.X` always works.

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

All 42 available tags are listed in [GameplayTags.* (42)](#gameplaytags-42) below, grouped by category (stats, win conditions, status effects, materials, harvestables).

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
        // Real signature: Show(string message, double duration, bool playSound)
        toaster.Show("MyMod loaded!", 3.0, playSound: true);
    }
}
```

For a toast that all players in a multiplayer lobby see, wrap the call in a `Network.EventManager.SendEvent(...)` broadcast on `Constants.EventIdShowToast = 138` instead — the game already handles that event-id and pops the toast on every receiver.

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
        var componentMgr = Network.ComponentManager;
        if (componentMgr == null) return;
        var result = componentMgr.SpawnNetworkPrefab(_propScene, Game.RootNode);
        // result.RootNode is the spawned node; result.IsValid() is the success check.
        if (result.IsValid()) _spawned = result.RootNode;
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
- **Network prefabs are identified by index — registration order must match across clients.** For custom networked prefabs, every client must have the same network prefab registration/order. The network path identifies prefabs by index, so mismatched prefab lists can desync or spawn the wrong thing. (Cecil-verified: `SpawnNetworkPrefab` resolves the scene to a byte index via `NetworkPrefabManager.GetPrefabIndex`, and the receiver instantiates by that index.)
- `Game.RootNode` is the right parent for "persists across the session"; for "lives for one level", parent under a scene-specific node (e.g. via `SceneManager.Instance.GetLoadedScenes()`).
- `ScenePoolManager.Instance` is null very early in boot; defer spawning until at least one scene has loaded.
- `Game.RootNode` is null in the very-early bootstrap window too — your `ModInit` runs after it's set, but be aware.

## Recipe: React to level load

Pratfall has no public `OnSceneLoaded` event on `SceneManager`. The way to react to "a level just finished loading" from vanilla is to subscribe to `Network.EventManager.OnNetworkEventReceived` and filter for the loaded-level event id.

```csharp
using Godot;

public static class ModEntry
{
    private static NetworkEventReceived? _sub;

    public static void ModInit()
    {
        var mgr = Network.EventManager;
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
        var mgr = Network.EventManager;
        if (_sub != null && mgr != null)
            mgr.OnNetworkEventReceived -= _sub;
        _sub = null;
    }
}
```

Gotchas:
- Other loaded-level ids you might care about: `EventIdRequestLevelLoad = 103`, `EventIdUnloadLevel = 120`, `EventIdSetLevelActive = 148`. See the full [`Constants.EventId*`](#constantseventid-72) table.
- `Network.EventManager` is **the** subscription target — a **static property** that returns the live `NetworkEventManager` instance (use `Network.EventManager`, never `Network.Instance.EventManager`; every `Network.*` manager is a static accessor). The events fire whether you're host, client, or singleplayer.
- The `NetworkFrameEvent` payload exposes `EventId`, `TargetId`, and `Data` (the raw bytes). For loaded-level you don't need the payload; for other events, call `ev.GetEvent<YourEventType>()` to deserialize.

## Recipe: Multiplayer patterns

> **These are basic patterns, not a complete sync protocol.** A host check and a late-join hook do NOT by themselves make a mod multiplayer-safe. Pratfall has a two-layer network stack (low-level frame messages + high-level tagged events) and the safer path for any mod that changes gameplay rules, saved state, inventory, drops, or authority is to build explicit per-mod sync via `Network.EventManager.SendEvent` with a custom event id and sender identity embedded in the payload. If you haven't built that, treat your mod as **"all players need this mod installed and enabled"** and say so in your README — don't rely on host-only logic working invisibly for clients. The decoded protocol map (local research notes) covers the full stack; the recipes below cover only the entry-level patterns.

Vanilla Pratfall doesn't have a single `IsHost` shortcut — host/client identity lives on `Network.LobbyManager`. The patterns below cover the four things multiplayer mods always need to do.

```csharp
using Godot;

public static class ModEntry
{
    public static void ModInit()
    {
        var lobby = Network.LobbyManager;
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
        => Network.LobbyManager?.IsLobbyOwner ?? false;

    private static bool IsSingleplayer()
        => Network.LobbyManager?.IsSingleplayerLobby ?? true;

    private static void OnMemberJoined(INetworkLobbyMember member)
    {
        if (!IsHost()) return;  // only the host replays state to new joiners
        // Send mod state to the joiner via Network.EventManager.SendEvent
        // with a custom eventId outside 100–169 and 230–231 to avoid collisions
        // (see the Constants.EventId* table for the used range).
        // const ushort MyModStateSyncId = 50000;
        // Network.EventManager.SendEvent(MyModStateSyncId, mySnapshot,
        //     NetworkMessageSendOption.Reliable, "MyMod.StateSync");
        GD.Print($"[MyMod] new member joined (index={member.Index}); replaying state");
    }

    private static void OnMemberLeft(INetworkLobbyMember member)
    {
        GD.Print($"[MyMod] member left (index={member.Index}); cleaning up per-member state");
    }

    public static void ModDestroy()
    {
        var lobby = Network.LobbyManager;
        if (lobby == null) return;
        lobby.OnMemberJoined -= OnMemberJoined;
        lobby.OnMemberLeft   -= OnMemberLeft;
    }
}
```

Key facts:
- **Host check:** `Network.LobbyManager.IsLobbyOwner` (bool property on `NetworkLobbyManagerBase`). There is **no** `Network.IsHost` shortcut — that's invented and doesn't exist.
- **Singleplayer check:** `Network.LobbyManager.IsSingleplayerLobby`. Always true for offline play even though `Network` itself is still up.
- **Local member identity:** `Network.LobbyManager.LocalLobbyMember` (`INetworkLobbyMember` — exposes `Index`, `IsLocal`, `IsServer`, `GetUserId()`).
- **All members:** `Network.LobbyManager.LobbyMembers` (List).
- **Joiner notifications:** subscribe on `NetworkLobbyManagerBase.OnMemberJoined` / `OnMemberLeft` (instance `Action<INetworkLobbyMember>` fields). `LateJoinManager` is *not* the right hook — it has no public events; it's the manager that the *game* uses, not what mods subscribe to.
- **Custom network event ids:** pick anything outside `100–169` (gameplay events) and `230–231` (`EventIdGameModeChanged` + `EventIdSubmitSpeedrunTime`) to avoid future-Pratfall collisions. Document your ids in your README so two mods don't pick the same one.
- **README compatibility tag:** mod authors in comparable communities (Risk of Rain 2, Lethal Company, REPO) self-tag mods as one of:
  - **Client-side only** — visual / UI only; the host doesn't need your mod, lobby members with or without it are compatible
  - **Host-only** — only the host runs the logic; clients are unaffected
  - **All players need this** — protocol-level changes; mismatched lobbies break in subtle ways

  Pratfall's mod framework can negotiate this automatically when both sides have it, but vanilla mods should at least *declare* it in their README so players know what to expect.

### Custom network events — the payload must implement `INetworkEvent`

`NetworkEventManager.SendEvent<T>` and `NetworkFrameEvent.GetEvent<T>` are constrained `where T : INetworkEvent`, so **your payload type must implement `INetworkEvent`** — a `Serialize(ByteBufferWriter)` and a `Deserialize(ByteBufferReader)` that write/read your fields in the *same order*. The receive handler signature is `void (ushort eventId, NetworkFrameEvent eventData)`; the `eventId` is not the payload — read it with `eventData.GetEvent<T>()`. Sender identity is **not** exposed to the handler, so embed it in the payload if you need it.

**`SendEvent` does NOT loop back to the sender — this holds whether you call it on `Network.EventManager` or on a `NetworkComponent` (Cecil-verified: both paths only serialize the payload onto the outgoing frame; neither invokes the local handler).** If the host calls `SendEvent`, only the *other* players' `OnNetworkEventReceived` fires — the host's does not. Same for a client: it won't receive its own event. So don't put your state-change logic *only* inside `OnNetworkEventReceived` — factor it into a shared `Apply…` method that the **sender calls locally right after `SendEvent`** and that **receivers call from the handler**. (`GameEventBus` is a separate, local pub/sub system and does not have this caveat.) The whole block below is verified to compile against the current shipped build (Steam build `23505941`).

```csharp
using Godot;

public static class ModEntry
{
    private const ushort CrownEventId = 50000;   // outside the game's 100–169 / 230–231 range

    public static void ModInit()
    {
        // Managers are STATIC accessors; the manager is null until the network is up.
        if (Network.EventManager != null)
            Network.EventManager.OnNetworkEventReceived += OnNetEvent;
    }

    public static void ModDestroy()
    {
        if (Network.EventManager != null)
            Network.EventManager.OnNetworkEventReceived -= OnNetEvent;   // always unsubscribe
    }

    private static void OnNetEvent(ushort eventId, NetworkFrameEvent eventData)
    {
        if (eventId != CrownEventId) return;
        CrownState state = eventData.GetEvent<CrownState>();
        ApplyCrownState(state);                  // receivers apply here
    }

    // Host-authoritative: host decides, applies locally, then broadcasts.
    public static void HostAssignCrown(int holderId)
    {
        if (Network.LobbyManager == null || Network.EventManager == null) return;
        if (!Network.LobbyManager.IsLobbyOwner) return;                  // host only

        var state = new CrownState { HolderId = holderId };
        ApplyCrownState(state);                  // sender applies locally — SendEvent won't loop back!
        Network.EventManager.SendEvent(CrownEventId, state, NetworkMessageSendOption.Reliable, "crown");
    }

    // Shared by both paths: receivers call it from OnNetEvent, the sender calls it after SendEvent.
    private static void ApplyCrownState(CrownState state)
    {
        GD.Print($"[CrownMod] crown holder = {state.HolderId}");
        // move/update your marker here
    }
}

// Payload — must implement INetworkEvent.
public class CrownState : INetworkEvent
{
    public int HolderId;
    public void Serialize(ByteBufferWriter writer) { writer.Write(HolderId); }
    public void Deserialize(ByteBufferReader reader) { HolderId = reader.ReadInt32(); }
}
```

`ByteBufferWriter` / `ByteBufferReader` expose typed primitives — `Write(int/uint/float/bool/string/byte)` and `ReadInt32() / ReadSingle() / ReadBoolean() / ReadString(defaultValue) / …`. For a real-world payload shape, copy `CustomGameManager.CustomGameSettingsNetworkEvent`.

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

Mods that ship `.tscn` files or instantiate custom Godot-derived types (`class MyComponent : Node3D`, `class MyResource : Resource`, `[GlobalClass]` attributes) need their assembly registered with Godot's script bridge. **Registration is now automatic — no manifest flag.** Pratfall's loader's `LoadAssembly` always calls `Godot.Bridge.ScriptManagerBridge.LookupScriptsInAssembly` for every mod assembly (verified build `23570525`), so custom `Node` / `Resource` subclasses work from `.tscn` and `PackedScene.Instantiate` with no opt-in.

So: **no code recipe and no manifest field is needed** — the registration is handled by the loader, not your mod.

> **The `AddAssemblyToGodot` opt-out was removed in the 2026-06 big update.** It used to be a manifest field defaulting to `true`; registration is now unconditional, so the flag no longer exists. If you carried it over from an old manifest, it's simply ignored.

Under the hood, Pratfall calls `Godot.Bridge.ScriptManagerBridge.LookupScriptsInAssembly(yourAssembly)` after loading your DLL. If you ever need to call it manually (e.g. for a runtime-loaded sub-assembly), you can:

```csharp
Godot.Bridge.ScriptManagerBridge.LookupScriptsInAssembly(myAssembly);
```

## Recipe: Gold, progression & compatibility-renderer (2026-06 update)

The 2026-06 big update (build `23570525`) added a gold economy, a progression/difficulty system, and a compatibility (low-spec GPU) renderer. Most of the new content — vending machines, treasure chests, the hub, the rescue-victory screen — is **scene-internal game plumbing, not a mod API**; interact with it through `InteractableComponent`'s delegate fields (see the [Harmony](#recipe-harmony-patches) / [multiplayer](#recipe-multiplayer-patterns) recipes) or the [drop-pool recipe](#recipe-extend-a-drop-pool), not by driving those components directly. The genuinely mod-facing entry points are below (all signatures Cecil-verified against build `23570525`).

### Gold

`GoldManager.Instance` is the gold API (a `public static` field; `null` until a gameplay scene is loaded). The spendable **wallet** lives on the savegame; `GoldManager.CurrentGoldCoins` is something else (the coins physically spawned in the world right now).

```csharp
var gold = GoldManager.Instance;
if (gold == null) return;                          // null on the main menu

// Read the player's spendable wallet (NOT CurrentGoldCoins, which is live world pickups):
int wallet   = SavegameManager.CurrentSavegame.CurrentGold;        // spendable
int lifetime = SavegameManager.CurrentSavegame.TotalCollectedGold; // lifetime counter

gold.SaveGoldValue(50);                            // award 50 — replicates, saves, fires the stat

if (gold.CanConsumePrice(100) && gold.TryConsumePrice(100))   // charge 100 if affordable
    GD.Print("bought it");

gold.SpawnGoldAt(new Vector3(0, 1, 0), Vector3.Up, 3f);       // drop physical coins at a point
```

Don't write `Savegame.CurrentGold` directly — go through `SaveGoldValue` / `TryConsumePrice` so the change replicates, persists, and counts toward stats. To **react** to gold being collected, reuse the [game-event recipe](#recipe-listen-to-game-events) and filter on `GameplayTags.Stats_Gameplay_Collected_Gold` (the event payload carries the `int` amount).

### Detecting compatibility-renderer (low-spec) mode

Visual mods that need to degrade on the GL-compatibility backend read the active renderer from a static helper — there is no manager or singleton:

```csharp
// GodotRenderer is a game enum: ForwardPlus = 0, Mobile = 1, Compatibility = 2.
// GodotHelper.GetRenderer() is cached; it wraps RenderingServer.GetCurrentRenderingMethod().
if (GodotHelper.GetRenderer() == GodotRenderer.Compatibility)
{
    // shrink particle counts, raise light energy, disable expensive nodes, …
}
```

The four vanilla `*OnCompatibilityRendererComponent` nodes are exactly this pattern — each applies its tweak in `OnStart()` only when `GetRenderer() == Compatibility`, and no-ops otherwise. Attach one in a `.tscn` for declarative behavior, or just call `GetRenderer()` yourself.

### Progression / difficulty (read-only)

You can query unlock state on the local player. Note there is **no public accessor for the current run's difficulty or mode** — that selection is committed via internal host-authoritative network events:

```csharp
var prog = Player.LocalPlayer?.PlayerProgressionComponent;
bool hardUnlocked      = prog?.IsDifficultyUnlocked(DifficultyLevel.Difficulty1) ?? false;
bool challengeUnlocked = prog?.IsModeUnlocked(ProgressionMode.Challenge) ?? false;
// DifficultyLevel.Easy/Peaceful/Normal and ProgressionMode.Standard are always unlocked.
```

Everything else in the new systems (the gold/vending/treasure components, `GameModeProgressionConfig`, the progression / challenge / rescue UI controllers) is internal — driven by scene data and host-authoritative network events, not a mod API.

## Recipe: PCK assets — unpack, repack, and override game assets

Pratfall ships its assets as a single Godot PCK (`Pratfall.pck` next to the executable). Mods that need to **inspect** the game's assets (to find `res://` paths, override existing scenes, or reference a built-in texture) or that need to **ship their own assets** (custom scenes, textures, audio) work with PCK files directly.

### Unpacking `Pratfall.pck` to see what's inside

Use [**GDRE Tools / gdsdecomp**](https://github.com/GDRETools/gdsdecomp) — community-maintained Godot reverse-engineering tool with Godot 4 PCK extraction and project-recovery support. Point it at `<Pratfall install>\Pratfall.pck`; if a future Pratfall build ever ships with the pack embedded in the executable, point it at the `.exe` instead (GDRE supports `--recover=<GAME_PCK/EXE/APK/DIR>` and `--extract=<GAME_PCK/EXE/APK>`).

It can recover/extract most of the project tree (the exact result depends on how the game was exported):

- Scenes (`.tscn`, `.scn`) — open in Godot to see node structure
- Resources (`.tres`, `.res`) — configs, materials, animations
- Imported assets (textures, audio, fonts) converted back toward their original formats
- GDScript files **if any** — Pratfall is pure C#, so this is usually empty

What's NOT in the PCK:

- **C# game code** — Pratfall's logic lives in `Pratfall.dll`, not in the PCK. Use [ILSpy](https://github.com/icsharpcode/ILSpy) as your primary .NET decompiler; [dnSpyEx](https://github.com/dnSpyEx/dnSpy) (active fork of the original dnSpy) if you need debugger / assembly-editor workflows. `Mono.Cecil` is the right call for programmatic inspection (the framework + this guide were both built on Cecil scans).
- **Native libraries** — `.dll` / `.so` / `.dylib` files sit next to the executable, not inside the PCK.

Typical use cases for unpacking:

- Find the `res://` path of a vanilla scene or texture you want to swap out by mounting your own PCK at the same path (the supported override mechanism — see [Overriding Pratfall's own assets](#overriding-pratfalls-own-assets)).
- Read a Pratfall `.tres` config to understand its structure before extending it (e.g., `GameModeBaseConfig`, `BiomeConfig`, `MaterialConfig`).
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
   │   │   └── icon.png.import    ← auto-generated by Godot on first import
   │   └── audio/
   │       ├── ding.ogg
   │       └── ding.ogg.import    ← auto-generated by Godot on first import
   └── ...
   ```

2. **Let Godot import your assets before exporting** — open the project in the Godot editor and let it scan. Godot processes raw assets (`.png`/`.jpg`/`.ogg`/`.wav` etc.) into engine-specific imported resources stored under `res://.godot/imported/`, and writes `.import` side files next to your sources tracking the import settings. The PCK exporter packages whichever imported artifacts the engine needs — you don't have to hand-include the `.import` files specifically; Godot handles it. The mistake to avoid is **manually zipping raw assets into a PCK and expecting the runtime to load them as Godot resources**. That will not work.

3. **Export the PCK** — Godot editor:

   - `Project → Export → Add...`
   - Choose any preset (PCK doesn't actually need platform-specific binaries; Windows Desktop is fine)
   - Click **`Export PCK/Zip...`** (NOT "Export Project" — that builds an EXE you don't want)
   - Save as `YourMod.pck` next to your mod's DLL

4. **Reference the PCK in your mod's manifest**. Pratfall's native loader reads `PackageName`:

   ```json
   { "Name": "YourMod", "Assembly": "YourMod.dll", "PackageName": "YourMod.pck" }
   ```

   The framework manifest accepts `pckFile` (camelCase) or `PckFile` (PascalCase) — both parse to the same field per `ModManifest.FromJson`:

   ```json
   { "id": "YourMod", "assemblyFile": "YourMod.dll", "pckFile": "YourMod.pck" }
   ```

5. **Reference assets from your DLL** using the mounted path:

   ```csharp
   var scene = GD.Load<PackedScene>("res://YourModFolderName/scenes/MyScene.tscn");
   var icon = GD.Load<Texture2D>("res://YourModFolderName/textures/icon.png");
   var ding = GD.Load<AudioStream>("res://YourModFolderName/audio/ding.ogg");
   ```

### Auto-instantiated root scene (`root.tscn`)

Cecil of `ModManager.LoadPackage` (R2973): after mounting the PCK, Pratfall tries to load `res://<DirectoryName>/root.tscn` and, if it exists and parses as a `PackedScene`, **instantiates it and adds the result as a child of the game root**. The instantiated node is stored on the manifest so `ModManager.UnloadPackage` can `Free()` it on disable.

This means:

- If your mod ships `res://<YourModFolderName>/root.tscn`, it auto-runs on enable — useful for mods that want to inject a scene into the world without writing C# (the auto-instantiated node + its `_Ready` handle the wiring).
- If your mod does NOT ship `root.tscn`, the PCK still mounts cleanly — the load step silently no-ops on the missing scene. Your assets are still reachable via `res://<DirName>/...` paths from your DLL.
- **Same silent no-op if the folder name is wrong.** `DirectoryName` comes from the on-disk folder for local installs but from `workshop_manifest.json`'s `FolderName` for Workshop installs, so a mod copied into a folder that doesn't match the PCK's baked `res://<folder>/` root will look for a `root.tscn` that isn't there and quietly do nothing — see [PCK packaging gotchas](#pck-packaging-gotchas).

### Overriding Pratfall's own assets

`LoadResourcePack` is called with `replace_files=true` (Cecil-verified, IL `ldc.i4.1` before the call). So if your PCK contains a file at the same `res://` path as one already in `Pratfall.pck`, **your version wins**. That's the supported mechanism for asset overrides — swap a texture, replace a sound, override a scene — without needing C# patches. Be conservative: a poorly-targeted override (matching a path you didn't mean to) can break vanilla content silently.

### PCK packaging gotchas

- **Folder name = mount path — and it's resolved differently for local vs Workshop installs.** Assets and the auto-`root.tscn` load from `res://<DirectoryName>/...`, where `DirectoryName` is set (Cecil-verified, build `23570525`) from **`Path.GetFileName(<mod dir>)`** for a local `mods/<folder>/` install but from **`workshop_manifest.json`'s `FolderName`** for a Workshop install (NOT the numeric `<workshopid>` folder it physically lives in). Either way it must exactly equal the `res://<folder>/` your Godot project baked into the PCK.
  - *Authoring your own mod:* keep your Godot project's root folder name identical to the folder you ship under.
  - *Installing someone else's PCK mod (silent trap):* pick the wrong install-folder name and `res://<DirectoryName>/...` resolves to paths that aren't in the PCK, so the lookups **silently no-op** — the mod loads, enables, and reports no error, but does nothing. Example: Infinite Flare's PCK bakes its scene at `res://infinite-flare-mod/root.tscn` (its `workshop_manifest.FolderName` is `infinite-flare-mod`), so copying the same DLL + PCK into `mods/InfiniteFlareMod/` makes the loader look for `res://InfiniteFlareMod/root.tscn` — which isn't in the PCK → the scene never instantiates, even once you've enabled the mod. Fix: name the local folder to match the baked root (`mods/infinite-flare-mod/`). Confirm a PCK's baked root with `strings yourmod.pck | grep -i res://`.
- **PCK filename = `PackageName` field exactly.** Pratfall concats `manifest.Directory + "/" + manifest.PackageName` — so `PackageName` must include the `.pck` extension and match the filename on disk.
- **Let Godot do the importing.** Raw `.png` / `.ogg` / `.wav` etc. must be imported by the Godot editor before you export the PCK; Godot converts them to engine-specific resources and tracks them via `.import` side files in your project. Don't manually zip raw asset files into a PCK and expect Godot to load them as resources — at runtime use `ResourceLoader.Load("res://...")` (which goes through the import pipeline), not raw `FileAccess`.
- **PCKs cannot be unmounted in Godot 4.** `UnloadPackage` only `Free()`s the auto-instantiated root.tscn node — the PCK's files stay mounted in `res://` until the next game restart. The framework surfaces a "may not fully apply until next launch" notice for mods with a `pckFile`.
- **One mod, one PCK.** Two mods with assets at the same `res://<DirName>/...` path silently overwrite each other based on PCK mount order (`replace_files=true` again) — another reason folder names must be unique across all installed mods.

## Decoded Pratfall surface inventory

Audit of `Pratfall.dll` (2026-05-17 — Pratfall `1.1.0.R2943`) — 822 game types analyzed (skipping Epic / NAudio / SixLabors / ImGuiNET / K4os / MemoryPack / System / Steamworks namespaces). All numbers below are Cecil-verified.

**Spot-check follow-up for `1.1.0.R2973` (2026-05-18 Workshop update):** the modding subsystem was substantially restructured (ModManager got `LoadAllModManifests`, `LoadedMods`, `OnModsLoaded`, `ModsDirectory`, `Setup`, new Workshop-loading methods; `GetModManifest` renamed `GetModManifestFromDirectory` AND privatized; `ModManifest` gained `IsSteamWorkshopMod` / `SteamWorkshopManifest` / `SteamWorkshopItem` properties). The 822-type total isn't materially different; specific ModManager API renames are flagged inline in the [ModManager bullet](#static-helper-classes-22) (Static helper classes section). The non-modding inventory (singletons, events, configs, components, GameplayTags, EventIds) was not re-audited in full and may have small drift — re-Cecil before relying on a specific signature.

**Full re-verification against build `23598402` (2026-06-09, R3809):** every catalog below was re-derived from the live DLL and matched name-for-name — 78 singletons / 22 static helpers / 27 configs / 11 events / 42 `GameplayTags` / all 72 `Constants.EventId*` name→value pairs / 203 `IComponent` implementors / 27 entities / 13 interfaces — the behavioral IL claims throughout this guide were re-checked against the live method bodies, and every recipe re-compiled clean against the live assemblies. Build-number stamps on individual claims record where each was *first* verified; all still hold as of `23598402`.

This section is a **reference map**, not a tutorial. The goal: when you're mid-mod and you need to know "is there a manager for X?" or "what events fire when a player dies?", you should be able to find the answer here instead of disassembling `Pratfall.dll` yourself.

### "How do I ...?"

| Goal | Look at |
|---|---|
| Play a sound | `AudioManager.Instance` (SFX), `MusicManager.Instance` (music), `UISoundManager.Instance` (UI clicks) |
| Read which player is me | `Player.LocalPlayer` (static field on `Player`) |
| Find the local player's components | `Player.LocalPlayer.<ComponentName>Component` — every component on `IEntity` is a property (see [Entity hierarchy](#entity-hierarchy--ientity)) |
| React to a save | `SavegameManager.OnGameWillSave` / `OnGameDidSave` ([recipe](#recipe-persist-mod-data)) |
| React to a player dying | Subscribe `GameEventBus.OnGameEventReceived` and compare `tag.Equals(GameplayTags.Stats_Gameplay_Player_Death)` ([recipe](#recipe-listen-to-game-events)) |
| Send a custom network message | `Network.EventManager.SendEvent(ushort eventId, T evt, NetworkMessageSendOption opt, string name)` — `T` must implement `INetworkEvent`; pick an ID that doesn't collide with [`Constants.EventId*`](#constantseventid-72). `Network.EventManager` is a static property returning the live instance |
| Add a HUD prompt ("Press [A]") | `ButtonPrompBarController.Instance.AddButtonPrompt(...)` ([recipe](#recipe-show-hud-button-hints)) |
| Add an in-game language | Drop a JSON in `<userData>/localization/` ([recipe](#recipe-add-a-language)) |
| Add a possible item drop | Mutate `DebugMappingManager.Instance.DropPools[i].Pool` ([recipe](#recipe-extend-a-drop-pool)) |
| Add a custom `Node` / `Resource` type | Automatic — the loader registers every mod assembly with Godot's script bridge; no manifest flag ([recipe](#recipe-custom-godot-types)) |
| Add a game mode or level | Safe — neither is saved by index. (Player **colors** ARE save-coupled — don't insert/reorder those.) See [Save-coupled arrays](#save-coupled-arrays--dont-mutate) |
| Spawn an entity from code | `ScenePoolManager.Instance.Instantiate(packedScene, parent)` for local-only, `Network.ComponentManager.SpawnNetworkPrefab(prefab, parent)` for replicated ([recipe](#recipe-spawn-an-entity)) |
| Hook game ticks | Override `_Process` / `_PhysicsProcess` on a `Node` you parent under `Game.RootNode`, or use `MainThreadDispatcher.Instance.Enqueue(Action)` for one-shot off-thread → main-thread dispatch |
| Get the user save folder | `Game.Platform.GetUserDataPath()` then `ProjectSettings.GlobalizePath(...)` for a real filesystem path |
| Know which config is "the game settings" | `Game.Config` — but it's a struct with `init`-only setters, you can read but not mutate |

### Singletons (78)

A *singleton* here is a public class with a static `Instance` field or static-getter property. Access via `<Name>.Instance.<Member>`. Many are HUD/UI controllers that are **null on the main menu** — they only exist while a gameplay scene is loaded. (Generic/internal infrastructure singletons such as `NodeCounter<T>` are omitted — they're not author-facing.)

**Game state & flow**
- `GameController` — top-level game state, level loading orchestration
- `GameModeManager` — game-mode list + active mode (`Modes` is **safe to extend** — the selected mode isn't saved by index; see [pitfalls](#save-coupled-arrays--dont-mutate))
- `CustomGameManager` — custom-game preset state
- `LevelManager` — level prefab list (`LevelPrefabs` is **safe to extend** — no level index is persisted)
- `LifecycleManager` — drives `_Ready` / `_Process` / `_PhysicsProcess` ordering for `ILifecycleHandler`s
- `SceneManager` — scene transition queue
- `Loader` / `Preloader` / `LoadingScreenManager` — resource + scene loading pipeline
- `ScenePoolManager` — pooled scene instances (for `IPooledObject` reuse)
- `DebugMappingManager` — game's drop-pool registry (`DropPools` array). Populated by the active scene, not by code
- `GoldManager` — gold economy / currency state
- `HubItemManager` — hub item state

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
- `CrowdControlManager` — Crowd Control integration

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
- `GameStartChallengeUIController` — challenge-mode start screen
- `GameStartProgressionUIController` — progression-mode start screen
- `RescueVictoryUIViewController` — rescue-victory screen

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
- `ProceduralCaveComponent` — procedural cave generation singleton (holds `BiomeGenerationConfigs` — multiplayer-deterministic, keep identical across the lobby; not save-coupled)
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
- **`ModManager`** — Pratfall's native mod loader (substantially expanded in the 2026-05-18 `1.1.0.R2973` Workshop update). Public surface in R2973: `Setup()`, `LoadAllModManifests(bool isInitialLoad, Action onComplete)`, `LoadedMods` (List<string> — the enabled set read from `enabled_mods.json`; entries are **full mod directory paths**, not bare folder names, and `IsModEnabled` exact-matches them against `manifest.Directory`), `OnModsLoaded` (Action callback fired after `LoadAllModManifests` completes — useful if you want to react to "mods are ready"), `ModsDirectory` (string, active mods **folder path** — changes with `--qh-mod-directory`), `IsInitialized`, `EnabledModCount`, `EnableMod(ModManifest)`, `DisableMod(ModManifest)`, `IsModEnabled(ModManifest)`, `ShouldLoadMods` getter (`!HasFlag("--qh-skip-mods")` — now read by `ModManager.Setup`'s `LoadAllModManifests` callback as of build `23505941`; was unused in `1.1.0.R2973`), `ShouldHideModLoaderUi` getter. **Note**: `GetModManifest(string)` was renamed `GetModManifestFromDirectory(string)` AND made private — if you used the old name in pre-R2973 builds, you'll need to switch to iterating `Mods` (the `List<ModManifest>` property) or call `GetModManifestFromDirectory`. ([lifecycle recipe](#lifecycle))
- `NetworkHelper` — common multiplayer helpers
- `PerformanceHelper` — perf-counter conveniences
- `SaveDataManager` — low-level read/write of save blobs (the file-IO half)
- `SavegameManager` — save lifecycle + events (the orchestration half — see [recipe](#recipe-persist-mod-data))
- `SentryHelper` — Sentry crash-reporter integration
- `SettingsManager` — settings load/save (read `GeneralSettings`, `AudioSettings`, `VideoSettings`, `InputSettings`)
- `SteamLeaderboardHelper` — Steam leaderboard wrappers
- `TimeFormatHelper` — duration formatting

### Configs & Settings (27)

`*Config` and `*Settings` types — game-tuning data. Most are read via `Manager.Instance.Config` or `Game.Config`. **Don't mutate at runtime** — they're either struct-by-value (changes don't stick) or save-coupled (mutating breaks other players' saves).

| Type | Where you read it | Notes |
|---|---|---|
| `GameConfig` | `Game.Config` | Top-level — `AllowUserLocalization`, `BuildId`, … `init`-only setters, struct semantics |
| `NetworkConfig` | game internals | network-tuning |
| `NetworkPrefabsConfig` | `NetworkComponentManager` | networked-prefab registry |
| `GameModeBaseConfig` + `GameModeCustomConfig` / `GameModeSpeedrunConfig` / `GameModeStoryConfig` / `GameModeProgressionConfig` | `GameModeManager.Modes[i]` | per-mode config — **safe to extend** (mode isn't saved by index) |
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
| `OnNetworkEventReceived` | `Network.EventManager` (static prop → instance) | `NetworkEventReceived(ushort eventId, NetworkFrameEvent eventData)` | Low-level network event — `Constants.EventId*` IDs. `Network.EventManager` is null until the network is `_Ready`; gate subscription on a non-null check |
| `OnGetNetworkSpawnParent` | `NetworkComponentManager` | `NetworkSpawnParentCallback` | Override the parent node for spawned networked objects |
| `OnGcTiming` | `GcTimingListener` | `Action<GcTiming>` | GC-pause measurements (perf instrumentation) |
| `OnValueChanged` / `OnRemoteValueChanged` | `NetworkVar<T>` / `NetworkVarNode<T>` | `Action<T>` | Per-instance — fires when a replicated value changes |

There is **no `OnGameDidLoad`**. The game's `Setup(...)` accepts an `onGameDidLoad` callback that only the game itself subscribes to. For mods, load your state in `ModInit` by reading your file directly.

### `GameplayTags.*` (42)

`GameplayTag` resources pre-loaded from `res://data/gameplay_tags/*.tres` by the `GameplayTags` static class. Use these for `GameEventBus` filtering — compare with `incomingTag.Equals(GameplayTags.X)` (value equality on `.Tag` string, so the static-vs-runtime instance gotcha doesn't bite you).

**Stats / gameplay events** (fired by the game when these things happen — subscribe to track player actions)
- `Stats_Gameplay_Player_Death`, `Stats_Gameplay_Player_Damage`, `Stats_Gameplay_Fall_Damage`, `Stats_Gameplay_Heal`
- `Stats_Gameplay_Caught_Player`, `Stats_Gameplay_Threw_Flare`, `Stats_Gameplay_Bat_Hit`, `Stats_Gameplay_Worm_Hit`
- `Stats_Gameplay_Open_Package`, `Stats_Gameplay_Ball_For_Dog`, `Stats_Gameplay_Unconscious`
- `Stats_Gameplay_Ate`, `Stats_Gameplay_Ate_Freeze_Pop`, `Stats_Gameplay_Ate_Grape_Juice`
- `Stats_Gameplay_Stick_Chameleon_Grenade`, `Stats_Gameplay_Stuck_Sticky_Bomb`
- `Stats_Gameplay_Depth_Reached`, `Stats_Gameplay_New_Depth`, `Stats_Gameplay_Finish_Game`, `Stats_Gameplay_Win`
- `Stats_Gameplay_Collected_Gold`, `Stats_Gameplay_Win_Treasure`
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

### `Constants.EventId*` (72)

`ushort` (System.UInt16) constants holding numeric event IDs (`Constants.EventIdJump = 129`). Used by `Network.EventManager.SendEvent(UInt16 eventId, T evt, NetworkMessageSendOption opt, string eventIdName)` for **low-level network messages** — the `eventIdName` parameter is a separate human-readable debug-name string, NOT the event id itself. Different system from `GameEventBus` / `GameplayTags` — don't mix them.

Sorted by numeric ID:

| ID | EventId | ID | EventId |
|---|---|---|---|
| 100 | `EventIdInteraction` | 136 | `EventIdDropInventory` |
| 101 | `EventIdEmote` | 137 | `EventIdBootApplyStart` |
| 102 | `EventIdCameraShake` | 138 | `EventIdShowToast` |
| 103 | `EventIdRequestLevelLoad` | 139 | `EventIdPlayEmote` |
| 104 | `EventIdStartMission` | 140 | `EventIdBatEat` |
| 105 | `EventIdEndMission` | 141 | `EventIdTriggerContact` |
| 106 | `EventIdDebugShowItemTray` | 142 | `EventIdTeleport` |
| 107 | `EventIdTakeDamage` | 143 | `EventIdKnockBat` |
| 108 | `EventIdApplyImpulse` | 144 | `EventIdRequestStartTeleportEffect` |
| 109 | `EventIdShootCannon` | 145 | `EventIdTriggerGameEnd` |
| 110 | `EventIdShovelPosition` | 146 | `EventIdTriggerExtractor` |
| 111 | `EventIdRequestRevive` | 147 | `EventIdQuickRestart` |
| 112 | `EventIdTookDamage` | 148 | `EventIdSetLevelActive` |
| 113 | `EventIdExplode` | 149 | `EventIdNotifyFlareStick` |
| 114 | `EventIdNetworkGroupUnregistered` | 150 | `EventIdBatHitWithObject` |
| 115 | `EventIdGameOver` | 151 | `EventIdUpdateCustomGameSettings` |
| 116 | `EventIdCloseGameOverUI` | 152 | `EventIdResetRagdoll` |
| 117 | `EventIdGameRestart` | 153 | `EventIdUpdateProgressionGameSettings` |
| 118 | `EventIdCaughtPlayer` | 154 | `EventIdRequestMarkLateJoin` |
| 119 | `EventIdLoadedLevel` | 155 | `EventIdSpawnGoldCoin` |
| 120 | `EventIdUnloadLevel` | 156 | `EventIdCollectGoldCoin` |
| 121 | `EventIdUnloadLevelAck` | 157 | `EventIdLoadGameplayLevel` |
| 122 | `EventIdEquipCosmetic` | 158 | `EventIdApplyGold` |
| 123 | `EventIdHonk` | 159 | `EventIdKnockbackAndKillBat` |
| 124 | `EventIdLaserBeamSpawn` | 160 | `EventIdDropInteractable` |
| 125 | `EventIdPickaxeAction` | 161 | `EventIdVendingMachineAnimation` |
| 126 | `EventIdEnemySpit` | 162 | `EventIdGenerateSmallBranchBlob` |
| 127 | `EventIdGenerateBranch` | 163 | `EventIdKnockbackPlayer` |
| 128 | `EventIdContactDamage` | 164 | `EventIdNotifyWormClumpDeath` |
| 129 | `EventIdJump` | 165 | `EventIdExplodePoop` |
| 130 | `EventIdBootApply` | 166 | `EventIdApplyDifficulty` |
| 131 | `EventIdPlayHungrySound` | 167 | `EventIdUseAirHorn` |
| 132 | `EventIdDigPlayerFree` | 168 | `EventIdRequestVendingMachine` |
| 133 | `EventIdChangeMaterialAt` | 169 | `EventIdAnswerVendingMachine` |
| 134 | `EventIdChangeFloorAt` | 230 | `EventIdGameModeChanged` |
| 135 | `EventIdSetUnconscious` | 231 | `EventIdSubmitSpeedrunTime` |

Used range: 100–169 contiguous, plus 230–231 for stats events. If you ship a custom network event, pick an ID outside those ranges to avoid collisions with future Pratfall releases.

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

Concrete entities (27 total, Cecil-counted):
- `NodeEntity`, `Node3DEntity`, `RigidBody3DEntity`, `RigidBody3DCheapEntity`, `StaticBody3DEntity` — base classes (`RigidBody3DCheapEntity` added in the 2026-06-01 update — a lighter-weight `RigidBody3D` entity)
- `Player` (extends `RigidBody3DEntity`) — **the main thing mods care about**
- `WorldEntity`, `YarnBallEntity` — world-root entities
- Managers that are also entities: `CollisionSoundManager`, `CustomGameManager`, `DebugMappingManager`, `DynamicParticleManager`, `ExplosionManager`, `FreeFlyCamera`, `GameModeManager`, `GoldManager`, `HangDebuggerNode`, `HubItemManager`, `InstanceDrawManager`, `LevelManager`, `NetworkGroupManager`, `ScenePoolManager`, `SpeedrunManager`, `StoryPanelManager`, `WorldTextManager`
- Cameras: `CharacterEditorCamera`, `SpectatorCamera`

**`IEntity` exposes 204 properties** — 203 component-accessors (one per `IComponent` subclass) plus `Components: Dictionary<int, IComponent>` for dynamic access. This is the killer feature for mods:

```csharp
// Instead of GetComponent<PlayerHealthComponent>() everywhere, you just write:
var hp = Player.LocalPlayer?.PlayerHealthComponent;
// PlayerHealthComponent is actually Pratfall's FOOD/HUNGER component despite
// the "Health" name. Real fields per Cecil dump: FoodValue (UInt16 current),
// MaxFoodValue (UInt16 cap), FoodNormalized / HungerNormalized (0-1 floats),
// FoodConsumptionPerSecond, HungrySoundThreshold, HungrySound. There is NO
// CurrentHealth / MaxHealth field — those are on Pratfall's other body
// (HitPointsComponent etc.). FoodValue is read-only — use AddFood to change it.
// For "fill me up to max food":
if (hp != null) hp.AddFood((ushort)(hp.MaxFoodValue - hp.FoodValue));

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

**Use existing components — don't register your own into the component system.** The accessors and `ComponentType` enum are *generated code*: every entity class gets all 203 cached accessors stamped in at build time (e.g. `get_NetworkComponent` reads a `__internalNetworkComponent` backing field and calls `GetComponentRef` with the literal id `22084`), and the ids are auto-generated and sparse/hash-like (`EmissiveLightComponent = 135`, `PuppyColorComponent = 1083`, `NetworkComponent = 22084`). Mods can't run that generator, and minting your own `ComponentType` value risks colliding with a current or *future* game id. Dev-confirmed (Robert, #mod-dev 2026-06-10): it's easier not to use their component system for **adding** components — "obviously you can use the `RigidBody3DEntity` for example and existing components". For custom behavior, attach plain Godot nodes (`AddChild` from `ModInit`, or ship them in your scene — see [Two ways to run a node](#two-ways-to-run-a-node-scene-roottscn-vs-code-new--addchild), or [hook the game's lifecycle](#a-third-way-to-run-code-ilifecyclehandler-and-onstart) for `OnStart`/ordered ticks) and read game state through the existing accessors.

### `IComponent` implementors (203)

The components you might want to read/mutate on a `Player` or other entity. Categorized by name prefix:

**Player components (36)** — extension points for player behavior. `Player.LocalPlayer.<Name>` accessor is available for each via `IEntity`.

`PlayerAdvancedModeComponent`, `PlayerAmbientParticleComponent`, `PlayerAnimationComponent`, `PlayerCameraComponent`, `PlayerCatchComponent`, `PlayerCheckpointComponent`, `PlayerCollisionComponent`, `PlayerContactDamageComponent`, `PlayerCosmeticsComponent`, `PlayerCrownComponent`, `PlayerDamageAreaComponent`, `PlayerDistanceLightComponent`, `PlayerDropAdvantageComponent`, `PlayerEmoteComponent`, `PlayerFallDamageComponent`, `PlayerGoldAttractorComponent`, `PlayerHandSlotComponent`, **`PlayerHealthComponent`**, `PlayerHealthDrainComponent`, `PlayerHonkComponent`, `PlayerJourneyRecordComponent`, `PlayerLateJoinComponent`, `PlayerMaterialBootComponent`, `PlayerMeshComponent`, `PlayerMonitorComponent`, `PlayerMovementSoundComponent`, `PlayerPickaxeComponent`, `PlayerPingingComponent`, `PlayerProgressionComponent`, `PlayerReviveComponent`, `PlayerSkeletonComponent`, `PlayerSlideEffectComponent`, `PlayerSpectateOnDeathComponent`, `PlayerTeleportEffectComponent`, `PlayerToastComponent`, `PlayerUnconsciousComponent`.

**Interactable / item components (50)** — things the player can pick up or interact with. The `Interactable*` prefix is consistent.

`InteractableActivateFreezeBootComponent`, `InteractableAudioPlayerComponent`, `InteractableBatHornComponent`, `InteractableBatteryComponent`, `InteractableBounceComponent`, `InteractableCameraComponent`, `InteractableChameleonBranchComponent`, `InteractableColliderTrackingComponent`, **`InteractableComponent`** (base), `InteractableCrownComponent`, `InteractableDogHoleComponent`, `InteractableDogPoopComponent`, `InteractableDrillerComponent`, `InteractableDrillLauncherComponent`, `InteractableEmissionEnergyCurveComponent`, `InteractableExplosionComponent`, `InteractableExtractorComponent`, `InteractableFeedFoodToPlayer`, `InteractableFireworksComponent`, `InteractableFlareComponent`, `InteractableFoodVoiceModifierComponent`, `InteractableGravityComponent`, `InteractableGravityModifierComponent`, `InteractableGrenadeComponent`, `InteractableGunComponent`, `InteractableHealthPotionComponent`, `InteractableHolderComponent`, `InteractableLaserGunComponent`, `InteractableLoadLevelComponent`, `InteractableMegaphoneComponent`, `InteractableNodeVisibilityComponent`, `InteractableParticleComponent`, `InteractablePickupItemComponent`, `InteractablePlaySoundComponent`, `InteractableReviveAllComponent`, `InteractableScaleCurveComponent`, `InteractableSetUnconsciousComponent`, `InteractableShowCharacterEditorComponent`, `InteractableShowGameCustomizerComponent`, `InteractableShowInviteOverlayComponent`, `InteractableSpawnerComponent`, `InteractableSpinComponent`, `InteractableStartDrillerComponent`, `InteractableTeleporterComponent`, `InteractableThumperComponent`, `InteractableTreasureChestComponent`, `InteractableUnlockDogBallAchievementComponent`, `InteractableVendingMachineComponent`, `InteractableWinComponent`, `InteractableZiplineLauncherComponent`.

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

**Procedural / world (11)**: `BiomeFlagPoleComponent`, `ChangeLightOverTimeComponent`, `ChangeMaterialContinuouslyComponent`, `CloudRotationComponent`, `DistanceLightComponent`, `EmissiveLightComponent`, `FlickerLightSizeComponent`, `LightBlinkComponent`, `ProceduralCaveComponent`, `WorldEnvironmentBlendComponent`, `WorldEnvironmentSettingsComponent`.

**Animation / mesh (10)**: `AnimationTreePhysicsTickComponent`, `AlignWithVelocityComponent`, `BoneAttachmentSyncComponent`, `HatFallPropellerComponent`, `MeshRandomizerComponent`, `NodeRandomizerComponent`, `RotateComponent`, `RotateHolderComponent`, `ShowRandomMeshComponent`, `TalkAnimationBlendComponent`.

**Audio (2)**: `AudioPlayerComponent`, `CollisionSoundComponent`.

**Compatibility renderer (4)** — drive visual tweaks off the compatibility-renderer fallback path: `DisableVisibilityOnCompatibilityRendererComponent`, `IncreaseLightIntensityOnCompatibilityRendererComponent`, `IncreaseParticlesOnCompatibilityRendererComponent`, `SetShaderParamOnCompatibilityRendererComponent`.

**Lifecycle / spawn (11)**: `BossHealthComponent`, `BuriedMineComponent`, `CheckpointStatueComponent`, `DespawnWhenTooFarAwayComponent`, `EnableInteractableOnDugOut`, `EnemySpawnerComponent`, `FlagPoleItemSpawnComponent`, `PickupItemOnDeathComponent`, `SpawnEntityOnDeathComponent`, `SpawnGoldOnDeathComponent`, `SpawnMeshOnBounceComponent`.

**Misc / debug (43)** — everything that didn't fit a tighter category: `CoreDamageCrackComponent`, `CoreDeathComponent`, `CrownFloatComponent`, `DepthScoreComponent`, `DiggingMineComponent`, `DogBarkComponent`, `EmoteComponent`, `GamemodeSignComponent`, `GameOverOnAllPlayersDeadComponent`, `GameplayTagComponent`, `GenerateBlobOnContactComponent`, `GenerateBridgeTrajectoryComponent`, `GenerateSpikeOnBounceComponent`, `GoldCoinComponent`, `HealOnKillComponent`, `HitPointsComponent`, `HudMarkerComponent`, `IconComponent`, `ImposterReplaceComponent`, `InteractionComponent`, `InventoryComponent`, `InverseExplosionSnakeComponent`, `LineComponent`, `LoadLevelVolumeComponent`, `NoFallDamageAreaComponent`, `ParticleComponent`, `PickaxeHittableComponent`, `PuppyColorComponent`, `RandomBarkComponent`, `RandomScaleSeedPositionComponent`, `ReviveOnKillComponent`, `ScreenshotComponent`, `ShakeComponent`, `StickyComponent`, `StunAreaComponent`, `TestComponent`, **`ThrowFlareComponent`**, `TrackerHatNumberComponent`, `TreasureComponent`, `WaterBubbleComponent`, `WinGameOnDeathComponent`, `ZiplineComponent`, `ZiplineUserComponent`.

### Public interfaces (13)

- `IComponent` — every component implements this. 203 implementors (see above).
- `IEntity` — every entity implements this. 27 implementors (see [Entity hierarchy](#entity-hierarchy--ientity)). Exposes 203 component-accessor properties (one per `IComponent` subclass) plus a `Components` dictionary.
- `IGameEvent` — the payload type for `GameEventBus.SendEvent<T>(GameplayTag, T)`. Concrete event data lives in `GameEvent<T1>` … `GameEvent<T1,T2,T3,T4,T5,T6>` generic carrier types (just `(Value1, Value2, ...)` tuples) — Pratfall doesn't ship named per-event POCOs.
- `INetworkEvent` — payload type for `Network.EventManager.SendEvent`. Implementors are the per-event records (e.g. `CustomGameManager.CustomGameSettingsNetworkEvent`).
- `INetworkMessage` — payload base for the low-level **message** layer (`NetworkMessageManager`). A sibling of `INetworkEvent`, **not** its base: both extend `ISerializationCallbackReceiver`.
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
| `res://data/gameplay_tags/*.tres` | The 42 `GameplayTag` resources loaded by `GameplayTags` static cctor |
| `res://scenes/...` | `.tscn` scene files (rooms, prefabs) |
| `res://materials/...` | Godot material resources |
| `res://addons/...` | Godot editor addons (not loaded at runtime) |
| `res://tests/...` | Internal test scenes (not loaded by `TestRunner` in retail) |
| `res://...` (no folder) | A few root-level resources |

**Mod assets get mounted at `res://<YourModFolderName>/...`** — Pratfall's loader mounts each mod's `.pck` under its folder name. So if your mod folder is `MyMod`, your mod's `scene.tscn` lives at `res://MyMod/scene.tscn`. This is why folder names must be unique across all installed mods.

### Save-coupled arrays — don't mutate

*Some* config arrays are referenced from saved data by **index**, so adding or reordering entries repoints every existing player's saved choice and corrupts it. But this is **not** true of every index-array — verify against what the game actually persists before assuming. The authoritative source is the `Savegame` type's serialized fields (checked via Cecil against Steam build `23505941`); the only index-coupled save fields there are the cosmetic colors.

**Genuinely save-coupled — do NOT add/reorder:**

- `PlayerColorsConfig.Colors: Color[]` — `Savegame` stores **five** plain-`int` indices into this one list: `PlayerColorIndex`, `NoseColorIndex`, `ShirtColorIndex`, and (added in the 2026-06 big update, build 23570525) `PantsColorIndex` and `ShoeColorIndex`. Inserting or reordering colors repoints every player's saved cosmetic choice.

  *Also new in 23570525 (saved, but NOT index-coupled, so safe to ignore for mutation purposes): `CurrentGold` / `TotalCollectedGold` (scalar ints), `UnlockedVendingMachineCosmetics` and `SavedCharacterEditorPresets` (keyed by string/content), `HadFirstVendingMachineRoll`. These are the gold-economy / progression save fields — they don't couple to any config array. The older stat/unlock collections (`NewlyUnlockedStats`, `UnlockedStatConfigs`, `UnlockedCreatorCodes` — present since before the big update) are likewise content-keyed lists, not config-array indices.*

  *Build `23581753` adds one more save-safe scalar: `TotalWormsChasedAway` (`System.Int32`, the "Worms Chased Away" stat counter from the EOS-cross-play update). Like the gold scalars it's a plain int, not a config-array index — safe to ignore for mutation. (Cecil-verified: it sits beside `CurrentGold`/`TotalCollectedGold` in `Savegame`, and the five color indices were unchanged by the update.)*

**NOT save-coupled — safe to extend** (verified: no such index exists in `Savegame`, and dev-confirmed):

- `GameModeManager.Modes: GameModeBaseConfig[]` — **adding a game mode does not brick saves.** The selected mode is not written to the savegame; only the custom mode's last settings are saved (by content, not by a `Modes[]` index). The runtime `GameModeManager._currentModeIndex` is a replicated `NetworkVarByte` for multiplayer sync, not a persisted value. *(Dev-confirmed, Tim: "We do not save the game mode, we just save the last changes in the custom game mode. The index itself is not written to a savegame.")*
- `LevelManager.LevelPrefabs: PackedScene[]` — no level-prefab index is persisted in `Savegame` either.

**Separate concern — not about saves:**

- `ProceduralCaveComponent.BiomeGenerationConfigs` — mutating these doesn't corrupt saves, but it changes procedural generation, so a modded host and a vanilla client would **diverge in multiplayer**. Keep these identical across the lobby.
- `OptionsUIViewController.TabBarItems: OptionsContentUIViewBase[]` — reordering shifts the in-session options tab layout; it isn't persisted to saves.

Bottom line: adding a game mode or level is fine. For the cosmetic **color** arrays, prefer additive-by-content patterns (like `RandomWeightedDropPool.Pool` — see [drop pool recipe](#recipe-extend-a-drop-pool)) or accept that inserting shifts saved color indices.

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

There is no `--console` flag on Windows for Godot 4 to attach a live stdout console. Two practical workarounds:

1. **Launch from a terminal and redirect.** Run `Pratfall.exe > out.log 2>&1` from cmd / PowerShell, then tail `out.log` in another window. Steam's launch-options can take stdout redirection but it's finicky — direct-launch from the install dir is the reliable path.
2. **`override.cfg` for immediate stdout flush** (per Tim, in [the official modding guide](https://github.com/quad-head)). Create `override.cfg` next to `Pratfall.exe` with:
   ```ini
   [application]
   run/flush_stdout_on_print=true
   ```
   Forces Godot to flush every `GD.Print` to stdout immediately rather than buffering. Tim notes a performance impact — dev-only, remove before shipping.

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
- Test in a **2-player lobby** if your mod has any multiplayer behavior. Singleplayer doesn't exercise `Network.LobbyManager` properly — and per the [multiplayer-patterns disclaimer](#recipe-multiplayer-patterns), if you don't have explicit per-mod state sync, the lobby is your only way to know whether host-vs-client divergence ships.

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
| **Steam Workshop** | **Shipped 2026-05-18.** First-party path. | Auto-update + re-install across devices — but updates apply at **next launch, not live**: the native loader scans Workshop mods once at startup (`Setup` → `LoadAllModManifests` → `LoadAllModManifestsFromSteamWorkshop`; Cecil-verified — `OnItemInstalled` has zero callers, so there's no runtime reload), so a downloaded update takes effect on restart. Pratfall's native loader handles subscribe / install via `Steamworks.SteamUGC`, and `ModManifest` gained `IsSteamWorkshopMod` + `SteamWorkshopManifest` + `SteamWorkshopItem` properties so mod code can detect Workshop sourcing. **Caveat: Chinese players may not have Workshop access** (Robert) — consider this if your mod targets that audience. |
| **Nexus Mods** | De facto current host; works today | Manual install only — users download a zip and drop the mod folder into `<GameDir>\mods\`. No auto-update. |
| **Thunderstore** | Community exists; rep (Ebkr) is engaged with the Pratfall team | Standard format for BepInEx-style games (Risk of Rain 2, Lethal Company, REPO, Content Warning). Pratfall is on Godot+C# which is uncommon for the platform, so existing tooling (r2modman) doesn't natively understand the loader yet. |
| **GitHub release / direct download** | Universal fallback | Works for any platform Pratfall runs on. Reasonable for early development; not a great long-term distribution channel. |

**Fragmentation matters.** Ebkr (Thunderstore) flagged the risk of mods splintering across platforms — if your players use one platform and your dependencies are on another, the install path breaks. With Steam Workshop live as of 2026-05-18 it's the natural first-party choice for most mods; supplement with Nexus or direct download for players who can't access Workshop. If you publish on multiple, link cross-platform so users can find the same mod from anywhere.

**Pratfall's uncommon stack matters too.** Tim noted that Godot + C# is rare among modded games, so tooling assumptions made for Unity+BepInEx don't always transfer. If you write a Thunderstore-format manifest, expect to also explain manual install for users whose mod manager doesn't auto-handle Pratfall yet.

### Uploading to Steam Workshop

Pratfall ships **`SteamWorkshopUploader.exe`** in the `<game>/mods/` folder. It's a small native CLI tool (~800 KB) that:

1. Auto-detects mod folders in its own directory (every sibling of the `.exe` is a candidate).
2. Prompts you to pick one by number.
3. Walks you through the create-or-update flow with the Workshop terms-of-service link first.

To use it:

```
cd <game>/mods/
SteamWorkshopUploader.exe
```

Output starts with:
```
By submitting this item, you agree to the workshop terms of service: http://steamcommunity.com/sharedfiles/workshoplegalagreement
Select a mod directory:
1. example_mod
Enter number to upload mod:
```

The tool takes no CLI args — interaction is purely via stdin. On first upload it creates the Workshop item; subsequent runs against the same folder update the existing item (it tracks the Workshop ID per-mod, likely written back into the mod folder somewhere).

**Constraints:**
- The mod has to live under `<game>/mods/` to be detected by the uploader. Mods installed under `%APPDATA%\Pratfall\mods\` or other framework-supported locations need to be copied / symlinked to `<game>/mods/` before upload.
- Steam must be running and you must be signed in.
- The first upload creates a new Workshop item under YOUR Steam account; subsequent uploads update it. You can't transfer ownership.

### Steam Workshop preview image

The uploader auto-picks up a preview image from your mod folder if you ship one. Per [Tim's modding guide](https://github.com/quad-head):

- Name: `Preview.png`, `Preview.jpg`, or a similar variant (the tool detects common image filenames)
- Location: top of your mod folder, next to `manifest.json`
- Size limit: **1 MB** (Steam Workshop hard cap)
- Aspect: Steam Workshop thumbnails render at roughly 4:3 in the storefront; square (1:1) works too. Use 512×512 or 600×600 for a sharp result without bloating the file size.

If you ship no preview image, your Workshop listing falls back to a default placeholder until you upload one — which means a worse first impression in the Workshop browser. Always include one.

## Pitfalls

*Quick-reference recap. Most of these are covered in depth in [Lifecycle](#lifecycle), [Godot 4 concepts](#godot-4-concepts), and the [decoded surface inventory](#decoded-pratfall-surface-inventory) — this list is the at-a-glance version.*

- **Folder names must be unique across mods.** Pratfall mounts each mod's PCK at `res://<DirectoryName>/...`. Two mods sharing a folder name silently overwrite each other's assets. (Confirmed by Tim in #mod-dev, 2026-05-17.)
- **Filesystem URIs vs paths.** `Game.Platform.GetUserDataPath()` returns a Godot `user://` URI on Steam. Pass it through `ProjectSettings.GlobalizePath(...)` before any `System.IO` call. Godot's own `DirAccess` understands the URI, so game-side code paths work without it — but System.IO does not.
- **Mind the one genuinely save-coupled array.** `PlayerColorsConfig.Colors` is indexed by save data (`Savegame.PlayerColorIndex` / `NoseColorIndex` / `ShirtColorIndex` / `PantsColorIndex` / `ShoeColorIndex` — five indices as of build 23570525) — inserting/reordering colors invalidates existing players' saved cosmetics. `GameModeManager.Modes` and `LevelManager.LevelPrefabs` are **not** saved by index — adding a game mode or level is safe. See [Save-coupled arrays](#save-coupled-arrays--dont-mutate).
- **`ByteBufferWriter` has a 32 KB string cap.** Affects any custom network protocol built on top of `Network.EventManager.SendEvent`. Keep payloads under 32 KB after JSON serialization.
- **`Game.Config` is a value-type struct with `init`-only setters.** Mods cannot mutate config flags at runtime — not even via reflection (the `modreq(IsExternalInit)` modifier enforces this at the C# language level, and even reflection-based hacks would write to a copy because `Game.Config` returns the struct by value). If `Game.Config.AllowUserLocalization == false` on the shipped build, your mod can't flip it; either ship a JSON-only locale that side-loads via `TranslationServer.AddTranslation` directly, or wait for the dev to enable the flag.
- **`GameplayTag` vs `Constants.EventId*` are different systems.** `GameplayTags.X` references are for `GameEventBus.SendEvent` / `OnGameEventReceived` (the high-level pub/sub — `GameEventBus` IS a singleton with `Instance`, but the event itself is static). `Constants.EventId*` are `const ushort` numeric IDs (values like `129`, `115`) for `Network.EventManager.SendEvent(UInt16 eventId, ...)` (the low-level network event channel — `NetworkEventManager` is an *instance* accessed through the `Network` singleton, NOT a static class). Don't mix them — subscribing to a `GameEventBus` handler hoping a numeric event id will match would match nothing.
- **Don't mint your own `ComponentType` / component-system entries.** The `Entity.XxxComponent` accessors are build-time generated code with auto-generated sparse ids — mods can't run the generator, and invented ids can collide with current or future game components. Use the existing accessors freely; ship custom behavior as plain Godot nodes. (Dev-confirmed by Robert in #mod-dev, 2026-06-10.) See [Entity hierarchy & `IEntity`](#entity-hierarchy--ientity).
- **`ModEntry` class name is exact.** Pratfall uses `assembly.GetType("ModEntry")` — case-sensitive, no namespace. See [Lifecycle](#lifecycle).
- **`ModInit` / `ModDestroy` reentrance.** Mods can be enabled → disabled → enabled multiple times per session. Make both methods idempotent: every subscription paired with an unsubscribe, every array growth paired with a shrink.
- **`AssemblyLoadContext.Unload()` is called on disable.** Don't hold long-lived references to game types in static fields outside the mod's `ModEntry` — the GC needs to collect your assembly's load context.
- **HUD-attached singletons are null on the main menu.** `ButtonPrompBarController.Instance` and similar HUD pieces are only present during gameplay. Null-check before use.
- **Don't allocate in `_Process` / `_PhysicsProcess` hot paths.** These run every frame / every physics tick. Allocating boxes / temp lists / closures generates GC pressure that Pratfall already instruments (`GcTimingListener.OnGcTiming` event for measuring; `GcManager.Instance` for blocking GC during sensitive sections — note `GcManager` is a *blocker*, not an event source, despite its name). Pool what you can, cache lookup results, and prefer `for` over LINQ on per-frame code.
- **Don't touch Godot objects from a background thread.** Godot 4's C# API is main-thread-only — calling `node.Position = ...` or `resource.Duplicate()` from a `Task.Run` / timer thread crashes silently or corrupts state. If you have off-thread work, marshal back via `MainThreadDispatcher.Instance.Enqueue(() => { /* main-thread code */ })`.
- **Don't `[GlobalClass]`-collide with a game type name.** Godot 4 has a global class registry shared between the game and your mod's auto-registered types (the loader runs `LookupScriptsInAssembly` on every mod assembly). If your `[GlobalClass] class Player : Node3D { }` collides with Pratfall's own `Player`, registration loses silently and your scenes won't instantiate it. Namespace your `[GlobalClass]` types or use unambiguous names.
- **Don't open mod `.tscn` files outside Pratfall's decompiled Godot project.** Opening a `.tscn` that references `Pratfall.dll` types in a fresh Godot editor instance will offer to "fix missing dependencies" and silently strip references to game types. You'll save the file and your scene will be missing every Pratfall-specific node. Either work inside a Godot project that has Pratfall's types available, or edit `.tscn` files as text only.
- **Don't ship other mods' DLLs inside your mod folder.** Two copies of the same assembly in two different `AssemblyLoadContext`s create type-identity confusion (`typeof(X)` from one ALC isn't equal to `typeof(X)` from the other). Declare your dependencies in your README; let users install them separately.

## Resources

- **Tim's example mod** — [`quad-head/pratfall-example-mod`](https://github.com/quad-head/pratfall-example-mod) — the canonical reference.
- **Robert's infinite-flare-mod** — [`quad-head/pratfall-infinite-flare-mod`](https://github.com/quad-head/pratfall-infinite-flare-mod) — a small, focused real-world example (one Harmony patch that overrides a single value). Good first read after the canonical example to see what a minimal shipped mod actually looks like.
- **Discord** — `#mod-dev` channel of the Pratfall dev server (Tim, Robert, and active modders coordinate there).
- **The Pratfall Mod Framework** — [MOD_AUTHORS_GUIDE_FRAMEWORK.md](MOD_AUTHORS_GUIDE_FRAMEWORK.md) — adds a safety gate, IL scanner, multiplayer sync, and helpers that wrap the patterns in this guide.
