# Pratfall Mod Framework

Runtime mod framework for [Pratfall](https://store.steampowered.com/app/4244510/Pratfall/) (Godot 4.6.1 C#).

## Install (players)

1. Quit Pratfall.
2. Download the latest `PratfallModFramework-Installer-vX.Y.zip` from the [**Releases**](https://github.com/unexpear/APratfallModFramework/releases/latest) page and unzip it anywhere.
3. Run `PratfallModFramework.Installer.exe`. It auto-detects your Pratfall install via Steam.
4. Click **Install**. The installer backs up `Pratfall.dll` to `Pratfall.dll.original` before patching, so it's fully reversible — click **Uninstall** any time, or run Steam Verify and Steam will restore the original automatically.
5. Launch Pratfall. The native Pratfall **Mod** button (added in Pratfall 1.1.0.R2943) is hidden, and the framework's **Mods** button takes its slot.

To install a mod: drop its folder into `%APPDATA%\Pratfall\mods\<modid>\` and it'll appear in the Mods dialog on next launch. Steam Workshop subscriptions also work — the framework discovers them automatically (at startup, and live mid-session when you subscribe from inside Pratfall).

**v1.1: Mods stay disabled until you check them.** Each mod card has two icons next to its toggle:
- **ℹ Info** — opens the read-only manifest / file list / declared patches viewer.
- **🔍 Scan** — runs the IL safety scanner (Mono.Cecil), reports calls to dangerous APIs (Process, raw network, registry, P/Invoke, code generation, file deletion), and marks the mod as user-checked.

A mod is enabled when you flip its toggle ON, click 🔍, or accept Download in a multiplayer prompt. Updating a mod (any DLL or PCK byte change) re-locks the gate.

## Writing mods

Two focused guides, pick the path that matches your distribution model:

- **[MOD_AUTHORS_GUIDE_VANILLA.md](MOD_AUTHORS_GUIDE_VANILLA.md)** — for mods targeting **just Pratfall + Tim's official loader**, no framework dependency. Setup, the `ModEntry.ModInit`/`ModDestroy` lifecycle, recipes for Harmony patches / localization / save data / game events / button prompts / drop pools / custom Godot types, plus the full Cecil-verified Pratfall surface inventory (73 singletons, 22 static helpers, 26 configs, 11 events, 56 network EventIds, 40 GameplayTags, 184 IComponent types, the arrays you should NOT mutate) and pitfalls.

- **[MOD_AUTHORS_GUIDE_FRAMEWORK.md](MOD_AUTHORS_GUIDE_FRAMEWORK.md)** — for mods using **this framework's helpers**. Same recipe set but using the wrappers (`[ModPatch]`, `ModLocalizationHelper`, `ModSaveDataHelper`, `ModGameEventHelper`, `ModButtonPromptHelper`, `ModDropPoolHelper`) plus framework-specific manifest fields (multiplayer mode, requires/conflictsWith, assemblySha256 pinning) and behavior you should understand (the user-check gate, fingerprint re-locks on update).

Minimal sample mod (Harmony patch via `[ModPatch]`): [`sample-mods/HelloWorldMod/`](sample-mods/HelloWorldMod).

## Build from source (devs)

Requires .NET 8 SDK. From the repo root:

```
dotnet build framework/PratfallModFramework.sln -c Release
```

The Release build artifacts land in `framework/Installer/bin/Release/net8.0-windows/` — the installer EXE plus the framework + bootstrap DLLs it needs.

## What it provides

- bootstrap injection into the game (Cecil patches `GcManager._Ready()`, original `Pratfall.dll` backed up first)
- runtime DLL mod loading with Harmony patches
- a Pratfall-styled Mods UI in the main menu for enable/disable, persists across game ↔ menu
- a real Pratfall-backed multiplayer control plane for:
  - installed + enabled mod snapshots
  - host-authoritative mod votes
  - session-state reconciliation
- chunked P2P mod transfer (DLL + optional PCK side-file) with SHA-256 verification, order-independent reassembly, round-robin scheduling for fairness across concurrent transfers; manifest.json is written from the cached peer snapshot so the receive-side rescan can find the mod
- transfer is **never automatic** — when the lobby votes to enable a mod the local player doesn't have, an in-game prompt asks: **Download** (transfer the files), **Use settings only** (stretch — only when the mod supports it), or **Decline (leave lobby)**. The framework calls `LeaveLobby` on decline so the session doesn't drift out of sync.
- per-event peer authentication (claimed sender must be in the current lobby member list)
- automatic compatibility checking across the union of local + every known peer's mod set, fired on every state change
- conflict-resolution dialog when two locally-enabled mods declare each other incompatible — pick which one stays, loser is disabled and persisted
- *(trust modes / quarantine were removed in favor of the explicit per-peer acquisition prompt — every download is now a deliberate user choice, so there's nothing to silently auto-accept)*
- PCK loading for asset mods
- bubble around the game's official mod loader so framework state + official-style mods coexist
- offline-gated debug peer for solo testing without a second PC

## Current Status

### Verified solo (passes the local smoke + stress suite)

- Framework loads, Mods button injects, dialog opens with the correct rocky-frame styling
- Toggle persistence across launches via `user://modframework-state.json`
- Vote system fires from the park (offline session-start), not the main menu
- Official-loader bubble integrates cleanly with Tim's `example_mod`: toggling on + Load Enabled Mods → game's `ModEntry.ModInit()` runs, log confirms `Mod initialized!`
- Transfer chunker / reassembler / SHA-256 / disk write — full loopback pass, including reversed delivery, duplicate chunks, and 5 boundary payload sizes
- Tampered chunk mid-stream → receiver returns `FailedHashMismatch` (rejected before persist)
- Trust quarantine: trusted-only mode + unknown hash routes to `user://mods-quarantine/<id>/<id>.dll`
- Concurrent transfers (3 simultaneous) interleave fairly via round-robin scheduling, no chunk cross-talk
- Vote consensus math: 10 scenarios across 2/3/4 peers including ties (tied = fail, by contract)
- Compatibility checker detects all 5 issue categories on synthetic fixtures
- Conflict-resolution prompt fires on real conflicting mods, disables loser, syncs the dialog toggle visually

### Known limitations / waiting items

- **No real multiplayer test yet.** All transfer / vote / member-join behavior is verified by solo loopback or debug peer, never by two real Steam clients. Audit cleared the wire format against `Pratfall.ByteBufferWriter`'s 32 KB cap (chunks sized to 14 KB raw → ~20 KB JSON envelope).
- (was: `ModManager.EnableMod` second-call returns false — *no longer relevant*. The 2026-05-18 Pratfall update + framework turn-off means we no longer call the native `ModManager.EnableMod` at all; our own loader pipeline handles enable/disable. The native bug Tim was going to fix doesn't affect us anymore.)
- (was: Workshop integration is stubbed — *now shipped*. `WorkshopSubscriber` hooks `Steamworks.SteamUGC.OnItemInstalled` directly. Subscriptions at startup are discovered by `ManifestManager.ScanWorkshopMods` walking every Steam library; live subscribes mid-session fire the SteamUGC event → framework rescans → new mod appears in the Mods dialog without a restart. User still has to click 🔍 to approve before it loads.)
- (was: PCK side-file transfer not implemented — *now shipped*. The host sends both `<modId>.dll` and `<modId>.pck` (when the manifest declares `pckFile`), and the receiver also writes `manifest.json` next to them from the cached peer snapshot, so the rescan finds the mod end-to-end. v1.1: PCK bytes now contribute to the user-approval fingerprint, and the receive flow waits for both files before marking-checked + enabling.)
- **Stretch end-to-end** still needs a real multiplayer lobby to verify; the apply path is implemented and unit-clean.
- (was: Optional malware scan before first enable — *now shipped in v1.1* as the **🔍 IL safety scanner**. Mono.Cecil walks the mod's DLL statically and reports calls to dangerous APIs — Process.Start, raw sockets, HttpClient, Registry, Reflection.Emit, P/Invoke, file deletion, environment probes. Findings show severity (Danger/Warning/Note), the API called, and the call-site method. Running 🔍 also marks the mod's fingerprint as user-checked, releasing the v1.1 user-check gate.)

### Resolved with the dev (Tim, Robert)

- **Network event IDs `62000-62005`** — confirmed safe forever (game counts up from low numbers, never approaches that range).
- **Custom-loader approach blessed (replaces the earlier bubble approach).** The 2026-05-18 Pratfall update added Steam Workshop + extensive ModManager restructuring; Tim's accompanying Discord note was *"update with the fixes for modding is live so it should be easy to add a custom mod loader."* The framework now Harmony-patches `ModManager.LoadAllModManifests` to skip native discovery entirely and runs its own pipeline (scan + load + lifecycle + Workshop discovery). The earlier "bubble around `enabled_mods.json`" architecture was retired — it depended on private internals that got renamed/privatized in the update.
- **`RegisterCustomType` for runtime Godot Node registration** — withdrawn, not realistic. Robert's recommended pattern is to load `RandomWeightedDropPool` resources and add to the array directly. `ModDropPoolHelper.Register(poolPath, scene, weight)` and `RegisterIn(pool, scene, weight)` implement that pattern with proper unregister-on-unload.
- **Analytics suppression for mod exceptions** (Robert's request) — `ModExceptionFilter` is in place and active.

### Public template

[`sample-mods/HelloWorldMod/`](sample-mods/HelloWorldMod/README.md) — minimal DLL mod with `OnLoad`/`OnUnload` and a build target that copies output into `%APPDATA%\Pratfall\mods\HelloWorldMod\`.

## Native ModManager Relationship

As of 2026-05-18, the framework **turns off Pratfall's native ModManager** and runs as the sole mod loader. The earlier "bubble around `enabled_mods.json`" approach was retired when Tim's Workshop + modding-fixes update privatized methods we depended on (`GetModManifest` → `GetModManifestFromDirectory`, made private) and Tim explicitly invited custom mod loaders.

What the framework does to the native ModManager:

- Harmony-prefixes `ModManager.LoadAllModManifests(Action onComplete)` to skip native discovery + auto-load. Still invokes `onComplete` so dependent game code doesn't hang.
- Harmony-prefixes `ModManager.ReadLoadedModsFromFile` to return empty (defense-in-depth against anything else still calling it).
- Harmony-prefixes `ModManager.WriteLoadedModsToFile` to no-op.
- Lets `Steam.SetupWorkshopCallbacks` and `CreateModDirectory` run normally (those are infrastructure setup we want).

What the framework does **for** the native ModManager (state Pratfall still reads):

- Harmony-prefixes `ModManager.get_EnabledModCount` to return our framework's enabled count. `SpeedrunManager.SubmitTimeToLeaderboard` reads this to gate leaderboard submissions (anti-cheat); without the bridge, modded runs would slip through with `count=0`. `GameOverUIController.Show` also reads it for display.
- Harmony-prefixes `ModManager.get_ShouldHideModLoaderUi` to always return true so Pratfall's native ModButton in the main menu is hidden (the framework's Mods button replaces it).

What the framework does for mod discovery (entirely on our own):

- Scans `user://mods/` and `<game>/mods/` for `manifest.json` files.
- Scans every Steam library folder (registry + `libraryfolders.vdf`) under `steamapps/workshop/content/4244510/<workshopid>/` for Workshop mods. Tags each with `IsSteamWorkshopMod=true` + parsed `WorkshopId`.
- Subscribes to `Steamworks.SteamUGC.OnItemInstalled` (via a Harmony postfix on `Steam.SetupWorkshopCallbacks` + a direct subscribe in case the postfix never fires) so newly-subscribed Workshop mods appear in the Mods dialog without a game restart.

Manifest schema acceptance:

- **Framework schema** (`id`, `name`, `version`, `type`, `effects`, `multiplayer`, etc.) — full feature set.
- **Pratfall native schema** (`Name`, `Assembly`, `PackageName`, `AutoLoad`, `AddAssemblyToGodot`) — parsed identically; the framework synthesizes a missing `id` from `Name` / folder name. Workshop mods typically ship this schema.
- `AutoLoad: true` is **deliberately ignored** by the framework — every mod stays disabled until the user explicitly enables + 🔍 approves. The user-check gate is the framework's only defense against unreviewed code execution; auto-load would bypass it.

Canonical desired-state remains stored in `<userData>/modframework-state.json` (enabled mod IDs + approved fingerprints). The native `enabled_mods.json` is read once on first state-load as a back-compat migration path; otherwise ignored.

Discovery scope is intentionally limited to Pratfall-relevant roots:

- the Pratfall install folder's `mods/`
- `user://mods/` (Godot user-data → `%APPDATA%\Pratfall\mods\`)
- Steam library `steamapps/workshop/content/4244510/` folders
- optional explicitly configured Pratfall profile roots

The framework never scans unrelated folders on the machine.

## Quick Start

1. Download the latest installer zip from the [Releases page](https://github.com/unexpear/APratfallModFramework/releases/latest), unzip it, and run `PratfallModFramework.Installer.exe` inside.
2. Run it. It patches `Pratfall.dll` and copies framework files.
3. Launch Pratfall from Steam.
4. Place mods in `<Pratfall>\mods\<ModName>\`.

Expected package shape for DLL mods:

```text
mods/MyMod/
|- manifest.json
`- MyMod.dll
```

Manifest-only mods are also valid when they do not require a DLL, such as explicit `stretch`/settings packages with no patches.

## Multiplayer Model

The framework reconciles **session state**, not code compatibility.

That means it tries to answer:

- do both players have the mod installed?
- is the version the same?
- is it enabled on both sides?
- does it declare conflicts or dependencies?

Then it drives the session toward one of these outcomes:

1. `already-installed local match`
   - both sides already have the same DLL mod installed
   - vote only decides whether to enable/disable for the session
2. `stretch`
   - settings/state-oriented mods
   - no file transfer
3. `transfer`
   - used only when a required mod is truly missing
4. `restart_required`
   - safe to stage, not safe to hot-apply
5. `disable/reject`
   - when equal session state cannot be reached safely

The framework does **not** try to merge incompatible code patches automatically.

## Local Debug Peer Mode

For solo testing, the framework can simulate one extra peer offline.

- This is **opt-in**.
- It only activates when there is **no real multiplayer lobby**.
- Real Pratfall/Steam lobby transport still takes priority when available.

Create `user://modframework-debug-peer.json` with:

```json
{
  "enabled": true,
  "mirrorLocalInstalledManifests": true,
  "enabledModIds": [],
  "defaultVoteYes": true,
  "voteResponses": {
    "SomeSpecificMod": false
  }
}
```

Behavior:

- `mirrorLocalInstalledManifests: true`
  - the simulated peer advertises the same installed non-`local_only` manifests as the local player
  - this is the easiest way to test the `already-installed local match` vote path
- `enabledModIds`
  - which of those installed mods the simulated peer starts with enabled
- `defaultVoteYes`
  - default answer from the simulated peer when the host sends a vote
- `voteResponses`
  - optional per-mod vote override map

This mode is for testing `compare -> vote -> apply` logic on one machine.
It does **not** simulate real chunked transfer.

## For Mod Developers

Public starter template at [`sample-mods/HelloWorldMod/`](sample-mods/HelloWorldMod/README.md). It builds against the framework, has a working post-build target that drops the DLL + manifest into your user mods folder, and is intentionally minimal — copy it, rename, add your own logic.

Two helpers worth knowing about:

- `ModDropPoolHelper.Register(poolResPath, scene, weight)` — the recommended pattern for content mods (per Robert on the dev side). Loads a `RandomWeightedDropPool` resource, appends a `RandomWeightedScene` entry, returns an `IDisposable` that removes it on dispose. Call from `OnLoad`, dispose from `OnUnload` and the pool is left exactly as it was.
- `WorkshopHook.OnWorkshopItemInstalled` — fires when Steam Workshop drops or updates a mod folder. The framework wires it from `WorkshopSubscriber`'s direct Steamworks SDK subscription (live mid-session subscribes appear immediately in the Mods dialog without a restart). Mod authors generally don't need to subscribe — the framework's own pipeline handles it. Available if you have a mod-management use case that needs the raw event.

### Official Loader Manifest

The game's built-in loader currently uses a different startup manifest shape than the framework's richer multiplayer manifest.

Official startup-loader example:

```json
{
  "Name": "Example Mod",
  "Version": "1.0.0",
  "Description": "Adds cool new gameplay features",
  "Author": "Tim",
  "PackageName": "ExampleMod.pck",
  "Assembly": "ExampleMod.dll"
}
```

The framework parses both shapes. After the 2026-05-18 turn-off of Pratfall's native ModManager, the distinction is essentially cosmetic — all mods load through the framework's own pipeline regardless of which schema their manifest uses.

Behavior for Pratfall-native-schema mods (Workshop downloads + any older mods that ship `Assembly`/`PackageName` instead of `id`/`type`):

- They are parsed and shown in the framework's Mods dialog like any other mod.
- They use `DirectoryName` as the implicit fallback `id` when no framework `id` exists.
- `AutoLoad: true` is **ignored** (see [Native ModManager Relationship](#native-modmanager-relationship)) — every mod stays disabled until the user enables + 🔍 approves.
- The Pratfall-loader-only delayed-apply via Load-Enabled-Mods / Host Game / Play Offline no longer exists — the framework loads mods immediately on user enable, just like framework-schema mods.

### `manifest.json`

```json
{
  "id": "MyMod",
  "name": "My Mod",
  "version": "1.0.0",
  "author": "You",
  "description": "What it does",
  "type": "patch",
  "effects": {
    "settings": [],
    "patches": ["MainMenuUIViewController._Ready"],
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

`type` is kept for backward compatibility and coarse fallback behavior.

`multiplayer.mode` is the explicit session-sync contract:

- `local_only`
  - never negotiated with peers
  - best default for new DLL mods until you know more
- `stretch`
  - sync over existing settings/network state
  - no file transfer
- `transfer`
  - peers may need the mod package to match
- `restart_required`
  - safe to stage, not safe to hot-apply
- `auto`
  - framework infers mode from `effects` and `type`

Recommended rule for mod authors:

- default to `local_only`
- only opt into `stretch`, `transfer`, or `restart_required` when you can describe the mod honestly

For `stretch`, use explicit settings objects when you expect the framework to apply values:

```json
{
  "type": "settings",
  "effects": {
    "settings": [
      { "name": "LowGravity", "value": 1 },
      { "name": "FallDamage", "clear": true },
      { "name": "UseFixedSeed", "value": 1 }
    ],
    "fixedSeedString": "12345",
    "patches": [],
    "nodes": [],
    "assets": [],
    "needsRestart": false
  },
  "multiplayer": {
    "mode": "stretch"
  }
}
```

Notes:

- string-only entries like `"settings": ["LowGravity"]` are still accepted for classification
- real stretch application requires an explicit `value` or `clear`
- `fixedSeedString` is applied directly and syncs through Pratfall's existing custom-settings event path

### Writing a Mod

Your DLL references `PratfallModFramework.dll` and any Pratfall game assemblies needed for the types you patch, then uses `[ModPatch]` to declare patches.

Minimal entry point:

```csharp
using Godot;
using PratfallModFramework;

public static class MyModMain
{
    public static void OnLoad()
    {
        GD.Print("[MyMod] Loaded");
    }

    public static void OnUnload()
    {
        GD.Print("[MyMod] Unloaded");
    }
}
```

Example patch:

```csharp
using PratfallModFramework;

[ModPatch(typeof(MainMenuUIViewController), "_Ready", PatchType.Postfix)]
public static class MainMenuPatch
{
    static void Postfix()
    {
        // run code after the main menu becomes ready
    }
}
```

The framework handles Harmony setup and teardown automatically.

## Design Constraints

Important current constraints:

- runtime registration of arbitrary new C# Godot node types should be treated as unavailable
- content mods should prefer:
  - resources
  - `PackedScene`
  - existing game systems
  - Harmony patches where needed
- DLL mods are arbitrary code
  - the framework can add trust checks and warnings
  - it cannot provide a real in-process sandbox for untrusted DLLs

## Security Note

This framework loads user-provided DLLs into the game process.

That means DLL mods should be treated as fully trusted code from the player's point of view.

Trust layers shipped:

- **No automatic downloads** — when the lobby agrees on a mod you don't have, the framework asks you per-mod: Download / Use settings only (stretch, when applicable) / Decline (leave lobby). Replaces the old "trusted-only quarantine" UX with a direct user choice every time.
- **Hash tracking** — manifests can pin `assemblySha256`. The framework refuses to load a DLL whose actual hash differs.
- **Peer authentication** — every framework network event is dropped if the claimed sender isn't a current lobby member.

Not shipped (low priority given lobbies are friends-only):

- Malware scan before first enable.

These are trust layers, not a real code sandbox.

## Building from Source

Requires the .NET 8 SDK.

```text
cd framework
dotnet build
```

Projects:

- `framework/PratfallModFramework/`
  - core framework DLL
- `framework/PratfallModFramework.Patcher/`
  - Mono.Cecil patcher for `Pratfall.dll`
- `framework/PratfallBootstrapLoader/`
  - zero-dependency loader injected by the patcher
- `framework/Installer/`
  - single-file GUI installer

## Architecture

```text
Pratfall.exe (patched by Cecil to call Bootstrap.Init on GcManager._Ready)
`- PratfallModFramework.dll
   ├─ Bootstrap                  entry point + startup status banner
   ├─ ModManager                 orchestrator: scan, load, enable/disable, vote, transfer
   ├─ ModAssemblyLoader          AssemblyLoadContext + Harmony, with SHA-256 hash gate
   ├─ ModManifest                dual-shape parser (framework + official-loader formats)
   ├─ ManifestManager            mod-folder scanner
   ├─ FrameworkModStateStore     persistent desired-enabled state (user://modframework-state.json)
   ├─ MainMenuIntegration        Mods button + dialog + conflict-resolution prompt
   ├─ ToggleSwitch               custom toggle widget used in the dialog
   ├─ ModNetworkLayer            Pratfall network bindings (events, lobby member auth)
   ├─ ModNetworkContracts        wire types: manifest snapshot, vote, transfer chunk
   ├─ ModVoteSession             tally + tie-break (yes > no, ties = fail)
   ├─ ModP2PTransfer             chunker, reassembler, round-robin scheduler
   ├─ ModNetworkStretch          settings-mode apply path (CustomGameSettings)
   ├─ ModCompatibilityResolver   peer-vs-local diff for vote/transfer planning
   ├─ ModCompatibilityChecker    local-set + union check (auto-runs on every state change)
   ├─ ModDropPoolHelper          register/unregister entries on RandomWeightedDropPool
   ├─ ModFrameworkSelfTest       public test API used by tmp/stress-mods/
   ├─ ModExceptionFilter         keeps mod exceptions out of game analytics
   ├─ SessionStartHooks          Harmony patches on Host Game / Play Offline
   ├─ OfficialModBridge          patches game's ModManager.Read/WriteLoadedModsToFile
   ├─ NativeDialogBridge         calls into the game's DialogUIViewController
   ├─ VoteUI                     vote prompt (uses native dialog when available)
   ├─ WorkshopHook               public OnWorkshopItemInstalled event for mods that want raw Steam callbacks
   ├─ WorkshopSubscriber         Harmony postfix on Steam.SetupWorkshopCallbacks + direct Steamworks.SteamUGC subscribe
   ├─ NativeModUiSuppressor      hides native ModButton + bridges EnabledModCount for anti-cheat
   ├─ OfficialModBridge          turns OFF native ModManager (LoadAllModManifests/Read/Write all neutered)
   └─ ModAPI                     thin public-API surface for mods
```
